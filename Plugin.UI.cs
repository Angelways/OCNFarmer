using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace NorthIslandChestPlugin;

public sealed partial class Plugin
{
    private static readonly Vector2 DefaultFullWindowSize = new(980f, 760f);
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
        var spacing = MathF.Max(2f, style.ItemSpacing.X - 3f);
        // Match WindowHost's native title-bar layout: two native buttons, each
        // using the ImGui font size plus ItemInnerSpacing.
        var nativeButtonsWidth = 2f * (ImGui.GetFontSize() + style.ItemInnerSpacing.X);
        var defaultTitleBarButtonsWidth = nativeButtonsWidth + spacing;
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
        DrawPureBlurBackground(0.82f, true);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 14f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 8f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.025f, 0.03f, 0.035f, 0.72f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.03f, 0.04f, 0.045f, 0.2f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.16f, 0.18f, 0.2f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.24f, 0.27f, 0.29f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.3f, 0.33f, 0.34f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.27f, 0.31f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.42f, 0.44f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.78f, 0.6f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, new Vector4(0.22f, 0.86f, 0.8f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);

        try
        {
            DrawTitleBarControls();
            if (config.SimplifiedUi)
            {
                DrawSimplifiedHeader();
                return;
            }

            DrawBHeader();
            DrawBStatusSummary();
            ImGui.Spacing();

            var available = ImGui.GetContentRegionAvail();
            var workspaceSize = new Vector2(MathF.Max(0f, available.X - 2f), available.Y);
            ImGui.BeginChild("BWorkspace", workspaceSize, false);
            ImGui.BeginChild("BSettings", new Vector2(MathF.Max(0f, workspaceSize.X - 28f), -1f), false);
            DrawBConfigurationPanel();
            ImGui.EndChild();
            ImGui.EndChild();
        }
        finally
        {
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(9);
            ImGui.PopStyleVar(4);
        }
    }

    private void DrawSimplifiedHeader()
    {
        ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), "// OCNFarmer");
        ImGui.SameLine();
        ImGui.Text($"银箱 {Math.Max(0, silver):00}/{MaxSilver:00}  ·  铜箱 {Math.Max(0, copper):00}/{MaxCopper:00}");
        ImGui.Spacing();
        ImGui.TextColored(running ? new Vector4(0.28f, 0.9f, 0.72f, 1f) : new Vector4(0.65f, 0.67f, 0.68f, 1f), running ? "● 运行中" : "○ 已停止");
        ImGui.SameLine();
        ImGui.Text($"当前选择模式：{(activeProfile.Target == IslandTarget.SouthHorn ? "南征之章" : "北征之章")}");
    }

    private static void DrawPureBlurBackground(float strength, bool border)
    {
        if (ImGui.GetWindowViewport().ID != ImGui.GetMainViewport().ID)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var rounding = 4f * ImGuiHelpers.GlobalScale;
        ImGuiHelpers.PrependBlurBehind(
            drawList,
            min,
            max,
            strength,
            rounding,
            tintColor: new Vector4(0f, 0f, 0f, 0f),
            luminosityColor: new Vector4(0f, 0f, 0f, 0f),
            noiseOpacity: 0f);

        if (border)
        {
            drawList.AddRect(
                min,
                max,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.24f)),
                rounding,
                ImDrawFlags.RoundCornersAll,
                1.1f * ImGuiHelpers.GlobalScale);
        }
    }

    private void DrawBHeader()
    {
        ImGui.BeginGroup();
        ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), "// OCNFarmer");
        ImGui.TextDisabled($"OCFA Agent  /  v{PluginVersion}");
        ImGui.EndGroup();
        ImGui.SameLine();
        ImGui.TextColored(running ? new Vector4(0.28f, 0.9f, 0.72f, 1f) : new Vector4(0.65f, 0.67f, 0.68f, 1f), running ? "● 运行中" : "○ 已停止");
        ImGui.SameLine();
        ImGui.TextDisabled($"当前选择模式 {activeProfile.ChapterName}");
        ImGui.Separator();
    }

    private void DrawBStatusSummary()
    {
        ImGui.Indent(8f);
        if (!ImGui.BeginTable("BStatusSummary", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.Unindent(8f);
            return;
        }
        ImGui.TableSetupColumn("BStatusMain", ImGuiTableColumnFlags.WidthFixed, 280f);
        ImGui.TableSetupColumn("BMode", ImGuiTableColumnFlags.WidthFixed, 220f);
        ImGui.TableSetupColumn("BSilver", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("BCopper", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled("当前任务");
        ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), status);
        ImGui.TableNextColumn();
        ImGui.Text("寻宝副本选择");
        var locked = IsProfileSelectionLocked();
        ImGui.BeginDisabled(locked);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.08f, 0.58f, 0.56f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.1f, 0.4f, 0.41f, 1f));
        if (ImGui.RadioButton("南征之章（南岛）##SummarySouth", config.IslandTarget == IslandTarget.SouthHorn))
            SelectIslandTarget(IslandTarget.SouthHorn);
        if (ImGui.RadioButton("北征之章（北岛）##SummaryNorth", config.IslandTarget == IslandTarget.NorthHorn))
            SelectIslandTarget(IslandTarget.NorthHorn);
        ImGui.PopStyleColor(2);
        ImGui.EndDisabled();
        ImGui.TableNextColumn();
        ImGui.TextDisabled("银箱");
        ImGui.Text($"{(silver >= 0 ? silver : 0):00} / {MaxSilver:00}");
        ImGui.TextDisabled("容量");
        ImGui.TableNextColumn();
        ImGui.TextDisabled("铜箱");
        ImGui.Text($"{(copper >= 0 ? copper : 0):00} / {MaxCopper:00}");
        ImGui.TextDisabled("容量");
        ImGui.EndTable();
        ImGui.Unindent(8f);
    }

    private void DrawBProgressRail()
    {
        ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), "自动流程");
        ImGui.TextDisabled("自动流程路线");
        ImGui.Spacing();
        var current = ResolveBProgressStep();
        DrawBProgressStep("01 / 进入", "区域同步完成", current >= 1, current == 1);
        DrawBProgressStep("02 / 检测", "钱币与宝箱", current >= 2, current == 2);
        DrawBProgressStep("03 / 战斗", "BOCCHI 运行中", current >= 3, current == 3);
        DrawBProgressStep("04 / 寻宝", "等待宝箱上限", current >= 4, current == 4);
        DrawBProgressStep("05 / 重进", "自动循环", current >= 5, current == 5);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("当前区域 ID");
        ImGui.Text($"{clientState.TerritoryType}");
        ImGui.TextDisabled(activeProfile.TerritoryId == clientState.TerritoryType ? "区域匹配" : "区域不匹配");
        ImGui.Spacing();
        ImGui.TextDisabled("战斗辅助职业");
        ImGui.Text(combatJob);
        if (silver >= 0 && copper >= 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("宝箱负载");
            ImGui.Text($"银 {silver}/{MaxSilver}  ·  铜 {copper}/{MaxCopper}");
        }
    }

    private static void DrawBProgressStep(string title, string detail, bool complete, bool active)
    {
        var color = active
            ? new Vector4(0.95f, 0.88f, 0.02f, 1f)
            : complete
                ? new Vector4(0.28f, 0.9f, 0.72f, 1f)
                : new Vector4(0.47f, 0.49f, 0.5f, 1f);
        ImGui.TextColored(color, active ? "◆" : complete ? "■" : "□");
        ImGui.SameLine();
        ImGui.TextColored(color, title);
        ImGui.TextDisabled($"    {detail}");
        ImGui.Spacing();
    }

    private int ResolveBProgressStep()
    {
        if (!running) return 0;
        if (treasurePhase != TreasurePhase.None) return 4;
        if (currencyBuyer.IsBusy || currencyPurchaseMoveActive) return 2;
        if (waitingForEntry || islandSwitchPending) return 1;
        if (initialScan || waitingForScan || pendingScanAt != DateTime.MinValue) return 2;
        return 3;
    }

    private void DrawBConfigurationPanel()
    {
        ImGui.Dummy(new Vector2(0f, 6f));
        DrawBSectionTitle("副本与战斗配置", "副本设置");
        DrawBProfileConfig();
        ImGui.Spacing();
        DrawBUsageInstructions();
        ImGui.Spacing();
        DrawAutomaticPurchaseConfig();
        ImGui.Spacing();
        DrawServerChanConfig();
        ImGui.Spacing();
        DrawBTowerConfig();
        ImGui.Spacing();
        DrawBDebug();
        ImGui.Spacing();
        if (!string.IsNullOrEmpty(treasureError))
            ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"错误：{treasureError}");
        DrawBFooter();
    }

    private static void DrawBSectionTitle(string title, string code)
    {
        ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), "◇");
        ImGui.SameLine();
        ImGui.Text(title);
        ImGui.SameLine();
        ImGui.TextDisabled(code);
        ImGui.Separator();
    }

    private void DrawBProfileConfig()
    {
        const float profileControlWidth = 250f;
        if (ImGui.BeginTable("BProfileConfig", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("BProfileLabel", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("BProfileValue", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + 8f);
            ImGui.TableNextColumn();
            ImGui.Text("战斗辅助职业");
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(profileControlWidth);
            if (ImGui.BeginCombo("##BCombatJob", combatJob))
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
            ImGui.SameLine(0f, 6f);
            DrawInfoIcon(
                "BCombatJobInfo",
                "注意：有些辅助职业的辅助技能可能与魔寻宝 CD 存在冲突，不接受因此所产生问题的反馈。默认选择的辅助白魔法师无此问题");

            ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + 8f);
            ImGui.TableNextColumn();
            ImGui.Text("DR 自动丢弃预设");
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(profileControlWidth);
            if (ImGui.InputText("##BDiscardPreset", ref discardPreset, 128))
            {
                config.DiscardPreset = discardPreset;
                config.Save();
            }
            ImGui.SameLine(0f, 6f);
            DrawInfoIcon(
                "BDiscardPresetInfo",
                "如需自动丢弃跑刀垃圾，请在此处填写 DR 自动丢弃物品模块的预设名称，留空则不启用");

            ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + 8f);
            ImGui.TableNextColumn();
            ImGui.Text("寻宝模式选择");
            ImGui.TableNextColumn();
            var treasureModeText = config.TreasureModeSelection == TreasureMode.XszRun ? "XSZ 跑刀" : "DR 跑刀";
            ImGui.SetNextItemWidth(profileControlWidth);
            if (ImGui.BeginCombo("##BTreasureMode", treasureModeText))
            {
                if (ImGui.Selectable("DR 跑刀", config.TreasureModeSelection == TreasureMode.DrRun))
                {
                    config.TreasureModeSelection = TreasureMode.DrRun;
                    config.Save();
                }
                if (ImGui.Selectable("XSZ 跑刀", config.TreasureModeSelection == TreasureMode.XszRun))
                {
                    config.TreasureModeSelection = TreasureMode.XszRun;
                    config.Save();
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine(0f, 6f);
            DrawInfoIcon(
                "BTreasureModeInfo",
                "XSZ 跑刀为 XSZToolbox 测试码功能，如果你没有权限则不要选择这个模式。");
            ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + 6f);
            ImGui.TableNextColumn();
            ImGui.Text("寻宝记录");
            ImGui.TableNextColumn();
            if (ImGui.Button("查看寻宝战利品记录", new Vector2(profileControlWidth, 0f)))
                treasureHistoryWindow.IsOpen = true;
            ImGui.EndTable();
        }
    }

    private static void DrawInfoIcon(string id, string tooltip)
    {
        var size = new Vector2(20f, 20f);
        ImGui.InvisibleButton($"##{id}", size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var center = (min + max) * 0.5f;
        var drawList = ImGui.GetWindowDrawList();
        var accent = ImGui.GetColorU32(new Vector4(0.95f, 0.88f, 0.02f, 1f));
        drawList.AddCircle(center, 8f, accent, 24, 1.4f);
        var textSize = ImGui.CalcTextSize("!");
        drawList.AddText(center - textSize * 0.5f, accent, "!");
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360f);
            ImGui.TextWrapped(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private void DrawBUsageInstructions()
    {
        ImGui.SetNextItemOpen(true, ImGuiCond.Once);
        if (!ImGui.CollapsingHeader("使用说明")) return;
        ImGui.TextWrapped("1. 本插件功能为高危行为，如介意请勿使用；");
        ImGui.TextWrapped("2. 使用本插件的必须条件：");
        ImGui.TextWrapped("   1）启用 BOCCHI 及其配套插件，并且【关闭】自动轮换副本功能；");
        ImGui.TextWrapped("   2）启用 Daily Routines 插件，并启用下列模块：");
        ImGui.TextWrapped("      ① 蜃景幻界新月岛 助手　② 更好的辅助职业列表　③ 辅助职业切换指令");
        ImGui.TextWrapped("      ④ 自动任务出发确认　⑤ 即刻退本　⑥ 特殊场景探索进入指令");
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

    private void DrawBTowerConfig()
    {
        if (!activeProfile.SupportsTower) return;
        ImGui.SetNextItemOpen(config.AutoGoTowerExpanded, ImGuiCond.Once);
        var expanded = ImGui.CollapsingHeader("自动前往魔之塔设置");
        if (expanded != config.AutoGoTowerExpanded)
        {
            config.AutoGoTowerExpanded = expanded;
            config.Save();
        }
        if (!expanded) return;
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

    private void DrawBDebug()
    {
        if (!ImGui.CollapsingHeader("Debug")) return;
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

    private void DrawBFooter()
    {
        ImGui.Separator();
        ImGui.TextDisabled(running ? "运行中" : "已停止");
        ImGui.SameLine();
        if (running)
        {
            if (ImGui.Button("停止运行")) Stop();
        }
        else if (ImGui.Button("开始运行")) Start();
        ImGui.SameLine();
        if (ImGui.Button("关闭窗口")) mainWindow.IsOpen = false;
    }

    private sealed class MainWindow : Dalamud.Interface.Windowing.Window
    {
        private readonly Plugin plugin;
        private bool simplifiedMode;
        private int sizeRestoreFrames;
        private bool titleBarSpacingPushed;

        public MainWindow(Plugin plugin) : base($"OCNFarmer v{PluginVersion}##OCNFarmer")
        {
            this.plugin = plugin;
            Flags |= ImGuiWindowFlags.NoCollapse;
            simplifiedMode = plugin.config.SimplifiedUi;
            var savedSize = GetSavedSize();
            if (savedSize.X > 0 && savedSize.Y > 0)
            {
                if (!simplifiedMode && (savedSize.X < 820f || savedSize.Y < 620f))
                    savedSize = DefaultFullWindowSize;
                Size = savedSize;
                SizeCondition = ImGuiCond.Always;
                sizeRestoreFrames = 1;
            }
            else if (!simplifiedMode)
            {
                Size = DefaultFullWindowSize;
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

        public override void PreDraw()
        {
            base.PreDraw();
            // Apply the same spacing to Dalamud's native title-bar controls so
            // the three plugin buttons and the two native controls form one
            // evenly spaced group.
            ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(7f, ImGui.GetStyle().ItemInnerSpacing.Y));
            titleBarSpacingPushed = true;
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
            if (titleBarSpacingPushed)
            {
                ImGui.PopStyleVar();
                titleBarSpacingPushed = false;
            }
            var size = ImGui.GetWindowSize();
            if (size.X <= 0 || size.Y <= 0) return;
            if (sizeRestoreFrames > 0)
            {
                sizeRestoreFrames--;
                if (sizeRestoreFrames == 0)
                {
                    SizeCondition = ImGuiCond.FirstUseEver;
                    // Size is only needed to apply a saved/default size during
                    // initialization. Keeping it populated can reapply that
                    // value when Dalamud recreates the window.
                    Size = null;
                }
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

    private sealed class TreasureHistoryWindow : Dalamud.Interface.Windowing.Window
    {
        private readonly Plugin plugin;
        private int filter;

        public TreasureHistoryWindow(Plugin plugin) : base("寻宝战利品##OCNFarmerTreasureHistory")
        {
            this.plugin = plugin;
            Flags |= ImGuiWindowFlags.NoCollapse;
            Size = new Vector2(760f, 560f);
            SizeCondition = ImGuiCond.FirstUseEver;
            IsOpen = false;
        }

        public override void Draw()
        {
            DrawPureBlurBackground(0.82f, true);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 14f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(10f, 8f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.025f, 0.03f, 0.035f, 0.72f));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.27f, 0.31f, 0.98f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.36f, 0.42f, 0.44f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.08f, 0.58f, 0.56f, 1f));
            try
            {
                ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), "寻宝战利品");
                ImGui.SameLine();
                ImGui.TextDisabled($"共 {plugin.treasureRecords.Count} 次");
                ImGui.Separator();
                DrawFilterButton("全部", 0);
                ImGui.SameLine();
                DrawFilterButton("今日", 1);
                ImGui.SameLine();
                DrawFilterButton("本周", 2);
                ImGui.SameLine();
                DrawFilterButton("本月", 3);
                var now = DateTime.Now;
                var today = now.Date;
                var weekStart = today.AddDays(-(int)today.DayOfWeek);
                var monthStart = new DateTime(today.Year, today.Month, 1);
                var filtered = plugin.treasureRecords.Where(record => filter switch
                {
                    1 => record.CompletedAt >= today,
                    2 => record.CompletedAt >= weekStart,
                    3 => record.CompletedAt >= monthStart,
                    _ => true,
                }).ToList();
                ImGui.SameLine();
                ImGui.TextDisabled($"筛选结果 {filtered.Count} 次");
                ImGui.TextDisabled($"今日 {plugin.treasureRecords.Count(x => x.CompletedAt >= today)} 次  ·  本周 {plugin.treasureRecords.Count(x => x.CompletedAt >= weekStart)} 次  ·  本月 {plugin.treasureRecords.Count(x => x.CompletedAt >= monthStart)} 次");
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), "物品获得统计");
                var lootTotals = filtered
                    .SelectMany(record => record.Loot ?? new Dictionary<string, int>())
                    .GroupBy(item => item.Key, StringComparer.Ordinal)
                    .Select(group => new { Name = group.Key, Count = group.Sum(item => item.Value) })
                    .OrderBy(item => item.Count)
                    .ThenBy(item => item.Name, StringComparer.Ordinal)
                    .ToList();
                if (lootTotals.Count == 0)
                    ImGui.TextDisabled("当前筛选范围内暂无战利品记录");
                else if (ImGui.BeginTable("TreasureLootTotalsTable", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("累计获得", ImGuiTableColumnFlags.WidthFixed, 110f);
                    ImGui.TableHeadersRow();
                    foreach (var item in lootTotals)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text(item.Name);
                        ImGui.TableNextColumn();
                        ImGui.Text($"×{item.Count}");
                    }
                    ImGui.EndTable();
                }
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.95f, 0.88f, 0.02f, 1f), "寻宝记录明细");
                if (ImGui.BeginTable("TreasureHistoryTable", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY, new Vector2(0f, -1f)))
                {
                    ImGui.TableSetupColumn("完成时间", ImGuiTableColumnFlags.WidthFixed, 150f);
                    ImGui.TableSetupColumn("副本", ImGuiTableColumnFlags.WidthFixed, 110f);
                    ImGui.TableSetupColumn("战利品", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableHeadersRow();
                    foreach (var record in filtered)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text(record.CompletedAt.ToString("yyyy-MM-dd HH:mm"));
                        ImGui.TableNextColumn();
                        ImGui.Text(record.Island == IslandTarget.SouthHorn ? "南征之章" : "北征之章");
                        ImGui.TableNextColumn();
                        var loot = record.Loot.Count == 0
                            ? "未检测到获得物品消息"
                            : string.Join("、", record.Loot.OrderBy(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}×{x.Value}"));
                        ImGui.TextWrapped(loot);
                    }
                    ImGui.EndTable();
                }
            }
            finally
            {
                ImGui.PopStyleColor(4);
                ImGui.PopStyleVar(3);
            }
        }

        private void DrawFilterButton(string label, int value)
        {
            // Keep the ImGui style stack balanced even when the click changes filter.
            var selectedBeforeClick = filter == value;
            if (selectedBeforeClick) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.08f, 0.58f, 0.56f, 1f));
            if (ImGui.SmallButton(label)) filter = value;
            if (selectedBeforeClick) ImGui.PopStyleColor();
        }
    }
}
