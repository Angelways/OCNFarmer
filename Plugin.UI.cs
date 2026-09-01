using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace NorthIslandChestPlugin;

public sealed partial class Plugin
{
    private static readonly Vector2 DefaultSimplifiedWindowSize = new(520f, 140f);
    private enum TitleBarIcon { Play, Stop, FullView, CompactView }

    private void OpenServerChanDocs()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://doc.sc3.ft07.com/zh/serverchan3") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.Error(ex, "打开 Server酱用户文档失败");
        }
    }

    private void DrawAutomaticPurchaseConfig()
    {
        ImGui.SetNextItemOpen(config.AutoPurchaseExpanded, ImGuiCond.Once);
        var expanded = ImGui.CollapsingHeader("自动购买配置");
        if (expanded != config.AutoPurchaseExpanded)
        {
            config.AutoPurchaseExpanded = expanded;
            config.Save();
        }
        if (!expanded) return;

        DrawCurrencyPurchaseConfig(CurrencyKind.Silver);
        ImGui.Separator();
        DrawCurrencyPurchaseConfig(CurrencyKind.Gold);
        if (!string.IsNullOrWhiteSpace(currencyPurchaseStatus))
            ImGui.TextWrapped($"购买状态：{currencyPurchaseStatus}");
    }

    private void DrawServerChanConfig()
    {
        ImGui.SetNextItemOpen(config.ServerChanExpanded, ImGuiCond.Once);
        var expanded = ImGui.CollapsingHeader("无人值守通知设置");
        if (expanded != config.ServerChanExpanded)
        {
            config.ServerChanExpanded = expanded;
            config.Save();
        }
        if (!expanded) return;

        ImGui.TextWrapped("通过Server酱，插件可实现在指定条件达成时为你的手机或者其他设备发送一个包含战利品清单的通知。具体参阅");
        if (ImGui.SmallButton("Server酱用户文档")) OpenServerChanDocs();

        var enabled = config.ServerChanEnabled;
        if (ImGui.Checkbox("启用通知功能", ref enabled))
        {
            config.ServerChanEnabled = enabled;
            config.Save();
        }
        if (!config.ServerChanEnabled) return;

        ImGui.TextWrapped("在此处填写Server酱SendKey页面获取到的API URL");
        var apiUrl = config.ServerChanApiUrl;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("API URL", ref apiUrl, 512))
        {
            config.ServerChanApiUrl = apiUrl;
            config.Save();
        }

        var notifyProblem = config.NotifyProblem;
        if (ImGui.Checkbox("插件出现问题时通知", ref notifyProblem))
        {
            config.NotifyProblem = notifyProblem;
            config.Save();
        }
        var notifyComplete = config.NotifyTreasureComplete;
        if (ImGui.Checkbox("寻宝完成时通知", ref notifyComplete))
        {
            config.NotifyTreasureComplete = notifyComplete;
            config.Save();
        }
        if (activeProfile.SupportsTower)
        {
            var notifyTowerWeather = config.NotifyTowerWeather;
            if (ImGui.Checkbox("魔之塔蜃景天气出现时通知", ref notifyTowerWeather))
            {
                config.NotifyTowerWeather = notifyTowerWeather;
                config.Save();
            }
        }

        if (ImGui.Button("发送通知测试"))
        {
            SendServerChanNotificationAsync(
                "OCNFarmer",
                "这是一条来自OCNFarmer插件的测试消息，如果收到了此消息，说明你的Server酱配置正常。",
                "测试通知");
        }
    }

    private void DrawCurrencyPurchaseConfig(CurrencyKind kind)
    {
        var settings = GetPurchaseSettings(activeProfile);
        var silverKind = kind == CurrencyKind.Silver;
        var name = silverKind ? activeProfile.SilverCurrencyName : activeProfile.GoldCurrencyName;
        var widgetId = $"{activeProfile.Target}{kind}";
        var currentAmount = silverKind ? silverCurrency : goldCurrency;
        var mode = silverKind ? settings.SilverMode : settings.GoldMode;
        var trigger = silverKind ? settings.SilverTriggerAmount : settings.GoldTriggerAmount;
        var modeText = mode switch
        {
            CurrencyPurchaseMode.OldCoffer => "自动买钱箱",
            CurrencyPurchaseMode.UltimateFixative => "自动买终极固定剂",
            _ => "不购买",
        };

        ImGui.Text($"{name}：{(currentAmount >= 0 ? currentAmount.ToString() : "未检测")}/{CurrencyCap}");
        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo($"行为##{widgetId}PurchaseMode", modeText))
        {
            foreach (var candidate in Enum.GetValues<CurrencyPurchaseMode>())
            {
                if (candidate == CurrencyPurchaseMode.UltimateFixative && !activeProfile.SupportsFixative)
                    continue;

                var label = candidate switch
                {
                    CurrencyPurchaseMode.OldCoffer => "自动买钱箱",
                    CurrencyPurchaseMode.UltimateFixative => "自动买终极固定剂（仅北征）",
                    _ => "不购买",
                };
                if (ImGui.Selectable(label, candidate == mode))
                {
                    mode = candidate;
                    SetPurchaseMode(settings, kind, mode);
                    ClampSelectedPurchaseQuantity(settings, kind, mode);
                    config.Save();
                }
                if (candidate == mode) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(mode == CurrencyPurchaseMode.None);
        var cost = GetPurchaseCost(kind, mode);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt($"触发钱币数量##{widgetId}Trigger", ref trigger))
        {
            trigger = Math.Clamp(trigger, cost, CurrencyCap);
            if (silverKind) settings.SilverTriggerAmount = trigger;
            else settings.GoldTriggerAmount = trigger;
            ClampSelectedPurchaseQuantity(settings, kind, mode);
            config.Save();
        }

        var quantity = GetConfiguredQuantity(settings, kind, mode);
        var maxAtTrigger = Math.Max(1, trigger / cost);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt($"购买数量##{widgetId}Quantity", ref quantity))
        {
            quantity = Math.Clamp(quantity, 1, maxAtTrigger);
            SetConfiguredQuantity(settings, kind, mode, quantity);
            config.Save();
        }
        if (mode != CurrencyPurchaseMode.None)
            ImGui.TextWrapped($"单价：{cost} {name}；触发值下最多可购买 {maxAtTrigger} 个。");
        ImGui.EndDisabled();
    }

    private void DrawIslandTargetConfig()
    {
        ImGui.Text("目标副本");
        var locked = IsProfileSelectionLocked();
        ImGui.BeginDisabled(locked);
        if (ImGui.RadioButton("北征之章（北岛）##IslandNorth", config.IslandTarget == IslandTarget.NorthHorn))
            SelectIslandTarget(IslandTarget.NorthHorn);
        ImGui.SameLine();
        if (ImGui.RadioButton("南征之章（南岛）##IslandSouth", config.IslandTarget == IslandTarget.SouthHorn))
            SelectIslandTarget(IslandTarget.SouthHorn);
        ImGui.EndDisabled();
    }

    private void DrawTitleBarControls()
    {
        var originalCursor = ImGui.GetCursorScreenPos();
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var style = ImGui.GetStyle();
        const float buttonWidth = 24f;
        const float defaultTitleBarButtonsWidth = 82f;
        var spacing = style.ItemSpacing.X;
        var totalWidth = buttonWidth * 3f + spacing * 2f;
        var maximumX = windowPosition.X + windowSize.X - totalWidth - defaultTitleBarButtonsWidth;
        var buttonX = MathF.Max(windowPosition.X + 8f, maximumX);
        var buttonHeight = MathF.Max(18f, ImGui.GetFrameHeight() - 2f);
        var buttonY = windowPosition.Y + 1f;

        ImGui.PushClipRect(windowPosition, windowPosition + windowSize, false);
        ImGui.SetCursorScreenPos(new Vector2(buttonX, buttonY));
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.2f));
        var buttonSize = new Vector2(buttonWidth, buttonHeight);
        ImGui.BeginDisabled(running || currencyBuyer.IsBusy);
        if (DrawTitleBarIconButton("TitleStart", TitleBarIcon.Play, buttonSize, "启动插件")) Start();
        ImGui.EndDisabled();
        ImGui.SameLine(0f, spacing);
        if (DrawTitleBarIconButton("TitleEmergencyStop", TitleBarIcon.Stop, buttonSize, "紧急停止")) EmergencyStop();
        ImGui.SameLine(0f, spacing);
        var modeIcon = config.SimplifiedUi ? TitleBarIcon.FullView : TitleBarIcon.CompactView;
        var modeTooltip = config.SimplifiedUi ? "切换至完整界面" : "切换至简化界面";
        if (DrawTitleBarIconButton("TitleUiMode", modeIcon, buttonSize, modeTooltip))
            mainWindow.ToggleSimplifiedMode();
        ImGui.PopStyleColor(3);
        ImGui.PopClipRect();
        ImGui.SetCursorScreenPos(originalCursor);
    }

    private static bool DrawTitleBarIconButton(string id, TitleBarIcon icon, Vector2 size, string tooltip)
    {
        var pressed = ImGui.Button($"##{id}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) * 0.5f;
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var drawList = ImGui.GetWindowDrawList();

        switch (icon)
        {
            case TitleBarIcon.Play:
                drawList.AddTriangleFilled(
                    center + new Vector2(-3.5f, -5f),
                    center + new Vector2(-3.5f, 5f),
                    center + new Vector2(5f, 0f),
                    color);
                break;
            case TitleBarIcon.Stop:
                drawList.AddRectFilled(center + new Vector2(-4.5f, -4.5f), center + new Vector2(4.5f, 4.5f), color, 1f);
                break;
            case TitleBarIcon.FullView:
                DrawRectangleOutline(drawList, center + new Vector2(-5.5f, -4f), center + new Vector2(5.5f, 4f), color);
                break;
            case TitleBarIcon.CompactView:
                DrawRectangleOutline(drawList, center + new Vector2(-5.5f, -2.5f), center + new Vector2(3f, 3.5f), color);
                DrawRectangleOutline(drawList, center + new Vector2(-2.5f, -5f), center + new Vector2(5.5f, 1f), color);
                break;
        }

        DrawTitleBarTooltip(tooltip);
        return pressed;
    }

    private static void DrawRectangleOutline(ImDrawListPtr drawList, Vector2 min, Vector2 max, uint color)
    {
        const float thickness = 1.4f;
        drawList.AddLine(min, new Vector2(max.X, min.Y), color, thickness);
        drawList.AddLine(new Vector2(max.X, min.Y), max, color, thickness);
        drawList.AddLine(max, new Vector2(min.X, max.Y), color, thickness);
        drawList.AddLine(new Vector2(min.X, max.Y), min, color, thickness);
    }

    private static void DrawTitleBarTooltip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
    }

    public void DrawStatus()
    {
        DrawTitleBarControls();
        ImGui.Text($"状态：{status}");
        if (config.SimplifiedUi)
        {
            ImGui.Text($"目标副本：{activeProfile.ChapterName}");
            ImGui.Text($"插件运行状态：{(running ? "运行中" : "已停止")}");
            return;
        }

        ImGui.Text($"当前区域 ID：{clientState.TerritoryType}（目标 {activeProfile.TerritoryId}，{activeProfile.ChapterName}）");
        ImGui.Spacing();
        DrawIslandTargetConfig();
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("使用说明"))
        {
            ImGui.TextWrapped("使用说明");
            ImGui.TextWrapped("1. 本插件功能为高危行为，如介意请勿使用；");
            ImGui.TextWrapped("2. 使用本插件的必须条件：");
            ImGui.TextWrapped("   1）启用 BOCCHI 及其配套插件，并且【关闭】自动轮换副本功能；");
            ImGui.TextWrapped("   2）启用 Daily Routines 插件，并启用下列模块：");
            ImGui.TextWrapped("      ① 蜃景幻界新月岛 助手");
            ImGui.TextWrapped("      ② 更好的辅助职业列表");
            ImGui.TextWrapped("      ③ 辅助职业切换指令");
            ImGui.TextWrapped("      ④ 自动任务出发确认");
            ImGui.TextWrapped("      ⑤ 即刻退本");
            ImGui.TextWrapped("      ⑥ 特殊场景探索进入指令");
            if (ImGui.Button("一键开启上述模块"))
            {
                Send("/pdr load OccultCrescentHelper");
                Send("/pdr load BetterMKDSupportJobList");
                Send("/pdr load PhantomJobSwitchCommand");
                Send("/pdr load AutoCommenceDuty");
                Send("/pdr load InstantLeaveDuty");
                Send("/pdr load FieldEntryCommand");
                status = "已发送一键开启 Daily Routines 模块指令";
            }
        }
        ImGui.Spacing();
        ImGui.Text("选择 BOCCHI 战斗中的辅助职业");
        if (ImGui.BeginCombo("##CombatJob", combatJob))
        {
            foreach (var job in CombatJobs)
            {
                var selected = job == combatJob;
                if (ImGui.Selectable(job, selected))
                {
                    combatJob = job;
                    config.CombatJob = combatJob;
                    config.Save();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("注意：有些辅助职业的辅助技能可能与魔寻宝 CD 存在冲突，不接受因此所产生问题的反馈。默认选择的辅助白魔法师无此问题");
        ImGui.Spacing();
        DrawAutomaticPurchaseConfig();
        ImGui.Spacing();
        DrawServerChanConfig();
        ImGui.Spacing();
        ImGui.TextWrapped("如需自动丢弃跑刀垃圾，请在此处填写DR自动丢弃物品模块的预设名称，留空则不启用");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##DiscardPreset", ref discardPreset, 128))
        {
            config.DiscardPreset = discardPreset;
            config.Save();
        }
        ImGui.Spacing();
        if (silver >= 0 && copper >= 0) ImGui.Text($"宝箱：银 {silver}/{MaxSilver}，铜 {copper}/{MaxCopper}");
        if (!string.IsNullOrEmpty(treasureError)) ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"错误：{treasureError}");
        if (running)
        {
            if (ImGui.Button("停止脚本")) Stop();
        }
        else if (ImGui.Button("开始运行")) Start();
        ImGui.SameLine();
        if (ImGui.Button("关闭窗口")) mainWindow.IsOpen = false;
        ImGui.Spacing();
        if (activeProfile.SupportsTower)
        {
            ImGui.SetNextItemOpen(config.AutoGoTowerExpanded, ImGuiCond.Once);
            var towerExpanded = ImGui.CollapsingHeader("自动前往魔之塔设置");
            if (towerExpanded != config.AutoGoTowerExpanded)
            {
                config.AutoGoTowerExpanded = towerExpanded;
                config.Save();
            }
            if (towerExpanded)
            {
                ImGui.TextWrapped("注意：该项功能只会在蜃景天气出现时停止插件功能前往魔之塔进入区域，不会自动进行魔之塔战斗，后续流程需要手动或者由其他插件接管。");
                ImGui.TextWrapped("如果你不知道上述是什么意思，则不要开启此功能，也不要就此功能进行任何反馈。");
                var autoGoTower = config.AutoGoTower;
                if (ImGui.Checkbox("蜃景天气出现时自动前往魔之塔区域", ref autoGoTower))
                {
                    config.AutoGoTower = autoGoTower;
                    config.Save();
                    if (autoGoTower && running && IsIsland())
                    {
                        weatherCheckPending = true;
                        nextWeatherCheckAt = DateTime.UtcNow;
                    }
                }
                var notifyTowerArrival = config.NotifyTowerArrival;
                if (ImGui.Checkbox("到达魔之塔进入区域后发送通知", ref notifyTowerArrival))
                {
                    config.NotifyTowerArrival = notifyTowerArrival;
                    config.Save();
                }
            }
            ImGui.Spacing();
        }
        if (ImGui.CollapsingHeader("Debug"))
        {
            if (ImGui.Button("直接开始寻宝流程（测试用）"))
            {
                if (!running) running = true;
                silver = MaxSilver;
                copper = 0;
                BeginTreasureProcedure();
            }
            if (activeProfile.SupportsTower && ImGui.Button("直接开始前往魔之塔流程（测试用）"))
            {
                if (!running) running = true;
                BeginTowerProcedureForTest();
            }
        }
    }

    private sealed class MainWindow : Dalamud.Interface.Windowing.Window
    {
        private readonly Plugin plugin;
        private bool simplifiedMode;
        private int sizeRestoreFrames;

        public MainWindow(Plugin plugin) : base($"OCNFarmer v{PluginVersion}##OCNFarmer")
        {
            this.plugin = plugin;
            simplifiedMode = plugin.config.SimplifiedUi;
            var savedSize = GetSavedSize();
            if (savedSize.X > 0 && savedSize.Y > 0)
            {
                Size = savedSize;
                SizeCondition = ImGuiCond.Always;
                sizeRestoreFrames = 1;
            }
            else if (simplifiedMode)
            {
                Size = DefaultSimplifiedWindowSize;
                SizeCondition = ImGuiCond.Always;
                sizeRestoreFrames = 1;
            }
            IsOpen = false;
        }

        public override void Draw() => plugin.DrawStatus();

        public void ToggleSimplifiedMode()
        {
            var currentSize = ImGui.GetWindowSize();
            if (currentSize.X > 0 && currentSize.Y > 0)
                StoreSize(currentSize);

            simplifiedMode = !simplifiedMode;
            plugin.config.SimplifiedUi = simplifiedMode;
            var targetSize = GetSavedSize();
            if (targetSize.X <= 0 || targetSize.Y <= 0)
                targetSize = simplifiedMode ? DefaultSimplifiedWindowSize : currentSize;
            Size = targetSize;
            SizeCondition = ImGuiCond.Always;
            // 当前帧已经 Begin；保留两帧，确保下一帧真正应用目标尺寸后再恢复正常尺寸条件。
            sizeRestoreFrames = 2;
            plugin.config.Save();
        }

        public override void PostDraw()
        {
            base.PostDraw();
            var size = ImGui.GetWindowSize();
            if (size.X <= 0 || size.Y <= 0) return;
            if (sizeRestoreFrames > 0)
            {
                sizeRestoreFrames--;
                if (sizeRestoreFrames == 0)
                    SizeCondition = ImGuiCond.FirstUseEver;
                return;
            }
            var savedSize = GetSavedSize();
            if (MathF.Abs(savedSize.X - size.X) < 0.5f && MathF.Abs(savedSize.Y - size.Y) < 0.5f) return;
            StoreSize(size);
            plugin.config.Save();
        }

        private Vector2 GetSavedSize() => simplifiedMode
            ? new Vector2(plugin.config.SimplifiedWindowWidth, plugin.config.SimplifiedWindowHeight)
            : new Vector2(plugin.config.WindowWidth, plugin.config.WindowHeight);

        private void StoreSize(Vector2 size)
        {
            if (simplifiedMode)
            {
                plugin.config.SimplifiedWindowWidth = size.X;
                plugin.config.SimplifiedWindowHeight = size.Y;
            }
            else
            {
                plugin.config.WindowWidth = size.X;
                plugin.config.WindowHeight = size.Y;
            }
        }
    }
}
