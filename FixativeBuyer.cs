using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace NorthIslandChestPlugin;

/// <summary>
/// 北岛初始营地自动买终极固定剂。购买前会 /ocnstop，结束后 /ocnstart。
/// </summary>
internal sealed unsafe class FixativeBuyer
{
    private const uint NpcDataId = 1059485;
    private const uint SilverItemId = 51975;
    private const uint GoldItemId = 51976;
    private const uint FixativeItemId = 51978;
    private const int SilverPrice = 1200;
    private const int GoldPrice = 1920;
    private const uint SilverShopEventId = 1771028;
    private const uint GoldShopEventId = 1771029;
    private const int ShopCategoryOther = 3;
    private const float CampRange = 60f;
    private static readonly Vector3 StartCrystal = new(882f, 258.5f, 882f);

    private enum Phase
    {
        Idle,
        StoppingFarmer,
        OpenShop,
        SelectDialog,
        SwitchTab,
        Buy,
        Confirm,
        Verify,
        Closing,
        RestartFarmer,
        Done,
    }

    private readonly Plugin plugin;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Action<string> send;

    private Phase phase = Phase.Idle;
    private DateTime phaseAt = DateTime.MinValue;
    private DateTime startedAt = DateTime.MinValue;
    private DateTime lastInteractAt = DateTime.MinValue;
    private DateTime lastTabAt = DateTime.MinValue;
    private DateTime buySentAt = DateTime.MinValue;
    private bool resumeFarmer;
    private bool buyingGold;
    private bool goldDone;
    private bool silverDone;
    private bool anySuccess;
    private uint currencyBefore;
    private uint fixativeBefore;
    private int buyQty;
    private int buyAttempts;
    private string status = "空闲";

    internal bool IsBusy => phase is not Phase.Idle and not Phase.Done;
    internal string Status => status;

    internal FixativeBuyer(
        Plugin plugin,
        IClientState clientState,
        IObjectTable objects,
        ICondition condition,
        IGameGui gameGui,
        IPluginLog log,
        Action<string> send)
    {
        this.plugin = plugin;
        this.clientState = clientState;
        this.objects = objects;
        _ = condition;
        this.gameGui = gameGui;
        this.log = log;
        this.send = send;
    }

    internal bool TryBegin(bool resumeFarmerAfter)
    {
        if (IsBusy)
            return false;
        if (!plugin.Config.EnableFixativeBuy)
            return false;
        if (clientState.TerritoryType != Plugin.IslandTerritory)
            return false;
        if (!IsAtCamp())
            return false;

        var silver = GetItemCount(SilverItemId);
        var gold = GetItemCount(GoldItemId);
        var cfg = plugin.Config;
        var needGold = gold >= (uint)cfg.GoldThreshold && gold >= GoldPrice;
        var needSilver = silver >= (uint)cfg.SilverThreshold && silver >= SilverPrice;
        if (!needGold && !needSilver)
            return false;

        resumeFarmer = resumeFarmerAfter;
        goldDone = !needGold;
        silverDone = !needSilver;
        buyingGold = needGold;
        anySuccess = false;
        buyAttempts = 0;
        startedAt = DateTime.UtcNow;
        phase = Phase.StoppingFarmer;
        phaseAt = DateTime.UtcNow;
        status = "购买前停止 OCNFarmer...";
        log.Information($"开始买固定剂 银={silver} 金={gold}，先 /ocnstop");
        return true;
    }

    internal void Reset()
    {
        phase = Phase.Idle;
        status = "空闲";
        resumeFarmer = false;
    }

    internal void Update()
    {
        if (phase is Phase.Idle or Phase.Done)
            return;

        if (clientState.TerritoryType != Plugin.IslandTerritory)
        {
            status = "已离开北岛，中止购买";
            Finish(restart: false);
            return;
        }

        if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(90))
        {
            status = "购买超时";
            Finish(restart: resumeFarmer);
            return;
        }

        switch (phase)
        {
            case Phase.StoppingFarmer:
                send("/ocnstop");
                status = "已 /ocnstop，准备开店";
                phase = Phase.OpenShop;
                phaseAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
                return;

            case Phase.OpenShop:
                if (DateTime.UtcNow < phaseAt) return;
                ClickTalk();
                if (GetShopAddon() != null || GetSelectAddon() != null)
                {
                    phase = GetShopAddon() != null ? Phase.SwitchTab : Phase.SelectDialog;
                    phaseAt = DateTime.UtcNow;
                    return;
                }

                if (DateTime.UtcNow - lastInteractAt > TimeSpan.FromMilliseconds(800))
                {
                    TryInteractNpc(buyingGold ? GoldShopEventId : SilverShopEventId);
                    lastInteractAt = DateTime.UtcNow;
                    status = buyingGold ? "打开白金店..." : "打开白银店...";
                }
                return;

            case Phase.SelectDialog:
                ClickTalk();
                if (GetShopAddon() != null)
                {
                    phase = Phase.SwitchTab;
                    return;
                }

                if (TrySelectShopDialog(buyingGold))
                {
                    phaseAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
                    return;
                }

                if (DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(20) && GetSelectAddon() == null && GetShopAddon() == null)
                {
                    SkipCurrentAndContinue("对话未出现");
                }
                return;

            case Phase.SwitchTab:
            {
                var shop = GetShopAddon();
                if (shop == null)
                {
                    if (GetSelectAddon() != null)
                    {
                        phase = Phase.SelectDialog;
                        return;
                    }

                    phase = Phase.OpenShop;
                    return;
                }

                if (TryFindFixative(shop, out _))
                {
                    phase = Phase.Buy;
                    return;
                }

                if (DateTime.UtcNow - lastTabAt > TimeSpan.FromMilliseconds(400))
                {
                    FireCallback(shop, 4, -1, 1, ShopCategoryOther);
                    lastTabAt = DateTime.UtcNow;
                    status = "切换到「其他」...";
                }
                return;
            }

            case Phase.Buy:
            {
                var shop = GetShopAddon();
                if (shop == null)
                {
                    phase = Phase.OpenShop;
                    return;
                }

                if (!TryFindFixative(shop, out var index))
                {
                    phase = Phase.SwitchTab;
                    return;
                }

                var currencyId = buyingGold ? GoldItemId : SilverItemId;
                var price = buyingGold ? GoldPrice : SilverPrice;
                var currency = GetItemCount(currencyId);
                buyQty = Math.Clamp((int)(currency / (uint)price), 0, plugin.Config.MaxBottlesPerTrip);
                if (buyQty <= 0)
                {
                    SkipCurrentAndContinue("币量不足");
                    return;
                }

                currencyBefore = currency;
                fixativeBefore = GetItemCount(FixativeItemId);
                if (!FireCallback(shop, 0, index, buyQty))
                {
                    SkipCurrentAndContinue("下单失败");
                    return;
                }

                buyAttempts++;
                buySentAt = DateTime.UtcNow;
                phase = Phase.Confirm;
                status = $"已下单 {(buyingGold ? "白金" : "白银")} x{buyQty}";
                return;
            }

            case Phase.Confirm:
                AutoConfirm();
                if (DateTime.UtcNow - buySentAt > TimeSpan.FromMilliseconds(200))
                    phase = Phase.Verify;
                return;

            case Phase.Verify:
                AutoConfirm();
                {
                    var currencyId = buyingGold ? GoldItemId : SilverItemId;
                    var currencyNow = GetItemCount(currencyId);
                    var fixativeNow = GetItemCount(FixativeItemId);
                    if (currencyNow < currencyBefore || fixativeNow > fixativeBefore)
                    {
                        anySuccess = true;
                        status = buyingGold ? "白金店购买成功" : "白银店购买成功";
                        log.Information(status);
                        MarkCurrentDone();
                        CloseShop();
                        phase = Phase.Closing;
                        phaseAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(350);
                        return;
                    }

                    if (DateTime.UtcNow - buySentAt < TimeSpan.FromMilliseconds(1500))
                        return;

                    if (buyAttempts < 2)
                    {
                        phase = Phase.Buy;
                        return;
                    }

                    SkipCurrentAndContinue("未扣币");
                    return;
                }

            case Phase.Closing:
                if (DateTime.UtcNow < phaseAt) return;
                CloseShop();
                if (!goldDone || !silverDone)
                {
                    buyingGold = !goldDone;
                    buyAttempts = 0;
                    phase = Phase.OpenShop;
                    phaseAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
                    status = buyingGold ? "继续白金店" : "继续白银店";
                    return;
                }

                phase = Phase.RestartFarmer;
                phaseAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
                return;

            case Phase.RestartFarmer:
                if (DateTime.UtcNow < phaseAt) return;
                Finish(restart: resumeFarmer);
                return;
        }
    }

    private void MarkCurrentDone()
    {
        if (buyingGold) goldDone = true;
        else silverDone = true;
    }

    private void SkipCurrentAndContinue(string reason)
    {
        log.Warning($"买固定剂跳过当前店：{reason}");
        status = reason;
        MarkCurrentDone();
        CloseShop();
        phase = Phase.Closing;
        phaseAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
    }

    private void Finish(bool restart)
    {
        CloseShop();
        phase = Phase.Idle;
        status = anySuccess ? "购买完成" : "购买未成功";
        resumeFarmer = false;
        if (restart)
        {
            send("/ocnstart");
            status += "，已 /ocnstart";
            log.Information("购买结束，已发送 /ocnstart");
        }

        plugin.OnFixativeBuyFinished(anySuccess);
    }

    private bool IsAtCamp()
    {
        var player = objects.LocalPlayer;
        if (player == null) return false;
        var p = player.Position;
        var dx = p.X - StartCrystal.X;
        var dz = p.Z - StartCrystal.Z;
        return MathF.Sqrt(dx * dx + dz * dz) <= CampRange;
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

    private bool TryInteractNpc(uint preferredEventId)
    {
        try
        {
            foreach (var obj in objects)
            {
                if (obj == null || !obj.IsValid() || obj.ObjectKind != ObjectKind.EventNpc)
                    continue;
                if (obj.BaseId != NpcDataId)
                    continue;
                if (!obj.IsTargetable || obj.Address == nint.Zero)
                    continue;

                var go = (GameObject*)obj.Address;
                var ts = TargetSystem.Instance();
                if (ts == null || go == null)
                    return false;

                // Interact opens the topic list; dialog select picks the shop.
                _ = preferredEventId;
                ts->InteractWithObject(go, true);
                return true;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "交互古钱鉴定师失败");
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
            if (addon == null || !addon->IsReady) return null;
            return addon;
        }
        catch
        {
            return null;
        }
    }

    private AtkUnitBase* GetShopAddon()
    {
        var a = GetAddon("ShopExchangeCurrency");
        if (a != null) return a;
        a = GetAddon("InclusionShop");
        if (a != null) return a;
        return GetAddon("ShopExchangeItem");
    }

    private AtkUnitBase* GetSelectAddon()
    {
        var a = GetAddon("SelectString");
        if (a != null) return a;
        return GetAddon("SelectIconString");
    }

    private void ClickTalk()
    {
        var talk = GetAddon("Talk");
        if (talk == null) return;
        try { talk->FireCallbackInt(0); } catch { /* ignored */ }
    }

    private bool TrySelectShopDialog(bool gold)
    {
        var keywords = gold ? "十二城邦白金币" : "十二城邦白银币（其他）";
        var select = GetAddon("SelectString");
        if (select != null)
        {
            var index = FindSelectIndex(select, false, keywords, gold ? 2 : 1);
            if (index >= 0)
                return FireCallback(select, index);
        }

        var icon = GetAddon("SelectIconString");
        if (icon != null)
        {
            var index = FindSelectIndex(icon, true, keywords, gold ? 2 : 1);
            if (index >= 0)
                return FireCallback(icon, index);
        }

        return false;
    }

    private static int FindSelectIndex(AtkUnitBase* addon, bool isIcon, string keywords, int fallback)
    {
        try
        {
            var count = isIcon
                ? ((AddonSelectIconString*)addon)->PopupMenu.PopupMenu.EntryCount
                : ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryCount;
            for (var i = 0; i < count && i < 16; i++)
            {
                var text = GetSelectEntryText(addon, isIcon, i);
                if (string.IsNullOrEmpty(text)) continue;
                if (text.Contains("780", StringComparison.Ordinal)) continue;
                if (text.Contains(keywords, StringComparison.Ordinal))
                    return i;
            }

            return Math.Clamp(fallback, 0, Math.Max(0, count - 1));
        }
        catch
        {
            return -1;
        }
    }

    private static string GetSelectEntryText(AtkUnitBase* addon, bool isIcon, int index)
    {
        try
        {
            if (addon->AtkValues == null) return string.Empty;
            var slot = 7 + index;
            if (slot >= addon->AtkValuesCount) return string.Empty;
            ref var value = ref addon->AtkValues[slot];
            if (value.Type == AtkValueType.String && value.String.HasValue)
                return value.String.ToString() ?? string.Empty;
        }
        catch
        {
            // ignored
        }

        return string.Empty;
    }

    private static bool TryFindFixative(AtkUnitBase* shop, out int buyIndex)
    {
        buyIndex = -1;
        try
        {
            if (shop == null || shop->AtkValues == null) return false;
            var count = shop->AtkValuesCount;
            if (count > 4)
            {
                var entryCount = shop->AtkValues[4].UInt;
                var num = Math.Min((int)entryCount, 40);
                for (var i = 0; i < num; i++)
                {
                    var idSlot = 1066 + i;
                    var indexSlot = 1310 + i;
                    if (idSlot >= count) break;
                    if (shop->AtkValues[idSlot].UInt != FixativeItemId) continue;
                    buyIndex = indexSlot < count ? (int)shop->AtkValues[indexSlot].UInt : i;
                    if (buyIndex < 0) buyIndex = i;
                    return true;
                }
            }
        }
        catch
        {
            buyIndex = -1;
        }

        return false;
    }

    private void AutoConfirm()
    {
        var itemDialog = GetAddon("ShopExchangeItemDialog");
        if (itemDialog != null)
            FireCallback(itemDialog, 0, buyQty);

        var currencyDialog = GetAddon("ShopExchangeCurrencyDialog");
        if (currencyDialog != null)
            FireCallback(currencyDialog, 0, buyQty);

        var yesno = GetAddon("SelectYesno");
        if (yesno != null && GetShopAddon() != null)
            FireCallback(yesno, 0);
    }

    private void CloseShop()
    {
        try
        {
            foreach (var name in new[]
                     {
                         "ShopExchangeCurrency", "InclusionShop", "ShopExchangeItem",
                         "SelectString", "SelectIconString", "Talk",
                         "ShopExchangeItemDialog", "ShopExchangeCurrencyDialog", "SelectYesno",
                     })
            {
                var addon = GetAddon(name);
                if (addon != null)
                    addon->Close(false);
            }
        }
        catch
        {
            // ignored
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
}
