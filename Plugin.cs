using System.Numerics;
using System.Text.RegularExpressions;
using System.Globalization;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace NorthIslandChestPlugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string PluginVersion = "1.0.0";
    private static readonly string[] CombatJobs =
    {
        "辅助白魔法师", "辅助骑士", "辅助狂战士", "辅助黑魔法师", "辅助青魔法师",
        "辅助炮术师", "辅助炼金术士", "辅助舞者", "辅助龙骑士", "自由人",
        "辅助风水士", "辅助武僧", "辅助死灵法师", "辅助忍者", "辅助预言师",
        "辅助游侠", "辅助赤魔法师", "辅助武士", "辅助召唤师", "辅助盗贼",
        "辅助时间魔法师", "辅助诗人"
    };
    private const uint TreasureGeneralActionSlot = 32;
    private const ulong GeneralActionTarget = 3758096384UL;
    private const uint IslandTerritory = 1346;
    private const int MaxSilver = 8;
    private const int MaxCopper = 30;
    private const float BaseX = 39f;
    private const float BaseZ = 39f;
    private const float BaseRadius = 18f;
    private static readonly TimeSpan IslandLoadDelay = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SubsequentScanInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JobChangeDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReturnScanDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TreasureCommandDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TeleportCheckDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaveDutyDelay = TimeSpan.FromSeconds(5);

    private enum TreasurePhase { None, FirstTeleport, InnerCheck, InnerMount, InnerStart, InnerReturn, SecondTeleport, OuterCheck, OuterMount, OuterStart, OuterReturn, LeaveDuty, Reentry }

    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WindowSystem windows = new("北征宝箱");
    private readonly MainWindow mainWindow;
    private DateTime pendingActionAt = DateTime.MinValue;
    private DateTime pendingScanAt = DateTime.MinValue;
    private DateTime pendingBocchiAt = DateTime.MinValue;
    private DateTime pendingReturnScanAt = DateTime.MinValue;
    private DateTime nextAllowedScanAt = DateTime.MinValue;
    private DateTime treasurePhaseAt = DateTime.MinValue;
    private TreasurePhase treasurePhase;
    private string teleportX = "928";
    private string teleportY = "190";
    private string teleportZ = "743";
    private string combatJob = "辅助白魔法师";
    private Vector3 teleportTarget;
    private string treasureError = "";
    private bool running;
    private bool bocchiEnabled;
    private bool waitingForEntry;
    private bool waitingForScan;
    private bool initialScan;
    private int treasureCastAttempts;
    private int silver = -1;
    private int copper = -1;
    private string status = "未运行";

    public string Name => "OCNFarmer";

    public Plugin(IChatGui chat, IClientState clientState, IObjectTable objects, IFramework framework, ICommandManager commands, IPluginLog log, IDalamudPluginInterface pluginInterface)
    {
        this.chat = chat;
        this.clientState = clientState;
        this.objects = objects;
        this.commands = commands;
        this.framework = framework;
        this.log = log;
        mainWindow = new MainWindow(this);
        windows.AddWindow(mainWindow);

        commands.AddHandler("/ocnchest", new CommandInfo((_, _) => mainWindow.IsOpen = true)
        {
            HelpMessage = "打开 OCNFarmer 设置。",
        });
        chat.ChatMessage += OnChatMessage;
        framework.Update += OnUpdate;
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsOpen = true;
    }

    public void Dispose()
    {
        Stop("已停止");
        chat.ChatMessage -= OnChatMessage;
        framework.Update -= OnUpdate;
        windows.RemoveAllWindows();
    }

    public void Start()
    {
        if (running) return;
        running = true;
        silver = copper = -1;
        initialScan = true;
        if (!IsIsland())
        {
            Send("/pdrfe ocn");
            waitingForEntry = true;
            pendingActionAt = DateTime.UtcNow + IslandLoadDelay;
            status = "正在进入蜃景幻界新月岛 北征之章...";
            return;
        }
        RequestFreelancerScan("副本内首次");
    }

    public void Stop(string message = "已停止")
    {
        if (bocchiEnabled) Send("/bocchiillegal off");
        bocchiEnabled = false;
        running = waitingForEntry = waitingForScan = false;
        pendingActionAt = pendingScanAt = pendingBocchiAt = pendingReturnScanAt = nextAllowedScanAt = DateTime.MinValue;
        treasurePhase = TreasurePhase.None;
        treasurePhaseAt = DateTime.MinValue;
        status = message;
    }

    private void BeginIsland()
    {
        status = "等待副本加载...";
        pendingActionAt = DateTime.UtcNow + IslandLoadDelay;
        waitingForEntry = false;
    }

    private void OnUpdate(IFramework framework)
    {
        if (!running) return;
        if (treasurePhase != TreasurePhase.None)
        {
            UpdateTreasureProcedure();
            return;
        }
        if (!IsIsland())
        {
            if (bocchiEnabled) Send("/bocchiillegal off");
            bocchiEnabled = false;
            status = "当前未在蜃景幻界新月岛 北征之章中，等待重新进入...";
            return;
        }
        // 进岛后等待系统消息“当前任务设有品级同步限制”确认加载完成；保留
        // pendingActionAt 作为极端情况下的超时兜底。
        if (waitingForEntry)
        {
            if (pendingActionAt != DateTime.MinValue && DateTime.UtcNow >= pendingActionAt)
            {
                waitingForEntry = false;
                pendingActionAt = DateTime.UtcNow + JobChangeDelay;
                status = $"未检测到进入副本，按加载超时继续切换{combatJob}...";
            }
            else return;
        }
        if (pendingActionAt != DateTime.MinValue && DateTime.UtcNow >= pendingActionAt)
        {
            pendingActionAt = DateTime.MinValue;
            // 首次进岛先用自由人探测宝箱，确认未满后才切换白魔并启动 BOCCHI。
            RequestFreelancerScan("首次进岛");
        }
        if (pendingScanAt != DateTime.MinValue && DateTime.UtcNow >= pendingScanAt)
        {
            pendingScanAt = DateTime.MinValue;
            BeginTreasureScan();
        }
        if (pendingReturnScanAt != DateTime.MinValue && DateTime.UtcNow >= pendingReturnScanAt)
        {
            pendingReturnScanAt = DateTime.MinValue;
            if (!waitingForScan) ScanTreasures();
        }
        if (pendingBocchiAt != DateTime.MinValue && DateTime.UtcNow >= pendingBocchiAt)
        {
            pendingBocchiAt = DateTime.MinValue;
            Send("/bocchiillegal on");
            bocchiEnabled = true;
            status = $"{combatJob}已切换，BOCCHI 已开启，正在刷 FT";
        }
        // 后续扫描由“亚返回”完成消息触发，不再依赖坐标轮询。
    }

    private void ScanTreasures()
    {
        if (pendingScanAt != DateTime.MinValue || waitingForScan) return;
        RequestFreelancerScan("亚返回后");
    }

    private void RequestFreelancerScan(string source)
    {
        if (pendingScanAt != DateTime.MinValue || waitingForScan) return;
        log.Information($"开始{source}宝箱检测：切换自由人，{JobChangeDelay.TotalSeconds:0.#}秒后使用原生通用动作 32");
        Send("/pdr pjob 自由人");
        pendingScanAt = DateTime.UtcNow + JobChangeDelay;
        status = $"{source}：正在切换自由人，准备释放魔寻宝...";
    }

    private void BeginTreasureScan()
    {
        waitingForScan = true;
        silver = copper = -1;
        nextAllowedScanAt = DateTime.UtcNow + SubsequentScanInterval;
        // 复刻 BOCCHI mobfarmer 的调用：魔寻宝是通用动作槽位 32。
        // 不发送聊天宏，避免宏命令与原生动作重复或被本地化解析吞掉。
        log.Information("自由人切换等待结束，首次和后续扫描均使用同一原生魔寻宝调用");
        treasureCastAttempts = 1;
        var cast = TryCastTreasureSight("首次调用");
        status = cast ? "已释放魔寻宝（通用动作 32），等待系统消息..." : "正在尝试释放魔寻宝，等待系统消息...";
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            _ = framework.RunOnFrameworkThread(() =>
            {
                if (running && waitingForScan)
                {
                    treasureCastAttempts++;
                    TryCastTreasureSight($"重试 #{treasureCastAttempts}");
                }
            });
        });
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            _ = framework.RunOnFrameworkThread(() =>
            {
                if (waitingForScan)
                {
                    waitingForScan = false;
                    if (silver < 0 || copper < 0)
                    {
                        ChangeToCombatJob();
                        if (initialScan)
                        {
                            initialScan = false;
                            pendingBocchiAt = DateTime.UtcNow + JobChangeDelay;
                            status = $"未收到首次系统宝箱消息，已恢复{combatJob}并等待开启 BOCCHI";
                        }
                        else
                        {
                            status = $"未收到系统宝箱消息，已恢复{combatJob}";
                        }
                    }
                }
            });
        });
    }

    private unsafe bool TryCastTreasureSight(string reason)
    {
        try
        {
            log.Information($"魔寻宝调用开始：{reason}，通用动作类型={ActionType.GeneralAction}，槽位={TreasureGeneralActionSlot}，目标参数={GeneralActionTarget}");
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
            {
                log.Error("魔寻宝调用失败：ActionManager.Instance() 返回空指针");
                return false;
            }

            var accepted = actionManager->UseAction(ActionType.GeneralAction, TreasureGeneralActionSlot,
                GeneralActionTarget, 0, ActionManager.UseActionMode.None, 0, null);
            log.Information($"魔寻宝调用完成：UseAction 返回 {accepted}");
            if (!accepted)
                log.Error("游戏拒绝了魔寻宝动作，请检查当前是否为自由人、辅助技能是否可用以及动作冷却状态");
            return accepted;
        }
        catch (InvalidOperationException ex)
        {
            log.Error(ex, $"魔寻宝调用失败（{reason}）：ActionManager 地址未解析，稍后重试");
            return false;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"魔寻宝调用失败（{reason}）：原生调用抛出异常");
            return false;
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;

        if (!running) return;

        // 该系统消息比 TerritoryType 更能说明副本已经完成加载。
        if (text.Contains("当前任务设有品级同步限制", StringComparison.Ordinal))
        {
            log.Information("检测到蜃景幻界新月岛 北征之章品级同步系统消息，开始准备首次宝箱检测");
            if (waitingForEntry)
            {
                waitingForEntry = false;
                pendingActionAt = DateTime.UtcNow + JobChangeDelay;
                status = "已确认进入副本，正在切换自由人...";
            }
        }

        // 后续检测只接受本角色完成亚返回的消息，忽略其他玩家的亚返回。
        var localPlayerName = objects.LocalPlayer?.Name.TextValue;
        var ownReturnCompleted = !string.IsNullOrWhiteSpace(localPlayerName) &&
            (text.Contains($"{localPlayerName}发动了“亚返回”", StringComparison.Ordinal) ||
             text.Contains($"{localPlayerName}发动了\"亚返回\"", StringComparison.Ordinal));
        if (ownReturnCompleted && treasurePhase == TreasurePhase.InnerReturn)
        {
            treasurePhase = TreasurePhase.SecondTeleport;
            treasurePhaseAt = DateTime.UtcNow + ReturnScanDelay;
            status = "内环寻宝完成，本角色亚返回已完成，5 秒后执行第二次传送...";
            log.Information("检测到寻宝内环的本角色亚返回完成消息");
            return;
        }
        if (ownReturnCompleted && treasurePhase == TreasurePhase.OuterReturn)
        {
            treasurePhase = TreasurePhase.LeaveDuty;
            treasurePhaseAt = DateTime.UtcNow + ReturnScanDelay;
            status = "外环寻宝完成，本角色亚返回已完成，5 秒后退出副本...";
            log.Information("检测到寻宝外环的本角色亚返回完成消息");
            return;
        }
        if (!initialScan && ownReturnCompleted)
        {
            log.Information($"检测到本角色 {localPlayerName} 的亚返回完成消息，将在 5 秒后检测宝箱");
            if (!waitingForScan && pendingReturnScanAt == DateTime.MinValue && DateTime.UtcNow >= nextAllowedScanAt)
            {
                pendingReturnScanAt = DateTime.UtcNow + ReturnScanDelay;
                status = "本角色亚返回已完成，等待 5 秒后检测宝箱...";
            }
            else if (DateTime.UtcNow < nextAllowedScanAt)
            {
                var remaining = nextAllowedScanAt - DateTime.UtcNow;
                log.Information($"忽略本次亚返回：距离下次宝箱检测还需 {remaining.TotalMinutes:0.0} 分钟");
            }
        }

        if (!waitingForScan || message.LogKind != XivChatType.SystemMessage) return;

        if (text.Contains("当前区域现在似乎没有宝箱", StringComparison.Ordinal))
        {
            silver = copper = 0;
            waitingForScan = false;
            log.Information("系统消息确认当前区域没有宝箱");
            CompleteTreasureScan();
            return;
        }

        var silverMatch = Regex.Match(text, @"(\d+)\s*个?\s*银宝箱");
        var copperMatch = Regex.Match(text, @"(\d+)\s*个?\s*铜宝箱");
        if (!silverMatch.Success || !copperMatch.Success) return;
        silver = int.Parse(silverMatch.Groups[1].Value);
        copper = int.Parse(copperMatch.Groups[1].Value);
        waitingForScan = false;
        CompleteTreasureScan();
    }

    private void CompleteTreasureScan()
    {
        if (silver >= MaxSilver || copper >= MaxCopper)
        {
            BeginTreasureProcedure();
            return;
        }
        ChangeToCombatJob();
        if (initialScan)
        {
            initialScan = false;
            pendingBocchiAt = DateTime.UtcNow + JobChangeDelay;
            status = $"首次检测：银箱 {silver}/{MaxSilver}，铜箱 {copper}/{MaxCopper}，等待白魔切换";
        }
        else
        {
            status = $"银箱 {silver}/{MaxSilver}，铜箱 {copper}/{MaxCopper}，继续刷 FT";
        }
    }

    private void BeginTreasureProcedure()
    {
        if (!TryReadTeleportTarget(out var error))
        {
            treasureError = error;
            status = error;
            treasurePhase = TreasurePhase.None;
            running = false;
            return;
        }

        treasureError = "";
        treasurePhase = TreasurePhase.FirstTeleport;
        treasurePhaseAt = DateTime.UtcNow + TreasureCommandDelay;
        status = $"宝箱达到上限（银 {silver}/{MaxSilver}，铜 {copper}/{MaxCopper}），准备传送寻宝...";
        log.Information("宝箱达到上限，0.5 秒后关闭 BOCCHI 非法模式并执行第一次传送");
    }

    private void UpdateTreasureProcedure()
    {
        if (treasurePhaseAt != DateTime.MinValue && DateTime.UtcNow < treasurePhaseAt) return;
        treasurePhaseAt = DateTime.MinValue;
        switch (treasurePhase)
        {
            case TreasurePhase.FirstTeleport:
                Send("/bocchiillegal off");
                bocchiEnabled = false;
                SendTeleport();
                treasurePhase = TreasurePhase.InnerCheck;
                treasurePhaseAt = DateTime.UtcNow + TeleportCheckDelay;
                status = "第一次传送已执行，5 秒后检查位置...";
                return;
            case TreasurePhase.SecondTeleport:
                SendTeleport();
                treasurePhase = TreasurePhase.OuterCheck;
                treasurePhaseAt = DateTime.UtcNow + TeleportCheckDelay;
                status = "第二次传送已执行，5 秒后检查位置...";
                return;
            case TreasurePhase.InnerMount:
                Send("/gaction 随机坐骑");
                treasurePhase = TreasurePhase.InnerStart;
                treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                status = "已到达内环位置，已召唤随机坐骑，3 秒后开始内环寻宝...";
                return;
            case TreasurePhase.InnerStart:
                Send("/pdr ptreasure 内环");
                treasurePhase = TreasurePhase.InnerReturn;
                status = "已开始内环寻宝，等待本角色亚返回...";
                return;
            case TreasurePhase.OuterMount:
                Send("/gaction 随机坐骑");
                treasurePhase = TreasurePhase.OuterStart;
                treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                status = "已到达外环位置，已召唤随机坐骑，3 秒后开始外环寻宝...";
                return;
            case TreasurePhase.OuterStart:
                Send("/pdr ptreasure 外环");
                treasurePhase = TreasurePhase.OuterReturn;
                status = "已开始外环寻宝，等待本角色亚返回...";
                return;
            case TreasurePhase.LeaveDuty:
                Send("/xsz-leaveduty");
                treasurePhase = TreasurePhase.Reentry;
                treasurePhaseAt = DateTime.UtcNow + LeaveDutyDelay;
                status = "正在退出副本，5 秒后重新开始循环...";
                break;
            case TreasurePhase.Reentry:
                treasurePhase = TreasurePhase.None;
                treasurePhaseAt = DateTime.MinValue;
                silver = copper = -1;
                initialScan = true;
                if (!IsIsland())
                {
                    Send("/pdrfe ocn");
                    waitingForEntry = true;
                    pendingActionAt = DateTime.UtcNow + IslandLoadDelay;
                    status = "寻宝完成，正在重新进入蜃景幻界新月岛 北征之章...";
                }
                else
                {
                    RequestFreelancerScan("新循环");
                }
                break;
        }

        if (treasurePhase is TreasurePhase.InnerCheck or TreasurePhase.OuterCheck)
        {
            if (!IsAtTeleportTarget())
            {
                treasureError = "TP 后未到达目标位置（允许误差 5 米），插件已停止；请检查 XSZToolbox 加载验证状态与 TP 功能开关";
                status = treasureError;
                treasurePhase = TreasurePhase.None;
                running = false;
                return;
            }

            if (treasurePhase == TreasurePhase.InnerCheck)
            {
                treasurePhase = TreasurePhase.InnerMount;
                treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                status = "已到达内环位置，1 秒后召唤随机坐骑...";
            }
            else
            {
                treasurePhase = TreasurePhase.OuterMount;
                treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                status = "已到达外环位置，1 秒后召唤随机坐骑...";
            }
        }
    }

    private void SendTeleport() => Send($"/xsz-tp {teleportTarget.X.ToString(CultureInfo.InvariantCulture)} {teleportTarget.Y.ToString(CultureInfo.InvariantCulture)} {teleportTarget.Z.ToString(CultureInfo.InvariantCulture)}");

    private bool TryReadTeleportTarget(out string error)
    {
        if (!float.TryParse(teleportX, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(teleportY, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(teleportZ, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            error = "传送坐标格式错误，请填写 x y z 三个数字";
            return false;
        }
        teleportTarget = new Vector3(x, y, z);
        error = "";
        return true;
    }

    private bool IsAtTeleportTarget()
    {
        var player = objects.LocalPlayer;
        return player != null && Vector3.Distance(player.Position, teleportTarget) <= 5f;
    }

    private bool IsIsland() => clientState.TerritoryType == IslandTerritory;

    private bool NearBase()
    {
        var player = objects.LocalPlayer;
        if (player == null) return false;
        var p = player.Position;
        var dx = p.X - BaseX;
        var dz = p.Z - BaseZ;
        return dx * dx + dz * dz <= BaseRadius * BaseRadius;
    }

    private void Send(string command)
    {
        try { commands.ProcessCommand(command); }
        catch (Exception ex) { log.Error(ex, $"执行命令失败：{command}"); }
    }

    private void ChangeToCombatJob()
    {
        Send($"/pdr pjob {combatJob}");
        log.Information($"切换战斗辅助职业：{combatJob}");
    }

    public void DrawStatus()
    {
        ImGui.Text($"状态：{status}");
        ImGui.Text($"当前区域 ID：{clientState.TerritoryType}（目标 1346）");
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("使用说明"))
        {
            ImGui.TextWrapped("1. 本插件均为高危行为，介意勿用。");
            ImGui.TextWrapped("2. 使用本插件的所必须条件：");
            ImGui.TextWrapped("   1）启用 BOCCHI 及其配套插件；");
            ImGui.TextWrapped("   2）启用 Daily Routines 插件，启用下列模块：");
            ImGui.TextWrapped("      ① 蜃景幻界新月岛 助手");
            ImGui.TextWrapped("      ② 更好的辅助职业列表");
            ImGui.TextWrapped("      ③ 辅助职业切换指令");
            ImGui.TextWrapped("   3）启用 XSZToolbox 插件并开启【坐标传送】功能。");
        }
        ImGui.Spacing();
        ImGui.Text("选择 BOCCHI 战斗中的辅助职业");
        if (ImGui.BeginCombo("##CombatJob", combatJob))
        {
            foreach (var job in CombatJobs)
            {
                var selected = job == combatJob;
                if (ImGui.Selectable(job, selected)) combatJob = job;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("注意：有些辅助职业的辅助技能可能导致与魔寻宝 CD 冲突，不接受因此导致问题的反馈。默认选择的辅助白魔法师应该无此问题。");
        ImGui.Spacing();
        ImGui.Text("设置宝箱达到上限后 DR 自动寻宝传送起始点");
        ImGui.SetNextItemWidth(72);
        ImGui.InputText("传送 X", ref teleportX, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72);
        ImGui.InputText("传送 Y", ref teleportY, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(72);
        ImGui.InputText("传送 Z", ref teleportZ, 32);
        if (silver >= 0 && copper >= 0) ImGui.Text($"宝箱：银 {silver}/{MaxSilver}，铜 {copper}/{MaxCopper}");
        if (!string.IsNullOrEmpty(treasureError)) ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"错误：{treasureError}");
        if (running)
        {
            if (ImGui.Button("停止脚本")) Stop();
        }
        else if (ImGui.Button("开始脚本")) Start();
        ImGui.SameLine();
        if (ImGui.Button("关闭窗口")) mainWindow.IsOpen = false;
    }

    private sealed class MainWindow : Dalamud.Interface.Windowing.Window
    {
        private readonly Plugin plugin;
        public MainWindow(Plugin plugin) : base($"OCNFarmer v{PluginVersion}##OCNFarmer") { this.plugin = plugin; IsOpen = false; }
        public override void Draw() => plugin.DrawStatus();
    }
}
