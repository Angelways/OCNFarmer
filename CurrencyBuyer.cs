using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

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

internal sealed unsafe class CurrencyBuyer
{
    private const uint VendorBaseId = 1059485;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShopTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(6);

    private enum Phase
    {
        Idle,
        MoveToVendor,
        OpenShop,
        SelectCurrency,
        WaitForShop,
        WaitForConfirmation,
        Verify,
        BetweenRequests,
        Closing,
    }

    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Action<string> send;
    private readonly Action<bool, string> finished;
    private readonly Queue<CurrencyPurchaseRequest> queue = new();

    private Phase phase;
    private CurrencyPurchaseRequest current;
    private DateTime startedAt;
    private DateTime phaseDeadline;
    private DateTime nextActionAt;
    private DateTime nextTalkAt;
    private int currencyBefore;
    private int rewardBefore;
    private bool confirmationSent;
    private bool closeForNextRequest;
    private bool completionSuccess;
    private bool cancelInteractionSent;
    private string completionMessage = string.Empty;
    private bool selectionEntriesLogged;
    private bool confirmationPromptLogged;

    internal bool IsBusy => phase != Phase.Idle;
    internal string Status { get; private set; } = "空闲";

    internal CurrencyBuyer(
        IClientState clientState,
        IObjectTable objects,
        ICondition condition,
        IGameGui gameGui,
        IPluginLog log,
        Action<string> send,
        Action<bool, string> finished)
    {
        this.clientState = clientState;
        this.objects = objects;
        this.condition = condition;
        this.gameGui = gameGui;
        this.log = log;
        this.send = send;
        this.finished = finished;
    }

    internal bool Begin(IEnumerable<CurrencyPurchaseRequest> requests)
    {
        if (IsBusy) return false;
        if (clientState.TerritoryType != Plugin.IslandTerritory ||
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.InCombat] || condition[ConditionFlag.OccupiedInEvent] ||
            condition[ConditionFlag.OccupiedInQuestEvent])
            return false;
        var existingYesNo = GetAddon("SelectYesno");
        if (existingYesNo != null && existingYesNo->IsVisible)
        {
            Status = "存在其他确认窗口，本次不执行自动购买";
            return false;
        }
        foreach (var request in requests) queue.Enqueue(request);
        if (queue.Count == 0) return false;

        startedAt = DateTime.UtcNow;
        StartNextRequest();
        return true;
    }

    internal void Cancel()
    {
        queue.Clear();
        RequestCloseCallbacks();
        TryCancelCurrentInteraction();
        phase = Phase.Idle;
        Status = "已取消自动购买";
        send("/vnav stop");
    }

    internal void Update()
    {
        if (!IsBusy) return;
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
            case Phase.MoveToVendor:
                MoveToVendor();
                break;
            case Phase.OpenShop:
                OpenShop();
                break;
            case Phase.SelectCurrency:
                SelectCurrencyShop();
                break;
            case Phase.WaitForShop:
                WaitForShopAndPurchase();
                break;
            case Phase.WaitForConfirmation:
                WaitForConfirmation();
                break;
            case Phase.Verify:
                VerifyPurchase();
                break;
            case Phase.BetweenRequests:
                if (DateTime.UtcNow >= nextActionAt) StartNextRequest();
                break;
            case Phase.Closing:
                DriveClosing();
                break;
        }
    }

    private void StartNextRequest()
    {
        confirmationSent = false;
        selectionEntriesLogged = false;
        confirmationPromptLogged = false;
        if (queue.Count == 0)
        {
            FinishNow(true, "自动购买完成");
            return;
        }

        current = queue.Dequeue();
        phase = Phase.MoveToVendor;
        phaseDeadline = DateTime.UtcNow + ShopTimeout;
        nextActionAt = DateTime.MinValue;
        nextTalkAt = DateTime.MinValue;
        Status = $"准备使用{current.CurrencyName}购买{current.RewardName} ×{current.Quantity}";
    }

    private void MoveToVendor()
    {
        var player = objects.LocalPlayer;
        var vendor = objects.FirstOrDefault(obj =>
            obj.ObjectKind == DalamudObjectKind.EventNpc && obj.BaseId == VendorBaseId && obj.IsTargetable && obj.Address != nint.Zero);
        if (player == null || vendor == null)
        {
            if (DateTime.UtcNow >= phaseDeadline) Fail("未找到固定剂兑换商人");
            return;
        }

        var distance = Vector3.Distance(player.Position, vendor.Position);
        if (distance > 4.5f)
        {
            if (DateTime.UtcNow >= nextActionAt)
            {
                var p = vendor.Position;
                send($"/vnav moveto {p.X.ToString(CultureInfo.InvariantCulture)} {p.Y.ToString(CultureInfo.InvariantCulture)} {p.Z.ToString(CultureInfo.InvariantCulture)}");
                nextActionAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            }
            Status = $"自动购买：正在接近兑换商人（{distance:0.0} 米）";
            return;
        }

        send("/vnav stop");
        phase = Phase.OpenShop;
        phaseDeadline = DateTime.UtcNow + ShopTimeout;
        nextActionAt = DateTime.MinValue;
        Status = $"自动购买：正在打开{current.CurrencyName}商店";
    }

    private void OpenShop()
    {
        ClickTalk();
        if (GetSelectAddon() != null)
        {
            phase = Phase.SelectCurrency;
            Status = $"自动购买：正在识别{current.CurrencyName}商店选项";
            return;
        }
        if (DateTime.UtcNow >= phaseDeadline)
        {
            Fail("打开兑换商店对话超时");
            return;
        }
        if (DateTime.UtcNow < nextActionAt) return;

        var vendor = objects.FirstOrDefault(obj =>
            obj.ObjectKind == DalamudObjectKind.EventNpc && obj.BaseId == VendorBaseId &&
            obj.IsTargetable && obj.Address != nint.Zero);
        if (vendor != null)
        {
            try
            {
                var targetSystem = TargetSystem.Instance();
                var gameObject = (GameObject*)vendor.Address;
                if (targetSystem != null && gameObject != null)
                    targetSystem->InteractWithObject(gameObject, true);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "自动购买：与兑换商人交互失败");
            }
        }
        nextActionAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);
    }

    private void SelectCurrencyShop()
    {
        ClickTalk();
        var addon = GetSelectAddon();
        if (addon == null)
        {
            if (DateTime.UtcNow >= phaseDeadline)
                Fail("兑换商店选择列表未出现或已消失");
            return;
        }

        var isIcon = GetAddon("SelectIconString") == addon;
        var keyword = current.Currency == CurrencyKind.Silver ? "其他" : "白金币";
        var index = FindSelectIndex(addon, isIcon, keyword);
        if (index < 0)
        {
            if (DateTime.UtcNow >= phaseDeadline)
                Fail($"商店列表中未找到包含“{keyword}”的选项，已停止以避免误选");
            return;
        }
        if (!FireCallback(addon, index))
        {
            Fail($"选择{current.CurrencyName}商店失败");
            return;
        }

        log.Information($"自动购买：已按关键字“{keyword}”选择第 {index} 个商店选项");
        phase = Phase.WaitForShop;
        phaseDeadline = DateTime.UtcNow + ShopTimeout;
        nextActionAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
        Status = $"自动购买：已选择{current.CurrencyName}商店";
    }

    private void WaitForShopAndPurchase()
    {
        ClickTalk();
        if (DateTime.UtcNow < nextActionAt) return;
        if (!TryFindRewardIndex(current.RewardItemId, out var rewardIndex))
        {
            if (DateTime.UtcNow >= phaseDeadline)
                Fail($"商店中未找到{current.RewardName}，已停止以避免误购");
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

        var shop = GetAddon("ShopExchangeCurrency");
        if (shop == null) shop = GetAddon("ShopExchangeItem");
        if (shop == null || !shop->IsVisible || !FireCallback(shop, 0, rewardIndex, current.Quantity))
        {
            if (DateTime.UtcNow >= phaseDeadline)
                Fail("发送购买请求失败");
            return;
        }

        phase = Phase.WaitForConfirmation;
        phaseDeadline = DateTime.UtcNow + ConfirmationTimeout;
        confirmationSent = false;
        Status = $"自动购买：已请求{current.RewardName} ×{current.Quantity}，等待确认";
    }

    private void WaitForConfirmation()
    {
        if (PurchaseApplied())
        {
            PurchaseSucceeded();
            return;
        }

        if (!confirmationSent)
        {
            var exchangeDialog = GetAddon("ShopExchangeCurrencyDialog");
            if (exchangeDialog != null && exchangeDialog->IsVisible)
            {
                var button = exchangeDialog->GetComponentButtonById(17);
                if (button != null && button->IsEnabled)
                {
                    FireCallback(exchangeDialog, 0, current.Quantity);
                    confirmationSent = true;
                    phase = Phase.Verify;
                    phaseDeadline = DateTime.UtcNow + ConfirmationTimeout;
                    Status = $"自动购买：已单次确认{current.RewardName}，等待库存更新";
                    return;
                }
            }

            var yesNo = (AddonSelectYesno*)GetAddon("SelectYesno");
            if (yesNo != null && yesNo->AtkUnitBase.IsVisible)
            {
                var prompt = GetYesNoPrompt(yesNo);
                if (!confirmationPromptLogged)
                {
                    log.Information($"自动购买确认文本：{(string.IsNullOrWhiteSpace(prompt) ? "<空>" : prompt)}");
                    confirmationPromptLogged = true;
                }
                if (PromptMatchesPurchase(prompt))
                {
                    if (yesNo->YesButton == null || !yesNo->YesButton->IsEnabled)
                    {
                        Status = "自动购买：确认窗口已出现，等待“是”按钮可用";
                        return;
                    }
                    yesNo->AtkUnitBase.FireCallbackInt(0);
                    confirmationSent = true;
                    phase = Phase.Verify;
                    phaseDeadline = DateTime.UtcNow + ConfirmationTimeout;
                    Status = $"自动购买：已单次确认{current.RewardName}，等待库存更新";
                    return;
                }
            }
        }

        if (DateTime.UtcNow >= phaseDeadline)
            Fail("购买确认窗口未出现或内容不匹配");
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
            Fail($"购买库存验证失败：{current.CurrencyName} {currencyBefore}->{currencyNow}，{current.RewardName} {rewardBefore}->{rewardNow}");
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

    private void Fail(string message)
    {
        log.Error($"自动购买失败：{message}");
        queue.Clear();
        BeginClosing(false, message, false);
    }

    private void BeginClosing(bool success, string message, bool continueWithNextRequest)
    {
        send("/vnav stop");
        completionSuccess = success;
        completionMessage = message;
        closeForNextRequest = continueWithNextRequest;
        cancelInteractionSent = false;
        phase = Phase.Closing;
        phaseDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        nextActionAt = DateTime.MinValue;
        Status = $"{message}，正在正常结束商店事件";
    }

    private void DriveClosing()
    {
        if (DateTime.UtcNow >= nextActionAt)
        {
            RequestCloseCallbacks();
            nextActionAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
        }

        var occupied = condition[ConditionFlag.OccupiedInEvent] ||
                       condition[ConditionFlag.OccupiedInQuestEvent];
        if (!occupied && !HasPurchaseWindow())
        {
            log.Information("自动购买：商店窗口已关闭，OccupiedInEvent/OccupiedInQuestEvent 已解除");
            if (closeForNextRequest && queue.Count > 0)
            {
                phase = Phase.BetweenRequests;
                nextActionAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(750);
                Status = "商店事件已正常结束，准备下一笔购买";
                return;
            }

            FinishNow(completionSuccess, completionMessage);
            return;
        }

        if (!cancelInteractionSent && DateTime.UtcNow >= phaseDeadline - TimeSpan.FromSeconds(6))
            TryCancelCurrentInteraction();

        if (DateTime.UtcNow >= phaseDeadline)
        {
            TryCancelCurrentInteraction();
            FinishNow(false, $"{completionMessage}；商店事件未能在 8 秒内正常结束，请手动关闭对话后再继续");
        }
    }

    private void FinishNow(bool success, string message)
    {
        queue.Clear();
        phase = Phase.Idle;
        Status = message;
        finished(success, message);
    }

    private bool PromptMatchesPurchase(string prompt)
    {
        var expectedCost = current.Cost * current.Quantity;
        return !string.IsNullOrWhiteSpace(prompt) &&
               prompt.Contains(current.CurrencyName, StringComparison.Ordinal) &&
               prompt.Contains($"×{expectedCost}", StringComparison.Ordinal) &&
               prompt.Contains("换取以下道具", StringComparison.Ordinal);
    }

    private static string GetYesNoPrompt(AddonSelectYesno* addon)
    {
        try
        {
            if (addon == null || addon->PromptText == null) return string.Empty;
            return ((Utf8String*)&addon->PromptText->NodeText)->ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool TryFindRewardIndex(uint rewardItemId, out int index)
    {
        index = -1;
        try
        {
            var agent = AgentShop.Instance();
            if (agent == null || !agent->IsAgentActive() || agent->ItemReceive == null) return false;
            var items = agent->ItemReceiveSpan;
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].ItemId != rewardItemId) continue;
                index = i;
                return true;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "自动购买：读取商店物品失败");
        }
        return false;
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

    private AtkUnitBase* GetSelectAddon()
    {
        var addon = GetAddon("SelectString");
        if (addon != null && addon->IsVisible) return addon;
        addon = GetAddon("SelectIconString");
        return addon != null && addon->IsVisible ? addon : null;
    }

    private void ClickTalk()
    {
        if (DateTime.UtcNow < nextTalkAt) return;
        var talk = GetAddon("Talk");
        if (talk == null || !talk->IsVisible) return;
        try
        {
            talk->FireCallbackInt(0);
            nextTalkAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        }
        catch { }
    }

    private int FindSelectIndex(AtkUnitBase* addon, bool isIcon, string keyword)
    {
        try
        {
            var count = isIcon
                ? ((AddonSelectIconString*)addon)->PopupMenu.PopupMenu.EntryCount
                : ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryCount;
            var entries = new List<string>();
            for (var i = 0; i < count && i < 16; i++)
            {
                var text = GetSelectEntryText(addon, isIcon, i);
                entries.Add($"[{i}] {text}");
                if (text.Contains(keyword, StringComparison.Ordinal))
                {
                    if (!selectionEntriesLogged)
                    {
                        log.Information($"自动购买商店选项：{string.Join(" | ", entries)}");
                        selectionEntriesLogged = true;
                    }
                    return i;
                }
            }

            if (!selectionEntriesLogged)
            {
                log.Information($"自动购买商店选项：{string.Join(" | ", entries)}");
                selectionEntriesLogged = true;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "自动购买：读取商店选择列表失败");
        }
        return -1;
    }

    private static string GetSelectEntryText(AtkUnitBase* addon, bool isIcon, int index)
    {
        try
        {
            PopupMenu* popup = isIcon
                ? &((AddonSelectIconString*)addon)->PopupMenu.PopupMenu
                : &((AddonSelectString*)addon)->PopupMenu.PopupMenu;
            if (popup->EntryNames == null || index < 0 || index >= popup->EntryCount)
                return string.Empty;
            var entry = popup->EntryNames[index];
            return entry.HasValue ? entry.ToString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
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

    private bool HasPurchaseWindow()
    {
        foreach (var name in new[] { "ShopExchangeCurrencyDialog", "ShopExchangeCurrency", "ShopExchangeItem", "SelectString", "SelectIconString", "Talk", "SelectYesno" })
        {
            var addon = GetAddon(name);
            if (addon != null && addon->IsVisible) return true;
        }
        return false;
    }

    private void RequestCloseCallbacks()
    {
        var exchangeDialog = GetAddon("ShopExchangeCurrencyDialog");
        if (exchangeDialog != null && exchangeDialog->IsVisible) FireCallback(exchangeDialog, -1);
        var yesNo = GetAddon("SelectYesno");
        if (yesNo != null && yesNo->IsVisible) FireCallback(yesNo, 1);

        foreach (var name in new[] { "ShopExchangeCurrency", "ShopExchangeItem", "SelectString", "SelectIconString" })
        {
            var addon = GetAddon(name);
            if (addon != null && addon->IsVisible) FireCallback(addon, -1);
        }

        ClickTalk();
    }

    private void TryCancelCurrentInteraction()
    {
        if (cancelInteractionSent || current.EventId == 0) return;
        try
        {
            var eventFramework = EventFramework.Instance();
            var handler = eventFramework == null ? null : eventFramework->GetEventHandlerById(current.EventId);
            if (handler == null)
            {
                log.Warning($"自动购买：未找到商店事件处理器 0x{current.EventId:X6}，无法执行取消兜底");
                return;
            }

            handler->CancelInteraction();
            cancelInteractionSent = true;
            log.Information($"自动购买：已对商店事件 0x{current.EventId:X6} 调用 CancelInteraction 兜底结束交互");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"自动购买：取消商店事件 0x{current.EventId:X6} 失败");
        }
    }
}
