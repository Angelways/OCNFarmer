using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace NorthIslandChestPlugin;

/// <summary>
/// 初始水晶旁远程货币兑换（Keita 同款）：EventStart(玩家) → AgentShop 下单 → EventComplete。
/// 支持北征终极固定剂、南/北征古旧钱箱。购买时内部暂停挂机，不发 /ocnstop。
/// </summary>
internal sealed unsafe class CurrencyExchangeBuyer : IDisposable
{
    private const int CurrencyStackCap = 9999;
    private const int SessionTimeoutMs = 10_000;
    private const int ConfirmTimeoutMs = 5_000;
    private const int RetryCooldownMs = 30_000;
    private const int ExchangeSpacingMs = 250;
    private const int WindowCleanupMs = 1_500;
    private const float InitialCrystalRadius = 10f;
    private const uint FixativeItemId = 51978;
    private const uint CofferItemId = 47740;

    private enum Phase
    {
        Idle,
        PausingFarmer,
        Active,
        ResumingFarmer,
    }

    private readonly record struct ExchangeSpec(
        string CurrencyName,
        uint CurrencyItemId,
        uint EventId,
        int Cost,
        string RewardName,
        uint RewardItemId);

    private readonly record struct ExchangeRequest(ExchangeSpec Spec, int Quantity);

    private readonly Plugin plugin;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;

    private readonly Queue<ExchangeRequest> queue = new();
    private readonly Dictionary<(uint CurrencyId, uint RewardId), long> retryAfter = new();

    private Phase phase = Phase.Idle;
    private bool resumeFarmer;
    private bool anySuccess;
    private string status = "空闲";
    private long startedAt;
    private long windowCleanupUntil;

    private ExchangeRequest? pending;
    private uint pendingCurrencyBefore;
    private uint pendingRewardBefore;
    private long pendingActionAt;
    private long pendingDeadline;
    private bool pendingConfirmClicked;
    private long nextExchangeAt;
    private uint activeEventId;
    private bool eventCompleted;

    internal bool IsBusy => phase is not Phase.Idle;
    internal string Status => status;

    internal CurrencyExchangeBuyer(
        Plugin plugin,
        IClientState clientState,
        IObjectTable objects,
        ICondition condition,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IPluginLog log)
    {
        this.plugin = plugin;
        this.clientState = clientState;
        this.objects = objects;
        _ = condition;
        this.gameGui = gameGui;
        this.addonLifecycle = addonLifecycle;
        this.log = log;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "ShopExchangeCurrency", OnShopAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ShopExchangeCurrency", OnShopAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "ShopExchangeCurrencyDialog", OnShopDialogAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, "ShopExchangeCurrencyDialog", OnShopDialogAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", OnConfirmAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreDraw, "SelectYesno", OnConfirmAddon);
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(OnShopAddon);
        addonLifecycle.UnregisterListener(OnShopDialogAddon);
        addonLifecycle.UnregisterListener(OnConfirmAddon);
        CompleteSession();
        queue.Clear();
        CloseShopWindows();
    }

    internal bool TryBegin(bool resumeFarmerAfter, bool stackCapMode = false)
    {
        if (IsBusy)
            return false;
        if (!plugin.Config.EnableCurrencyExchange)
            return false;
        if (!CanExchangeNow(out var reason))
        {
            if (!stackCapMode)
                log.Debug($"货币兑换未触发：{reason}");
            return false;
        }

        if (!BuildQueue(stackCapMode, out var queued))
            return false;

        resumeFarmer = resumeFarmerAfter;
        anySuccess = false;
        startedAt = Now();
        phase = Phase.PausingFarmer;
        status = "内部暂停挂机，准备兑换...";
        log.Information($"开始货币兑换，队列 {queued} 项（内部暂停，不发 /ocnstop）");
        return true;
    }

    internal void Abort()
    {
        if (!IsBusy)
            return;

        CompleteSession();
        queue.Clear();
        pending = null;
        phase = Phase.Idle;
        status = "兑换已中止";
        resumeFarmer = false;
        log.Information("货币兑换已被 /ocnstop 中止");
        plugin.OnCurrencyExchangeAborted();
    }

    internal void Reset()
    {
        CompleteSession();
        queue.Clear();
        pending = null;
        phase = Phase.Idle;
        status = "空闲";
        resumeFarmer = false;
    }

    internal void Update()
    {
        if (!IsBusy)
            return;

        var now = Now();
        MaintainWindowCleanup(now);

        if (now - startedAt > 90_000)
        {
            status = "兑换超时";
            Finish(restart: resumeFarmer);
            return;
        }

        if (!IsSupportedTerritory(clientState.TerritoryType))
        {
            status = "已离开支持区域，中止兑换";
            Finish(restart: false);
            return;
        }

        switch (phase)
        {
            case Phase.PausingFarmer:
                plugin.PauseForBuy();
                status = "已内部暂停挂机，建立远程会话";
                phase = Phase.Active;
                DequeueNextOrFinish(now);
                return;

            case Phase.Active:
                DriveActive(now);
                return;

            case Phase.ResumingFarmer:
                Finish(restart: resumeFarmer);
                return;
        }
    }

    private void DriveActive(long now)
    {
        if (pending is { } active)
        {
            SuppressShopWindow();
            ProcessPending(active, now);
            return;
        }

        if (!CanExchangeNow(out _))
            return;

        if (now < nextExchangeAt)
            return;

        DequeueNextOrFinish(now);
    }

    private void ProcessPending(ExchangeRequest request, long now)
    {
        if (pendingActionAt > 0)
        {
            if (now < pendingActionAt)
                return;

            try
            {
                if (!TrySendShopBuy(request, out _))
                {
                    if (now < pendingDeadline)
                        return;

                    CompleteSession();
                    FailExchange(request, now, $"未加载 {request.Spec.CurrencyName} 商店数据");
                    return;
                }

                pendingActionAt = 0;
                pendingDeadline = now + ConfirmTimeoutMs;
                status = $"已发送 {request.Spec.CurrencyName} ×{request.Quantity}，等待确认...";
            }
            catch (Exception ex)
            {
                CompleteSession();
                FailExchange(request, now, "发送兑换动作失败", ex);
            }

            return;
        }

        TryConfirmSelectYesno(request);

        var currencyNow = GetItemCount(request.Spec.CurrencyItemId);
        var rewardNow = GetItemCount(request.Spec.RewardItemId);
        var expectedCurrency = pendingCurrencyBefore - (uint)request.Quantity * (uint)request.Spec.Cost;
        if (currencyNow <= expectedCurrency || rewardNow >= pendingRewardBefore + request.Quantity)
        {
            anySuccess = true;
            CompleteSession();
            ScheduleWindowCleanup(now);
            retryAfter.Remove((request.Spec.CurrencyItemId, request.Spec.RewardItemId));
            pending = null;
            pendingActionAt = 0;
            pendingConfirmClicked = false;
            nextExchangeAt = now + ExchangeSpacingMs;
            status = $"{request.Spec.CurrencyName} → {request.Spec.RewardName} ×{request.Quantity} 完成";
            log.Information(status);
            return;
        }

        if (now < pendingDeadline)
            return;

        CompleteSession();
        FailExchange(request, now, $"未确认 {request.Spec.CurrencyName} 库存变化");
    }

    private void FailExchange(ExchangeRequest request, long now, string message, Exception? ex = null)
    {
        retryAfter[(request.Spec.CurrencyItemId, request.Spec.RewardItemId)] = now + RetryCooldownMs;
        pending = null;
        pendingActionAt = 0;
        pendingConfirmClicked = false;
        nextExchangeAt = now + ExchangeSpacingMs;
        ScheduleWindowCleanup(now);
        status = message;
        if (ex == null)
            log.Warning($"货币兑换：{message}");
        else
            log.Error(ex, $"货币兑换：{message}");
    }

    private void DequeueNextOrFinish(long now)
    {
        while (queue.Count > 0)
        {
            var request = queue.Dequeue();
            var count = GetItemCount(request.Spec.CurrencyItemId);
            if (count < (uint)request.Spec.Cost)
                continue;

            StartSession(request, now);
            return;
        }

        phase = Phase.ResumingFarmer;
        status = anySuccess ? "兑换完成，准备恢复挂机" : "兑换结束（未成功）";
    }

    private void StartSession(ExchangeRequest request, long now)
    {
        try
        {
            var player = objects.LocalPlayer;
            if (player == null)
            {
                FailExchange(request, now, "玩家不可用");
                return;
            }

            if (!ShopEventPackets.Ready)
            {
                FailExchange(request, now, "事件包未就绪");
                return;
            }

            windowCleanupUntil = 0;
            eventCompleted = false;
            activeEventId = request.Spec.EventId;
            ShopEventPackets.SendEventStart(player.EntityId, request.Spec.EventId);

            pending = request;
            pendingCurrencyBefore = GetItemCount(request.Spec.CurrencyItemId);
            pendingRewardBefore = GetItemCount(request.Spec.RewardItemId);
            pendingActionAt = now + 200;
            pendingDeadline = now + SessionTimeoutMs;
            pendingConfirmClicked = false;
            status = $"建立 {request.Spec.CurrencyName} 远程会话...";
            log.Debug($"EventStart player={player.EntityId:X} event={request.Spec.EventId:X}");
        }
        catch (Exception ex)
        {
            FailExchange(request, now, "建立兑换会话失败", ex);
        }
    }

    private void CompleteSession()
    {
        if (eventCompleted)
        {
            CloseShopWindows();
            return;
        }

        eventCompleted = true;
        if (activeEventId != 0 && ShopEventPackets.Ready)
        {
            try
            {
                ShopEventPackets.SendEventComplete(activeEventId);
            }
            catch (Exception ex)
            {
                log.Error(ex, "EventComplete 失败");
            }
        }

        activeEventId = 0;
        CloseShopWindows();
    }

    private void Finish(bool restart)
    {
        CompleteSession();
        queue.Clear();
        pending = null;
        phase = Phase.Idle;
        var shouldResume = restart && resumeFarmer;
        resumeFarmer = false;
        status = anySuccess ? "兑换完成" : "兑换未成功";
        if (shouldResume)
            status += "，内部恢复挂机";
        log.Information(status);
        plugin.OnCurrencyExchangeFinished(anySuccess, shouldResume);
    }

    private bool BuildQueue(bool stackCapMode, out int queued)
    {
        queue.Clear();
        queued = 0;
        var territory = clientState.TerritoryType;
        var cfg = plugin.Config;
        var now = Now();

        foreach (var spec in ExchangeCatalog.Get(territory, cfg.ExchangeReward))
        {
            var count = GetItemCount(spec.CurrencyItemId);
            retryAfter.TryGetValue((spec.CurrencyItemId, spec.RewardItemId), out var blockedUntil);
            if (now < blockedUntil)
                continue;

            if (stackCapMode || cfg.ExchangeTrigger == ExchangeTrigger.StackCapAtCrystal)
            {
                if (count < CurrencyStackCap)
                    continue;
            }
            else
            {
                var threshold = GetThreshold(spec.CurrencyItemId);
                if (count < (uint)threshold || count < (uint)spec.Cost)
                    continue;
            }

            var quantity = Math.Min(count / (uint)spec.Cost, (uint)Math.Max(1, cfg.MaxExchangesPerTrip));
            if (quantity <= 0)
                continue;

            queue.Enqueue(new ExchangeRequest(spec, (int)quantity));
            queued++;
        }

        return queued > 0;
    }

    private bool CanExchangeNow(out string reason)
    {
        var territory = clientState.TerritoryType;
        if (!IsSupportedTerritory(territory))
        {
            reason = "仅可在南征(1252)或北征(1346)兑换。";
            return false;
        }

        if (ExchangeCatalog.Get(territory, plugin.Config.ExchangeReward).Count == 0)
        {
            reason = plugin.Config.ExchangeReward == ExchangeReward.UltimateFixative
                ? "终极固定剂仅可在北征(1346)兑换。"
                : "当前地图不支持该兑换。";
            return false;
        }

        var player = objects.LocalPlayer;
        if (player is not { IsDead: false })
        {
            reason = "角色不可用。";
            return false;
        }

        if (!IsNearInitialCrystal(territory))
        {
            reason = "请站在初始魔路水晶旁（10 码内）。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private int GetThreshold(uint currencyItemId) =>
        currencyItemId is 51975 or 45043 ? plugin.Config.SilverThreshold : plugin.Config.GoldThreshold;

    private static bool IsSupportedTerritory(uint territory) =>
        territory is Plugin.NorthIslandTerritory or Plugin.SouthIslandTerritory;

    private bool IsNearInitialCrystal(uint territory)
    {
        var player = objects.LocalPlayer;
        if (player == null)
            return false;

        var basePos = territory == Plugin.NorthIslandTerritory
            ? new Vector3(882f, 258.5f, 882f)
            : new Vector3(834f, 73f, -694.6f);

        var dx = player.Position.X - basePos.X;
        var dz = player.Position.Z - basePos.Z;
        return dx * dx + dz * dz <= InitialCrystalRadius * InitialCrystalRadius;
    }

    private static uint GetItemCount(uint itemId)
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null) return 0;
            var count = inv->GetInventoryItemCount(itemId, false, true, true, 0);
            return count < 0 ? 0u : (uint)count;
        }
        catch
        {
            return 0;
        }
    }

    private bool TrySendShopBuy(ExchangeRequest request, out int itemIndex)
    {
        itemIndex = -1;
        if (TryGetAgentItemIndex(request.Spec.RewardItemId, out itemIndex) &&
            SendShopAgentEvent(itemIndex, request.Quantity))
            return true;

        var shopAddon = GetAddon("ShopExchangeCurrency");
        if (shopAddon == null)
            return false;

        if (!TryFindRewardIndex(shopAddon, request.Spec.RewardItemId, out itemIndex))
        {
            FireCallback(shopAddon, 4, -1, 1, 3);
            return false;
        }

        return FireCallback(shopAddon, 0, itemIndex, request.Quantity);
    }

    private static bool TryGetAgentItemIndex(uint rewardItemId, out int itemIndex)
    {
        itemIndex = -1;
        var agent = AgentShop.Instance();
        if (agent == null || !agent->IsAgentActive() || agent->ItemReceive == null)
            return false;

        var items = agent->ItemReceiveSpan;
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].ItemId != rewardItemId)
                continue;
            itemIndex = i;
            return true;
        }

        return false;
    }

    private static bool TryFindRewardIndex(AtkUnitBase* shop, uint rewardItemId, out int buyIndex)
    {
        buyIndex = -1;
        try
        {
            if (shop == null || shop->AtkValues == null)
                return false;

            var count = shop->AtkValuesCount;
            if (count <= 4)
                return false;

            var entryCount = shop->AtkValues[4].UInt;
            var num = Math.Min((int)entryCount, 40);
            for (var i = 0; i < num; i++)
            {
                var idSlot = 1066 + i;
                var indexSlot = 1310 + i;
                if (idSlot >= count)
                    break;
                if (shop->AtkValues[idSlot].UInt != rewardItemId)
                    continue;
                buyIndex = indexSlot < count ? (int)shop->AtkValues[indexSlot].UInt : i;
                if (buyIndex < 0)
                    buyIndex = i;
                return true;
            }
        }
        catch
        {
            buyIndex = -1;
        }

        return false;
    }

    private static bool SendShopAgentEvent(int itemIndex, int quantity)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Shop);
        if (agent == null)
            return false;

        var returnValue = stackalloc AtkValue[1];
        var values = stackalloc AtkValue[4];
        values[0] = default;
        values[0].Type = AtkValueType.Int;
        values[0].Int = 0;
        values[1] = default;
        values[1].Type = AtkValueType.Int;
        values[1].Int = itemIndex;
        values[2] = default;
        values[2].Type = AtkValueType.Int;
        values[2].Int = quantity;
        values[3] = default;
        values[3].Type = AtkValueType.Int;
        values[3].Int = 0;

        agent->ReceiveEvent(returnValue, values, 4, 1);
        return true;
    }

    private void TryConfirmSelectYesno(ExchangeRequest request)
    {
        if (pendingConfirmClicked)
            return;

        var yesno = GetAddon("SelectYesno");
        if (yesno == null)
            return;

        if (!FireCallback(yesno, 0))
            return;

        pendingConfirmClicked = true;
        status = $"已确认 {request.Spec.CurrencyName} 兑换";
    }

    private void OnShopAddon(AddonEvent type, AddonArgs args)
    {
        if (pending == null && Now() >= windowCleanupUntil)
            return;
        if (args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon != null)
                addon->IsVisible = false;
        }
        catch
        {
            // ignored
        }
    }

    private void OnShopDialogAddon(AddonEvent type, AddonArgs args)
    {
        if (args.Addon.IsNull)
            return;

        if (pending is not { } request)
        {
            if (Now() < windowCleanupUntil)
                HideAndCloseAddon((AtkUnitBase*)args.Addon.Address);
            return;
        }

        if (pendingActionAt != 0 || pendingConfirmClicked)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsReady)
                return;

            if (!FireCallback(addon, 0, request.Quantity))
                return;

            addon->IsVisible = false;
            pendingConfirmClicked = true;
            status = $"已确认 {request.Spec.CurrencyName} 数量对话框";
        }
        catch
        {
            // ignored
        }
    }

    private void OnConfirmAddon(AddonEvent type, AddonArgs args)
    {
        if (pending is not { } request || pendingActionAt != 0 || pendingConfirmClicked || args.Addon.IsNull)
            return;

        TryConfirmSelectYesno(request);
        if (pendingConfirmClicked)
        {
            try
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (addon != null)
                    addon->IsVisible = false;
            }
            catch
            {
                // ignored
            }
        }
    }

    private void ScheduleWindowCleanup(long now) =>
        windowCleanupUntil = Math.Max(windowCleanupUntil, now + WindowCleanupMs);

    private void MaintainWindowCleanup(long now)
    {
        if (now >= windowCleanupUntil)
            return;
        CloseShopWindows();
    }

    private static void SuppressShopWindow()
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("ShopExchangeCurrency");
            if (addon != null)
                addon->IsVisible = false;
        }
        catch
        {
            // ignored
        }
    }

    private void CloseShopWindows()
    {
        CloseWindow("ShopExchangeCurrencyDialog");
        CloseWindow("ShopExchangeCurrency");
        CloseWindow("SelectYesno");
    }

    private void CloseWindow(string name)
    {
        try
        {
            var addon = GetAddon(name);
            if (addon == null)
                return;
            addon->IsVisible = false;
            addon->Close(true);
        }
        catch
        {
            // ignored
        }
    }

    private static void HideAndCloseAddon(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null)
                return;
            addon->IsVisible = false;
            addon->Close(true);
        }
        catch
        {
            // ignored
        }
    }

    private AtkUnitBase* GetAddon(string name)
    {
        try
        {
            var ptr = gameGui.GetAddonByName(name);
            if (ptr.IsNull) return null;
            var addon = (AtkUnitBase*)ptr.Address;
            if (addon == null || !addon->IsReady) return null;
            return addon;
        }
        catch
        {
            return null;
        }
    }

    private static bool FireCallback(AtkUnitBase* addon, params int[] args)
    {
        try
        {
            if (addon == null || !addon->IsReady) return false;
            if (args.Length == 1)
            {
                addon->FireCallbackInt(args[0]);
                return true;
            }

            var values = stackalloc AtkValue[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                values[i] = default;
                values[i].Type = AtkValueType.Int;
                values[i].Int = args[i];
            }

            addon->FireCallback((uint)args.Length, values, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long Now() => Environment.TickCount64;

    private static class ExchangeCatalog
    {
        private static readonly ExchangeSpec[] NorthFixative =
        [
            new("十二城邦白银币", 51975, 0x1B0614, 1200, "终极固定剂", FixativeItemId),
            new("十二城邦白金币", 51976, 0x1B0615, 1920, "终极固定剂", FixativeItemId),
        ];

        private static readonly ExchangeSpec[] NorthCoffer =
        [
            new("十二城邦白银币", 51975, 0x1B0614, 40, "古旧的钱箱", CofferItemId),
            new("十二城邦白金币", 51976, 0x1B0615, 50, "古旧的钱箱", CofferItemId),
        ];

        private static readonly ExchangeSpec[] SouthCoffer =
        [
            new("十二城邦银币", 45043, 0x1B05B0, 40, "古旧的钱箱", CofferItemId),
            new("十二城邦金币", 45044, 0x1B05B2, 50, "古旧的钱箱", CofferItemId),
        ];

        internal static IReadOnlyList<ExchangeSpec> Get(uint territory, ExchangeReward reward) =>
            (territory, reward) switch
            {
                (Plugin.SouthIslandTerritory, ExchangeReward.OldCoffer) => SouthCoffer,
                (Plugin.NorthIslandTerritory, ExchangeReward.OldCoffer) => NorthCoffer,
                (Plugin.NorthIslandTerritory, ExchangeReward.UltimateFixative) => NorthFixative,
                _ => Array.Empty<ExchangeSpec>(),
            };
    }
}

public enum ExchangeReward
{
    UltimateFixative,
    OldCoffer,
}

public enum ExchangeTrigger
{
    ThresholdOnReturn,
    StackCapAtCrystal,
}
