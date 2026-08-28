using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Interop.Game.AddonEvent;
using OmenTools.Info.Game.Packets.Upstream;

namespace NorthIslandChestPlugin;
public enum CurrencyPurchaseMode
{
    None,
    OldCoffer,
    UltimateFixative,
}

internal enum CurrencyKind
{
    Silver,
    Gold,
}

internal readonly record struct CurrencyPurchaseRequest(
    CurrencyKind Currency,
    string CurrencyName,
    uint CurrencyItemId,
    uint EventId,
    string RewardName,
    uint RewardItemId,
    int Cost,
    int Quantity);

internal sealed unsafe class CurrencyBuyer : IDisposable
{
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShopTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(6);
    private static readonly Vector3 InitialCrystal = new(882f, 258.5f, 882f);
    private const float InitialCrystalRadius = 15f;
    private static readonly TimeSpan EventStartDelay = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan AgentReadyDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan EventStartRetryDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan WindowCleanupDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan BetweenRequestDelay = TimeSpan.FromMilliseconds(750);
    private const int MaxEventStartAttempts = 3;

    private enum Phase
    {
        Idle,
        StartSession,
        SendPurchase,
        WaitForConfirmation,
        Verify,
        BetweenRequests,
        Closing,
    }

    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly Action<bool, string> finished;
    private readonly Queue<CurrencyPurchaseRequest> queue = new();

    private Phase phase;
    private CurrencyPurchaseRequest current;
    private DateTime startedAt;
    private DateTime phaseDeadline;
    private DateTime nextActionAt;
    private DateTime windowCleanupUntil;
    private int currencyBefore;
    private int rewardBefore;
    private bool confirmationSent;
    private bool closeForNextRequest;
    private bool completionSuccess;
    private bool eventCompleted;
    private bool eventStartSent;
    private int eventStartAttempts;
    private uint activeEventId;
    private string completionMessage = string.Empty;
    internal bool IsBusy => phase != Phase.Idle;
    internal string Status { get; private set; } = "空闲";

    internal CurrencyBuyer(
        IClientState clientState,
        IObjectTable objects,
        ICondition condition,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        Action<bool, string> finished)
    {
        this.clientState = clientState;
        this.objects = objects;
        this.condition = condition;
        this.gameGui = gameGui;
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.finished = finished;

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
        CompleteEventSession();
        queue.Clear();
        CloseShopWindows();
    }

    internal bool Begin(IEnumerable<CurrencyPurchaseRequest> requests)
    {
        if (IsBusy) return false;
        if (clientState.TerritoryType != Plugin.IslandTerritory ||
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.InCombat] || IsOccupiedForShopEvent())
            return false;
        var existingYesNo = GetAddon("SelectYesno");
        if (existingYesNo != null && existingYesNo->IsVisible)
        {
            Status = "当前有其他操作等待确认，暂不自动购买";
            return false;
        }
        if (!DService.IsInitialized)
        {
            Status = "自动购买功能暂未就绪";
            return false;
        }
        if (GetAddon("ShopExchangeCurrency") != null || GetAddon("ShopExchangeCurrencyDialog") != null)
        {
            Status = "当前无法开始自动购买";
            return false;
        }
        if (!IsNearInitialCrystal())
        {
            Status = "请站在初始魔路水晶旁（15 码内）后再自动购买";
            return false;
        }

        foreach (var request in requests) queue.Enqueue(request);
        if (queue.Count == 0) return false;

        log.Information($"自动购买队列：{string.Join("；", queue.Select(x => $"{x.CurrencyName}->{x.RewardName} ×{x.Quantity}"))}");

        startedAt = DateTime.UtcNow;
        StartNextRequest();
        return true;
    }

    internal void Cancel()
    {
        queue.Clear();
        CompleteEventSession();
        CloseShopWindows();
        phase = Phase.Idle;
        Status = "已取消自动购买";
    }

    internal void Update()
    {
        if (!IsBusy) return;
        MaintainWindowCleanup();

        if (clientState.TerritoryType != Plugin.IslandTerritory)
        {
            Fail("已离开蜃景幻界新月岛 北征之章");
            return;
        }
        if (phase != Phase.Closing && DateTime.UtcNow - startedAt > OverallTimeout)
        {
            Fail("自动购买流程超过 90 秒");
            return;
        }
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            Status = "自动购买：等待过图完成";
            return;
        }
        if (condition[ConditionFlag.InCombat])
        {
            Status = "自动购买：等待脱离战斗";
            return;
        }

        switch (phase)
        {
            case Phase.StartSession:
                DriveStartSession();
                break;
            case Phase.SendPurchase:
                DriveSendPurchase();
                break;
            case Phase.WaitForConfirmation:
                DriveWaitForConfirmation();
                break;
            case Phase.Verify:
                VerifyPurchase();
                break;
            case Phase.BetweenRequests:
                DriveBetweenRequests();
                break;
            case Phase.Closing:
                DriveClosing();
                break;
        }
    }

    private void StartNextRequest()
    {
        confirmationSent = false;
        if (queue.Count == 0)
        {
            FinishNow(true, "自动购买完成");
            return;
        }

        current = queue.Dequeue();
        var player = objects.LocalPlayer;
        if (player == null)
        {
            Fail("玩家不可用");
            return;
        }

        var affordable = GetItemCount(current.CurrencyItemId) / current.Cost;
        var quantity = Math.Min(current.Quantity, affordable);
        if (quantity <= 0)
        {
            Fail($"{current.CurrencyName}已不足以购买{current.RewardName}");
            return;
        }

        current = current with { Quantity = quantity };
        currencyBefore = GetItemCount(current.CurrencyItemId);
        rewardBefore = GetItemCount(current.RewardItemId);
        eventCompleted = false;
        eventStartSent = false;
        eventStartAttempts = 0;
        activeEventId = 0;
        windowCleanupUntil = DateTime.MinValue;

        phase = Phase.StartSession;
        phaseDeadline = DateTime.UtcNow + ShopTimeout;
        nextActionAt = DateTime.UtcNow + EventStartDelay;
        Status = $"准备使用{current.CurrencyName}购买{current.RewardName} ×{current.Quantity}";
        log.Debug($"自动购买排队 EventStart player={GetLocalEntityId():X} event={current.EventId:X}");
    }

    private void DriveStartSession()
    {
        SuppressShopWindow();
        if (DateTime.UtcNow < nextActionAt)
            return;

        if (!eventStartSent)
        {
            if (!CanSendEventStart())
            {
                if (DateTime.UtcNow >= phaseDeadline)
                {
                    if (GetLocalEntityId() == 0)
                        Fail("玩家实体未就绪，已中止自动购买", "当前无法开始自动购买");
                    else
                        Fail("当前状态不允许发送 EventStart，已中止自动购买", "当前无法开始自动购买");
                }
                else
                    Status = "自动购买：正在准备购买...";
                return;
            }

            if (!TrySendEventStart())
            {
                eventStartAttempts++;
                if (eventStartAttempts >= MaxEventStartAttempts)
                {
                    Fail("EventStart 发包失败，已中止自动购买", "未能开始自动购买");
                    return;
                }

                nextActionAt = DateTime.UtcNow + EventStartRetryDelay;
                Status = "自动购买：正在准备购买...";
                return;
            }

            eventStartSent = true;
            activeEventId = current.EventId;
            nextActionAt = DateTime.UtcNow + AgentReadyDelay;
            Status = "自动购买：正在准备购买...";
            return;
        }

        if (!IsShopAgentReady())
        {
            if (DateTime.UtcNow >= phaseDeadline)
                Fail($"商店 Agent 未在 {ShopTimeout.TotalSeconds:0} 秒内就绪，已中止自动购买", "未能打开购买界面");
            return;
        }

        phase = Phase.SendPurchase;
        phaseDeadline = DateTime.UtcNow + ShopTimeout;
        nextActionAt = DateTime.MinValue;
        Status = $"自动购买：正在购买{current.RewardName} ×{current.Quantity}...";
    }

    private void DriveSendPurchase()
    {
        SuppressShopWindow();
        if (DateTime.UtcNow < nextActionAt) return;

        if (!TrySendShopBuy(out _))
        {
            if (DateTime.UtcNow >= phaseDeadline)
                Fail($"商店中未找到{current.RewardName}，已停止以避免误购");
            return;
        }

        phase = Phase.WaitForConfirmation;
        phaseDeadline = DateTime.UtcNow + ConfirmationTimeout;
        confirmationSent = false;
        Status = $"自动购买：正在购买{current.RewardName} ×{current.Quantity}...";
    }

    private void DriveWaitForConfirmation()
    {
        SuppressShopWindow();
        TryConfirmSelectYesno();
        if (PurchaseApplied())
        {
            PurchaseSucceeded();
            return;
        }

        if (DateTime.UtcNow >= phaseDeadline)
            Fail("购买确认超时或库存未变化");
    }
    private void VerifyPurchase()
    {
        if (PurchaseApplied())
        {
            PurchaseSucceeded();
            return;
        }
        if (DateTime.UtcNow >= phaseDeadline)
        {
            var currencyNow = GetItemCount(current.CurrencyItemId);
            var rewardNow = GetItemCount(current.RewardItemId);
            Fail(
                $"购买库存验证失败：{current.CurrencyName} {currencyBefore}->{currencyNow}，{current.RewardName} {rewardBefore}->{rewardNow}",
                "未能确认购买结果");
        }
    }

    private bool PurchaseApplied()
    {
        var currencyNow = GetItemCount(current.CurrencyItemId);
        var rewardNow = GetItemCount(current.RewardItemId);
        return currencyNow <= currencyBefore - current.Cost * current.Quantity &&
               rewardNow >= rewardBefore + current.Quantity;
    }

    private void PurchaseSucceeded()
    {
        log.Information($"自动购买成功：{current.CurrencyName} -> {current.RewardName} ×{current.Quantity}");
        BeginClosing(true, $"已购买{current.RewardName} ×{current.Quantity}", queue.Count > 0);
    }

    private void Fail(string diagnosticMessage, string? userMessage = null)
    {
        log.Error($"自动购买失败：{diagnosticMessage}");
        queue.Clear();
        BeginClosing(false, userMessage ?? diagnosticMessage, false);
    }

    private void BeginClosing(bool success, string message, bool continueWithNextRequest)
    {
        completionSuccess = success;
        completionMessage = message;
        closeForNextRequest = continueWithNextRequest;
        phase = Phase.Closing;
        phaseDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        nextActionAt = DateTime.MinValue;
        ScheduleWindowCleanup();
        Status = success ? "自动购买：正在完成购买..." : message;
    }

    private void DriveClosing()
    {
        CompleteEventSession();
        CloseShopWindows();

        if (DateTime.UtcNow < phaseDeadline)
            return;

        if (closeForNextRequest && queue.Count > 0)
        {
            phase = Phase.BetweenRequests;
            nextActionAt = DateTime.UtcNow + BetweenRequestDelay;
            phaseDeadline = DateTime.UtcNow + ShopTimeout;
            Status = $"自动购买：正在准备下一笔购买（剩余 {queue.Count} 笔）...";
            return;
        }

        FinishNow(completionSuccess, completionMessage);
    }

    private void DriveBetweenRequests()
    {
        if (DateTime.UtcNow < nextActionAt)
            return;

        if (IsShopAgentReady() || IsOccupiedForShopEvent())
        {
            if (DateTime.UtcNow >= phaseDeadline)
            {
                Fail(
                    "上一笔商店事件未完全退出，已停止以避免复用错误的钱币商店",
                    "上一笔购买未能正常结束");
                return;
            }

            Status = $"自动购买：正在准备下一笔购买（剩余 {queue.Count} 笔）...";
            return;
        }

        log.Information($"上一钱币商店已退出，开始下一笔自动购买，剩余 {queue.Count} 笔");
        StartNextRequest();
    }

    private void FinishNow(bool success, string message)
    {
        CompleteEventSession();
        CloseShopWindows();
        queue.Clear();
        phase = Phase.Idle;
        activeEventId = 0;
        Status = message;
        finished(success, message);
    }

    private void CompleteEventSession()
    {
        if (eventCompleted)
            return;

        eventCompleted = true;
        if (eventStartSent && activeEventId != 0)
        {
            try
            {
                new EventCompletePackt(activeEventId, 0).Send();
                log.Debug($"自动购买 EventComplete event={activeEventId:X}");
            }
            catch (Exception ex)
            {
                log.Error(ex, $"自动购买 EventComplete 失败 event={activeEventId:X}");
            }
        }

        eventStartSent = false;
        activeEventId = 0;
    }

    private bool TrySendEventStart()
    {
        var localEntityId = GetLocalEntityId();
        if (!DService.IsInitialized || localEntityId == 0)
            return false;

        try
        {
            new EventStartPackt(localEntityId, current.EventId).Send();
            log.Debug(
                $"自动购买 EventStart player={localEntityId:X} event={current.EventId:X} attempt={eventStartAttempts + 1}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"自动购买 EventStart 失败 event={current.EventId:X}");
            return false;
        }
    }

    private bool CanSendEventStart()
    {
        if (clientState.TerritoryType != Plugin.IslandTerritory)
            return false;
        if (!clientState.IsLoggedIn || objects.LocalPlayer == null)
            return false;
        if (!IsNearInitialCrystal())
            return false;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;
        if (condition[ConditionFlag.InCombat])
            return false;
        if (IsOccupiedForShopEvent())
            return false;
        if (!DService.IsInitialized || GetLocalEntityId() == 0)
            return false;

        var yesNo = GetAddon("SelectYesno");
        if (yesNo != null && yesNo->IsVisible)
            return false;

        return true;
    }
    private bool IsNearInitialCrystal() => IsNearInitialCrystal(objects.LocalPlayer?.Position);

    private static bool IsNearInitialCrystal(Vector3? position)
    {
        if (position == null)
            return false;

        var pos = position.Value;
        var dx = pos.X - InitialCrystal.X;
        var dz = pos.Z - InitialCrystal.Z;
        return dx * dx + dz * dz <= InitialCrystalRadius * InitialCrystalRadius;
    }

    private bool IsOccupiedForShopEvent() =>
        condition[ConditionFlag.OccupiedInEvent] ||
        condition[ConditionFlag.OccupiedInQuestEvent] ||
        condition[ConditionFlag.Occupied33] ||
        condition[ConditionFlag.Occupied30];

    private static bool IsShopAgentReady()
    {
        try
        {
            var agent = AgentShop.Instance();
            return agent != null && agent->IsAgentActive() && agent->ItemReceive != null;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySendShopBuy(out int itemIndex)
    {
        itemIndex = -1;
        if (!TryGetAgentItemIndex(current.RewardItemId, out itemIndex))
            return false;

        return SendShopAgentEvent(itemIndex, current.Quantity);
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

    private static bool SendShopAgentEvent(int itemIndex, int quantity)
    {
        if (itemIndex < 0 || quantity <= 0)
            return false;

        if (!IsShopAgentReady())
            return false;

        try
        {
            AgentId.Shop.SendEvent(1, 0, itemIndex, quantity, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryConfirmSelectYesno()
    {
        if (confirmationSent)
            return;

        if (!IsCurrentCurrencyConfirmation())
            return;

        // SelectYesno 只校验本次使用的钱币名称，避免确认其他操作。
        if (!AddonSelectYesnoEvent.ClickYes(current.CurrencyName))
            return;

        confirmationSent = true;
        if (phase == Phase.WaitForConfirmation)
        {
            phase = Phase.Verify;
            phaseDeadline = DateTime.UtcNow + ConfirmationTimeout;
        }
        Status = $"自动购买：正在购买{current.RewardName} ×{current.Quantity}...";
    }

    private bool IsCurrentCurrencyConfirmation()
    {
        var yesNo = (AddonSelectYesno*)GetAddon("SelectYesno");
        if (yesNo == null || !yesNo->AtkUnitBase.IsVisible)
            return false;

        var promptNode = yesNo->AtkUnitBase.GetTextNodeById(2);
        var prompt = promptNode == null ? string.Empty : promptNode->NodeText.ToString();
        return !string.IsNullOrWhiteSpace(prompt) &&
               prompt.Contains(current.CurrencyName, StringComparison.Ordinal);
    }
    private void OnShopAddon(AddonEvent type, AddonArgs args)
    {
        if (phase == Phase.Idle && DateTime.UtcNow >= windowCleanupUntil)
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

        if (phase is Phase.Idle or Phase.Closing)
        {
            if (DateTime.UtcNow < windowCleanupUntil)
                HideAddon((AtkUnitBase*)args.Addon.Address);
            return;
        }

        if (confirmationSent || phase is not (Phase.SendPurchase or Phase.WaitForConfirmation or Phase.Verify))
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || !addon->IsReady || !addon->IsVisible)
                return;

            if (!FireCallback(addon, 0, current.Quantity))
                return;

            addon->IsVisible = false;
            confirmationSent = true;
            if (phase == Phase.WaitForConfirmation)
            {
                phase = Phase.Verify;
                phaseDeadline = DateTime.UtcNow + ConfirmationTimeout;
            }
            Status = $"自动购买：正在购买{current.RewardName} ×{current.Quantity}...";
        }
        catch
        {
            // ignored
        }
    }

    private void OnConfirmAddon(AddonEvent type, AddonArgs args)
    {
        if (phase is not (Phase.SendPurchase or Phase.WaitForConfirmation or Phase.Verify) ||
            confirmationSent || args.Addon.IsNull)
            return;

        TryConfirmSelectYesno();
        if (!confirmationSent)
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
    private void ScheduleWindowCleanup() =>
        windowCleanupUntil = DateTime.UtcNow + WindowCleanupDuration;

    private void MaintainWindowCleanup()
    {
        if (DateTime.UtcNow >= windowCleanupUntil)
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
        if (phase is Phase.Closing or Phase.Idle)
        {
            CloseWindow("ShopExchangeCurrencyDialog");
            CloseWindow("ShopExchangeCurrency");
            if (IsCurrentCurrencyConfirmation())
                CloseWindow("SelectYesno");
            return;
        }

        HideWindow("ShopExchangeCurrencyDialog");
        HideWindow("ShopExchangeCurrency");
        if (IsCurrentCurrencyConfirmation())
            HideWindow("SelectYesno");
    }

    private void CloseWindow(string name)
    {
        try
        {
            var addon = GetAddon(name);
            if (addon == null)
                return;
            addon->IsVisible = false;
            if (phase == Phase.Closing || phase == Phase.Idle)
                addon->Close(true);
        }
        catch
        {
            // ignored
        }
    }

    private static void HideWindow(string name)
    {
        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
            if (addon != null)
                addon->IsVisible = false;
        }
        catch
        {
            // ignored
        }
    }

    private static void HideAddon(AtkUnitBase* addon)
    {
        try
        {
            if (addon == null)
                return;
            addon->IsVisible = false;
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
            return addon != null && addon->IsReady ? addon : null;
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

    private static int GetItemCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null) return 0;
            return Math.Max(0, inventory->GetInventoryItemCount(itemId, false, true, true, 0));
        }
        catch
        {
            return 0;
        }
    }

    private static uint GetLocalEntityId()
    {
        try
        {
            var playerState = PlayerState.Instance();
            return playerState == null ? 0 : playerState->EntityId;
        }
        catch
        {
            return 0;
        }
    }
}
