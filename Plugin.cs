using System.Numerics;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Configuration;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools;
using OmenTools.OmenService;

namespace NorthIslandChestPlugin;

public sealed class Plugin : IDalamudPlugin
{
    private static readonly string PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1.5.0.0";
    private static readonly string[] CombatJobs =
    {
        "辅助白魔法师", "辅助武士", "辅助猎人", "辅助武僧", "辅助狂战士",
        "辅助骑士", "辅助药剂师", "辅助炮击士", "辅助时魔法师", "辅助风水师",
        "辅助吟游诗人", "辅助舞者", "辅助剑斗士", "辅助魔法剑士", "辅助盗贼",
        "辅助预言师", "辅助召唤师", "辅助龙骑士", "辅助黑魔法师", "辅助忍者",
        "辅助亡灵法师", "辅助赤魔法师", "辅助青魔法师"
    };
    private const uint TreasureGeneralActionSlot = 32;
    private const ulong GeneralActionTarget = 3758096384UL;
    internal const uint IslandTerritory = 1346;
    private const int CurrencyCap = 9999;
    private const uint SilverCurrencyItemId = 51975;
    private const uint GoldCurrencyItemId = 51976;
    private const uint UltimateFixativeItemId = 51978;
    private const uint OldCofferItemId = 47740;
    private const int MaxSilver = 8;
    private const int MaxCopper = 30;
    private const float BaseX = 39f;
    private const float BaseZ = 39f;
    private const float BaseRadius = 18f;
    private static readonly TimeSpan SubsequentScanInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan JobChangeDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReturnScanDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CurrencyPurchaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CurrencyPurchaseRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CurrencyPurchaseRetryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TreasureCommandDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LeaveDutyDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProblemCheckInterval = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan TowerCrystalDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReturnWeatherCheckDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TreasureCrystalMoveStartDelay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan CrystalMoveTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MountRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MountRetryTimeout = TimeSpan.FromSeconds(12);
    private const uint MountRouletteGeneralActionSlot = 9;
    private static readonly Vector3 CrystalMoveTarget = new(882f, 258.5f, 882f);
    private const uint TowerWeatherId = 192;
    private static readonly Vector3 TowerCenter = new(-320f, 11.5f, 423f);
    private static readonly float TowerRadius = MathF.Sqrt(6.3f * 6.3f + 7.3f * 7.3f) - 0.3f;
    private static readonly Vector3 TowerStagingCenter = new(-390f, 68f, 692f);
    private const float TowerStagingRadius = 2f;
    private static readonly HttpClient NotificationHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private enum TreasurePhase { None, FirstMove, FirstMoveDelay, FirstCrystal, FirstWaitPlayers, InnerMount, InnerStart, InnerReturn, SecondMove, SecondMoveDelay, SecondCrystal, SecondWaitPlayers, OuterMount, OuterStart, OuterReturn, LeaveDuty, Reentry }
    private enum TowerPhase { None, MoveToCrystal, CrystalTeleport, MountToTower, MoveToStaging, StagingArrived, MoveToTower, Arrived, DismountBeforeResume }

    private readonly IChatGui chat;
    private readonly IClientState clientState;
    private readonly IObjectTable objects;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly PluginConfig config;
    private readonly CurrencyBuyer currencyBuyer;
    private readonly WindowSystem windows = new("北征宝箱");
    private readonly MainWindow mainWindow;
    private DateTime pendingScanAt = DateTime.MinValue;
    private DateTime pendingBocchiAt = DateTime.MinValue;
    private DateTime pendingReturnScanAt = DateTime.MinValue;
    private DateTime pendingCurrencyCheckAt = DateTime.MinValue;
    private DateTime pendingPurchaseAt = DateTime.MinValue;
    private DateTime purchaseRetryDeadline = DateTime.MinValue;
    private bool initialCurrencyCheckPending;
    private string initialCurrencyCheckSource = "首次进岛";
    private DateTime nextAllowedScanAt = DateTime.MinValue;
    private DateTime treasurePhaseAt = DateTime.MinValue;
    private TreasurePhase treasurePhase;
    private string combatJob = "辅助白魔法师";
    private string discardPreset = "";
    private DateTime playerWaitStartedAt = DateTime.MinValue;
    private DateTime nextPlayerCheckAt = DateTime.MinValue;
    private string currentCrystal = "";
    private bool innerLeg;
    private static readonly string[] Crystals = { "妖火", "城塞", "圣堂", "遗迹", "街道" };
    private string treasureError = "";
    private bool running;
    private bool bocchiEnabled;
    private bool waitingForEntry;
    private bool waitingForScan;
    private bool initialScan;
    private int treasureCastAttempts;
    private int silver = -1;
    private int copper = -1;
    private int silverCurrency = -1;
    private int goldCurrency = -1;
    private string currencyPurchaseStatus = "";
    private readonly Dictionary<string, int> treasureLoot = new(StringComparer.Ordinal);
    private string status = "未运行";
    private DateTime nextProblemCheckAt = DateTime.MinValue;
    private int lastProblemCheckSilver = -1;
    private bool problemCheckBaselineReady;
    private TowerPhase towerPhase;
    private DateTime towerPhaseAt = DateTime.MinValue;
    private Vector3 towerTarget;
    private Vector3 towerStagingTarget;
    private bool towerWeatherHandled;
    private bool towerWeatherNotificationSent;
    private bool towerStartPending;
    private DateTime towerStartAt = DateTime.MinValue;
    private bool weatherCheckPending;
    private DateTime nextWeatherCheckAt = DateTime.MinValue;
    private DateTime towerMoveDeadline = DateTime.MinValue;
    private DateTime nextTowerPositionCheckAt = DateTime.MinValue;
    private DateTime crystalMoveDeadline = DateTime.MinValue;
    private DateTime nextCrystalMoveCheckAt = DateTime.MinValue;
    private DateTime mountRetryDeadline = DateTime.MinValue;
    private DateTime nextMountRetryAt = DateTime.MinValue;
    private DateTime treasureMountDeadline = DateTime.MinValue;
    private DateTime nextTreasureMountAttemptAt = DateTime.MinValue;
    private DateTime towerResumeDeadline = DateTime.MinValue;

    public string Name => "OCNFarmer";

    public Plugin(IChatGui chat, IClientState clientState, IObjectTable objects, IFramework framework, ICommandManager commands, ICondition condition, IGameGui gameGui, IPluginLog log, IAddonLifecycle addonLifecycle, IDalamudPluginInterface pluginInterface)
    {
        this.chat = chat;
        this.clientState = clientState;
        this.objects = objects;
        this.commands = commands;
        this.framework = framework;
        this.condition = condition;
        this.gameGui = gameGui;
        this.log = log;
        DService.Init(pluginInterface, () => new DServiceInitOptions().EnableOnly(
            typeof(GamePacketManager)));
        config = pluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
        config.Initialize(pluginInterface);
        combatJob = CombatJobs.Contains(config.CombatJob, StringComparer.Ordinal) ? config.CombatJob : combatJob;
        discardPreset = config.DiscardPreset ?? "";
        NormalizePurchaseConfig();
        currencyBuyer = new CurrencyBuyer(clientState, objects, condition, gameGui, addonLifecycle, log, OnCurrencyPurchaseFinished);
        mainWindow = new MainWindow(this);
        windows.AddWindow(mainWindow);

        commands.AddHandler("/ocnchest", new CommandInfo((_, _) => mainWindow.IsOpen = true)
        {
            HelpMessage = "打开 OCNFarmer 设置。",
        });
        commands.AddHandler("/ocnstart", new CommandInfo((_, _) => Start())
        {
            HelpMessage = "启动 OCNFarmer 自动流程。",
        });
        commands.AddHandler("/ocnstop", new CommandInfo((_, _) => Stop("已通过命令停止"))
        {
            HelpMessage = "停止 OCNFarmer 自动流程。",
        });
        chat.ChatMessage += OnChatMessage;
        framework.Update += OnUpdate;
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += () => mainWindow.IsOpen = true;
    }

    public void Dispose()
    {
        currencyBuyer.Dispose();
        Stop("已停止");
        chat.ChatMessage -= OnChatMessage;
        framework.Update -= OnUpdate;
        commands.RemoveHandler("/ocnchest");
        commands.RemoveHandler("/ocnstart");
        commands.RemoveHandler("/ocnstop");
        windows.RemoveAllWindows();
        DService.Uninit();
    }

    public void Start()
    {
        if (running || currencyBuyer.IsBusy) return;
        running = true;
        silver = copper = -1;
        initialScan = true;
        towerWeatherHandled = false;
        towerWeatherNotificationSent = false;
        if (!IsIsland())
        {
            Send("/pdrfe ocn");
            waitingForEntry = true;
            status = "正在进入蜃景幻界新月岛 北征之章...";
            return;
        }
        weatherCheckPending = true;
        nextWeatherCheckAt = DateTime.UtcNow;
        if (TryHandleTowerWeather("岛内点击开始运行")) return;
        nextProblemCheckAt = DateTime.UtcNow + ProblemCheckInterval;
        problemCheckBaselineReady = false;
        ScheduleInitialCurrencyCheck("副本内首次", TimeSpan.Zero);
    }

    public void Stop(string message = "已停止")
    {
        if (currencyBuyer.IsBusy) currencyBuyer.Cancel();
        if (bocchiEnabled) Send("/bocchiillegal off");
        bocchiEnabled = false;
        running = waitingForEntry = waitingForScan = initialCurrencyCheckPending = false;
        pendingScanAt = pendingBocchiAt = pendingReturnScanAt = pendingCurrencyCheckAt = pendingPurchaseAt = nextAllowedScanAt = DateTime.MinValue;
        purchaseRetryDeadline = DateTime.MinValue;
        treasurePhase = TreasurePhase.None;
        treasurePhaseAt = DateTime.MinValue;
        towerPhase = TowerPhase.None;
        towerPhaseAt = DateTime.MinValue;
        towerWeatherHandled = false;
        towerWeatherNotificationSent = false;
        towerStartPending = false;
        towerStartAt = DateTime.MinValue;
        weatherCheckPending = false;
        nextWeatherCheckAt = DateTime.MinValue;
        towerMoveDeadline = nextTowerPositionCheckAt = DateTime.MinValue;
        crystalMoveDeadline = nextCrystalMoveCheckAt = DateTime.MinValue;
        mountRetryDeadline = nextMountRetryAt = DateTime.MinValue;
        treasureMountDeadline = nextTreasureMountAttemptAt = DateTime.MinValue;
        towerResumeDeadline = DateTime.MinValue;
        nextProblemCheckAt = DateTime.MinValue;
        lastProblemCheckSilver = -1;
        problemCheckBaselineReady = false;
        status = message;
    }

    private void OnUpdate(IFramework framework)
    {
        if (currencyBuyer.IsBusy)
        {
            currencyBuyer.Update();
            if (currencyBuyer.IsBusy) status = currencyBuyer.Status;
            return;
        }
        if (!running) return;

        if (towerStartPending)
        {
            if (DateTime.UtcNow < towerStartAt) return;
            towerStartPending = false;
            towerStartAt = DateTime.MinValue;
            BeginTowerNavigation();
            return;
        }

        if (towerPhase != TowerPhase.None)
        {
            UpdateTowerProcedure();
            return;
        }

        if (treasurePhase != TreasurePhase.None)
        {
            UpdateTreasureProcedure();
            return;
        }
        CheckSilverCurrencyHealth();
        if (!IsIsland())
        {
            if (bocchiEnabled) Send("/bocchiillegal off");
            bocchiEnabled = false;
            status = "当前未在蜃景幻界新月岛 北征之章中，正在自动进入...";
            return;
        }
        // 从副本外进入时只接受品级同步聊天消息，不使用时间兜底判断。
        if (waitingForEntry) return;
        if (weatherCheckPending)
        {
            if (DateTime.UtcNow >= nextWeatherCheckAt)
            {
                if (TryHandleTowerWeather("进入副本后")) return;
                nextWeatherCheckAt = DateTime.UtcNow.AddSeconds(1);
            }
        }
        if (pendingScanAt != DateTime.MinValue && DateTime.UtcNow >= pendingScanAt)
        {
            pendingScanAt = DateTime.MinValue;
            BeginTreasureScan();
        }
        if (pendingCurrencyCheckAt != DateTime.MinValue && DateTime.UtcNow >= pendingCurrencyCheckAt)
        {
            pendingCurrencyCheckAt = DateTime.MinValue;
            UpdateCurrencyCounts();
            if (HasCurrencyPurchaseRequest())
            {
                pendingPurchaseAt = DateTime.UtcNow + CurrencyPurchaseDelay;
                purchaseRetryDeadline = DateTime.UtcNow + CurrencyPurchaseRetryTimeout;
                status = "钱币达到购买条件，正在准备自动购买...";
                return;
            }
            ContinueAfterInitialCurrencyCheck();
        }
        if (pendingPurchaseAt != DateTime.MinValue && DateTime.UtcNow >= pendingPurchaseAt)
        {
            pendingPurchaseAt = DateTime.MinValue;
            if (TryBeginCurrencyPurchase()) return;

            if (HasCurrencyPurchaseRequest() && DateTime.UtcNow < purchaseRetryDeadline)
            {
                pendingPurchaseAt = DateTime.UtcNow + CurrencyPurchaseRetryInterval;
                status = "钱币达到购买条件，正在准备自动购买...";
                log.Debug($"自动购买暂未就绪，{CurrencyPurchaseRetryInterval.TotalSeconds:0} 秒后重试：{currencyBuyer.Status}");
                return;
            }

            log.Warning($"自动购买未能在 {CurrencyPurchaseRetryTimeout.TotalSeconds:0} 秒内开始，本次继续原流程：{currencyBuyer.Status}");
            purchaseRetryDeadline = DateTime.MinValue;
            ContinueAfterInitialCurrencyCheck();
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
            if (!string.IsNullOrWhiteSpace(discardPreset))
                Send($"/pdrdiscard {discardPreset.Trim()}");
            bocchiEnabled = true;
            status = "宝箱未达到上限，正在进行战斗流程";
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
        status = cast ? "已释放魔寻宝，等待系统" : "正在尝试释放魔寻宝，等待系统消息...";
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

    private bool IsMountedOrMounting()
    {
        try
        {
            return IsMounted() || IsMounting();
        }
        catch (Exception ex)
        {
            log.Error(ex, "读取骑乘状态失败");
            return false;
        }
    }

    private bool IsMounted()
    {
        return condition[ConditionFlag.Mounted];
    }

    private bool IsMounting()
    {
        return condition[ConditionFlag.Mounting] || condition[ConditionFlag.Mounting71];
    }

    private unsafe bool TryUseRandomMount(string reason)
    {
        try
        {
            log.Information($"随机坐骑原生调用开始（{reason}）：通用动作类型={ActionType.GeneralAction}，槽位={MountRouletteGeneralActionSlot}");
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
            {
                log.Error("随机坐骑原生调用失败：ActionManager.Instance() 返回空指针");
                return false;
            }

            // 与 BOCCHI 的 MountRoulette 实现一致：随机坐骑是通用动作 9，使用默认参数。
            var accepted = actionManager->UseAction(ActionType.GeneralAction, MountRouletteGeneralActionSlot);
            log.Information($"随机坐骑原生调用完成（{reason}）：UseAction 返回 {accepted}");
            if (!accepted)
                log.Warning("游戏拒绝了随机坐骑动作，请检查当前位置、战斗状态和坐骑权限");
            return accepted;
        }
        catch (InvalidOperationException ex)
        {
            log.Error(ex, $"随机坐骑原生调用失败（{reason}）：ActionManager 地址未解析");
            return false;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"随机坐骑原生调用失败（{reason}）：原生调用抛出异常");
            return false;
        }
    }

    private unsafe bool TryDismount(string reason)
    {
        if (!IsMounted()) return true;

        try
        {
            log.Information($"下坐骑原生调用开始（{reason}）");
            var actionManager = ActionManager.Instance();
            if (actionManager == null)
            {
                log.Error("下坐骑原生调用失败：ActionManager.Instance() 返回空指针");
                return false;
            }

            var accepted = actionManager->UseAction(ActionType.Mount, 0);
            log.Information($"下坐骑原生调用完成（{reason}）：UseAction 返回 {accepted}");
            return accepted;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"下坐骑原生调用失败（{reason}）");
            return false;
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;

        if (!running) return;

        CaptureTreasureLoot(text);

        // 该系统消息比 TerritoryType 更能说明副本已经完成加载。
        if (text.Contains("当前任务设有品级同步限制", StringComparison.Ordinal))
        {
            log.Information("检测到蜃景幻界新月岛 北征之章品级同步系统消息，开始首次钱币检测");
            if (waitingForEntry)
            {
                waitingForEntry = false;
                weatherCheckPending = true;
                nextWeatherCheckAt = DateTime.UtcNow;
                nextProblemCheckAt = DateTime.UtcNow + ProblemCheckInterval;
                problemCheckBaselineReady = false;
                if (!TryHandleTowerWeather("进入副本品级同步消息"))
                    ScheduleInitialCurrencyCheck("首次进岛", JobChangeDelay);
            }
        }

        // 后续检测只接受本角色完成亚返回的消息，忽略其他玩家的亚返回。
        var localPlayerName = objects.LocalPlayer?.Name.TextValue;
        var ownReturnCompleted = !string.IsNullOrWhiteSpace(localPlayerName) &&
            (text.Contains($"{localPlayerName}发动了“亚返回”", StringComparison.Ordinal) ||
             text.Contains($"{localPlayerName}发动了\"亚返回\"", StringComparison.Ordinal));
        if (ownReturnCompleted && treasurePhase == TreasurePhase.InnerReturn)
        {
            treasurePhase = TreasurePhase.SecondMove;
            treasurePhaseAt = DateTime.UtcNow + ReturnScanDelay;
            status = "内环寻宝完成，正在执行外环寻宝流程";
            log.Information("检测到寻宝内环的本角色亚返回完成消息");
            return;
        }
        if (ownReturnCompleted && treasurePhase == TreasurePhase.OuterReturn)
        {
            if (config.NotifyTreasureComplete)
                SendServerChanNotificationAsync("OCNFarmer已完成一次寻宝，请查阅战利品清单。", BuildTreasureLootMessage(), "寻宝完成");
            treasurePhase = TreasurePhase.LeaveDuty;
            treasurePhaseAt = DateTime.UtcNow + ReturnScanDelay;
            status = "外环寻宝完成，即将自动重进副本";
            log.Information("检测到寻宝外环的本角色亚返回完成消息");
            return;
        }
        if (!initialScan && ownReturnCompleted)
        {
            weatherCheckPending = true;
            nextWeatherCheckAt = DateTime.UtcNow;
            if (TryHandleTowerWeather("普通亚返回完成后")) return;
            log.Information($"检测到本角色 {localPlayerName} 的亚返回完成消息，已立即检测天气，并按间隔检测钱币和宝箱");
            if (pendingCurrencyCheckAt == DateTime.MinValue)
                pendingCurrencyCheckAt = DateTime.UtcNow + ReturnScanDelay;
            var nearCopperCap = copper is 28 or 29;
            if (!waitingForScan && pendingReturnScanAt == DateTime.MinValue &&
                (nearCopperCap || DateTime.UtcNow >= nextAllowedScanAt))
            {
                pendingReturnScanAt = DateTime.UtcNow + ReturnScanDelay;
                if (nearCopperCap)
                    log.Information($"当前铜宝箱为 {copper}/30，绕过 10 分钟间隔，将在本次亚返回后复检宝箱");
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

    private void CaptureTreasureLoot(string text)
    {
        if (treasurePhase is not (TreasurePhase.InnerStart or TreasurePhase.InnerReturn or TreasurePhase.OuterStart or TreasurePhase.OuterReturn) ||
            !text.Contains("获得了", StringComparison.Ordinal))
            return;

        var index = text.IndexOf("获得了", StringComparison.Ordinal) + "获得了".Length;
        var reward = text[index..].Trim();
        reward = reward.TrimEnd('。', '！', '!', '.', ' ');
        if (reward.Length == 0) return;

        var quantity = 1;
        var match = Regex.Match(reward, "^(\\d+)\\s*(?:枚|个|件|块|颗|瓶|张|本|只|组)?\\s*(.+)$");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsedQuantity))
        {
            quantity = Math.Max(1, parsedQuantity);
            reward = match.Groups[2].Value.Trim();
        }

        reward = reward.Trim().Trim('“', '”', '"', '\'');
        while (reward.Length > 0 && (char.GetUnicodeCategory(reward[0]) == UnicodeCategory.PrivateUse || char.IsControl(reward[0])))
            reward = reward[1..].TrimStart();
        if (reward.Length == 0) return;
        treasureLoot[reward] = treasureLoot.TryGetValue(reward, out var existing) ? existing + quantity : quantity;
        log.Information($"记录寻宝战利品：{reward} ×{quantity}");
    }

    private string BuildTreasureLootMessage()
    {
        var builder = new StringBuilder(DateTime.Now.ToString("yyyy年M月d日HH:mm", CultureInfo.InvariantCulture));
        builder.Append(" 完成了一次寻宝  \n本次寻宝的战利品清单：");
        foreach (var item in treasureLoot.OrderBy(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
            builder.Append("  \n").Append(item.Key).Append('×').Append(item.Value);
        if (treasureLoot.Count == 0)
            builder.Append("  \n未检测到获得物品消息");
        return builder.ToString().TrimEnd();
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
            status = $"首次检测：银箱 {silver}/{MaxSilver}，铜箱 {copper}/{MaxCopper}，等待辅助职业切换";
        }
        else
        {
            status = $"银箱 {silver}/{MaxSilver}，铜箱 {copper}/{MaxCopper}，进行战斗流程";
        }
    }

    private void BeginTreasureProcedure()
    {
        nextProblemCheckAt = DateTime.MinValue;
        treasureLoot.Clear();
        treasureError = "";
        treasurePhase = TreasurePhase.FirstMove;
        treasurePhaseAt = DateTime.UtcNow + TreasureCommandDelay;
        status = $"宝箱达到上限（银 {silver}/{MaxSilver}，铜 {copper}/{MaxCopper}），准备移动至小水晶...";
        log.Information("宝箱达到上限，0.5 秒后关闭 BOCCHI 并移动至小水晶区域");
    }

    private void UpdateTreasureProcedure()
    {
        if (treasurePhaseAt != DateTime.MinValue && DateTime.UtcNow < treasurePhaseAt) return;
        treasurePhaseAt = DateTime.MinValue;
        switch (treasurePhase)
        {
            case TreasurePhase.FirstMove:
                Send("/bocchiillegal off");
                bocchiEnabled = false;
                treasurePhase = TreasurePhase.FirstMoveDelay;
                treasurePhaseAt = DateTime.UtcNow + TreasureCrystalMoveStartDelay;
                status = "宝箱达到上限，准备移动至小水晶...";
                return;
            case TreasurePhase.FirstMoveDelay:
                StartCrystalMove(TreasurePhase.FirstCrystal);
                return;
            case TreasurePhase.FirstCrystal:
                UpdateCrystalMove(true);
                return;
            case TreasurePhase.SecondMove:
                treasurePhase = TreasurePhase.SecondMoveDelay;
                treasurePhaseAt = DateTime.UtcNow + TreasureCrystalMoveStartDelay;
                status = "内环寻宝完成，准备移动至小水晶...";
                return;
            case TreasurePhase.SecondMoveDelay:
                StartCrystalMove(TreasurePhase.SecondCrystal);
                return;
            case TreasurePhase.SecondCrystal:
                UpdateCrystalMove(false);
                return;
            case TreasurePhase.FirstWaitPlayers:
            case TreasurePhase.SecondWaitPlayers:
                CheckCrystalPlayers();
                return;
            case TreasurePhase.InnerMount:
                UpdateTreasureMount(true);
                return;
            case TreasurePhase.InnerStart:
                Send("/pdr ptreasure 内环");
                treasurePhase = TreasurePhase.InnerReturn;
                status = "已开始内环寻宝...";
                return;
            case TreasurePhase.OuterMount:
                UpdateTreasureMount(false);
                return;
            case TreasurePhase.OuterStart:
                Send("/pdr ptreasure 外环");
                treasurePhase = TreasurePhase.OuterReturn;
                status = "已开始外环寻宝...";
                return;
            case TreasurePhase.LeaveDuty:
                Send("/pdr leaveduty");
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
                    status = "寻宝完成，正在重新进入蜃景幻界新月岛 北征之章...";
                }
                else
                {
                    nextProblemCheckAt = DateTime.UtcNow + ProblemCheckInterval;
                    problemCheckBaselineReady = false;
                    RequestFreelancerScan("新循环");
                }
                break;
        }

    }

    private void StartCrystalMove(TreasurePhase arrivalPhase)
    {
        Send($"/vnav moveto {CrystalMoveTarget.X.ToString("0.###", CultureInfo.InvariantCulture)} {CrystalMoveTarget.Y.ToString("0.###", CultureInfo.InvariantCulture)} {CrystalMoveTarget.Z.ToString("0.###", CultureInfo.InvariantCulture)}");
        treasurePhase = arrivalPhase;
        crystalMoveDeadline = DateTime.UtcNow + CrystalMoveTimeout;
        nextCrystalMoveCheckAt = DateTime.UtcNow;
        status = "正在移动至小水晶区域，检测到达位置...";
        log.Information($"已执行前往小水晶区域导航，目标坐标 {CrystalMoveTarget}，开始严格坐标轮询");
    }

    private void UpdateCrystalMove(bool firstLeg)
    {
        if (DateTime.UtcNow < nextCrystalMoveCheckAt) return;
        nextCrystalMoveCheckAt = DateTime.UtcNow.AddSeconds(1);
        var atTarget = IsAtCrystalMoveTarget();
        log.Debug($"小水晶区域坐标检测：当前位置 {objects.LocalPlayer?.Position.ToString() ?? "未知"}，目标 {CrystalMoveTarget}，到达={atTarget}");
        if (!atTarget)
        {
            if (DateTime.UtcNow < crystalMoveDeadline) return;
            Stop("未到达小水晶区域，请检查导航功能");
            return;
        }

        crystalMoveDeadline = nextCrystalMoveCheckAt = DateTime.MinValue;
        BeginCrystalWait(firstLeg);
    }

    private bool IsAtCrystalMoveTarget()
    {
        var player = objects.LocalPlayer;
        if (player == null) return false;

        // 导航后的坐标存在浮点误差，使用 0.5 米球形容差；相比寻宝/魔之塔区域判定仍是严格的定点检测。
        return Vector3.DistanceSquared(player.Position, CrystalMoveTarget) <= 0.25f;
    }

    private void BeginCrystalWait(bool firstLeg)
    {
        innerLeg = firstLeg;
        currentCrystal = Crystals[Random.Shared.Next(Crystals.Length)];
        Send($"/pdr ptp {currentCrystal}");
        treasurePhase = firstLeg ? TreasurePhase.FirstWaitPlayers : TreasurePhase.SecondWaitPlayers;
        treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        playerWaitStartedAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        nextPlayerCheckAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        status = $"已传送至小水晶：{currentCrystal}，等待 2 秒后检测周围玩家...";
    }

    private void CheckCrystalPlayers()
    {
        if (DateTime.UtcNow < nextPlayerCheckAt) return;
        nextPlayerCheckAt = DateTime.UtcNow.AddSeconds(1);
        if (HasNearbyPlayer(50f))
        {
            status = "等待周围玩家中...";
            if (DateTime.UtcNow - playerWaitStartedAt > TimeSpan.FromSeconds(15))
            {
                var alternatives = Crystals.Where(x => x != currentCrystal).ToArray();
                currentCrystal = alternatives[Random.Shared.Next(alternatives.Length)];
                Send($"/pdr ptp {currentCrystal}");
                playerWaitStartedAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                nextPlayerCheckAt = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                status = $"周围玩家等待超过 15 秒，已换传小水晶：{currentCrystal}...";
            }
            return;
        }

        if (innerLeg)
        {
            treasurePhase = TreasurePhase.InnerMount;
            treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            treasureMountDeadline = DateTime.UtcNow + MountRetryTimeout;
            nextTreasureMountAttemptAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            status = "周围 50 米内无玩家，1 秒后召唤随机坐骑...";
        }
        else
        {
            treasurePhase = TreasurePhase.OuterMount;
            treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            treasureMountDeadline = DateTime.UtcNow + MountRetryTimeout;
            nextTreasureMountAttemptAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            status = "周围 50 米内无玩家，1 秒后召唤随机坐骑...";
        }
    }

    private void UpdateTreasureMount(bool firstLeg)
    {
        if (IsMounted())
        {
            treasureMountDeadline = nextTreasureMountAttemptAt = DateTime.MinValue;
            Send(firstLeg ? "/pdr ptreasure 内环" : "/pdr ptreasure 外环");
            treasurePhase = firstLeg ? TreasurePhase.InnerReturn : TreasurePhase.OuterReturn;
            status = firstLeg ? "已开始内环寻宝..." : "已开始外环寻宝...";
            return;
        }

        if (DateTime.UtcNow >= treasureMountDeadline)
        {
            Stop("未能召唤随机坐骑，请检查坐骑可用性");
            return;
        }

        if (DateTime.UtcNow < nextTreasureMountAttemptAt) return;
        nextTreasureMountAttemptAt = DateTime.UtcNow + MountRetryInterval;
        if (!IsMounting())
            TryUseRandomMount(firstLeg ? "内环" : "外环");
    }

    private bool HasNearbyPlayer(float radius)
    {
        var local = objects.LocalPlayer;
        if (local == null) return true;
        foreach (var obj in objects)
        {
            if (obj.ObjectKind == ObjectKind.Pc && obj.Address != local.Address && Vector3.Distance(obj.Position, local.Position) <= radius)
                return true;
        }
        return false;
    }

    private bool IsIsland() => clientState.TerritoryType == IslandTerritory;

    private unsafe uint GetCurrentWeatherId()
    {
        try
        {
            var weatherManager = WeatherManager.Instance();
            return weatherManager == null ? 0u : weatherManager->GetCurrentWeather();
        }
        catch (Exception ex)
        {
            log.Error(ex, "读取当前天气失败");
            return 0u;
        }
    }

    private bool TryHandleTowerWeather(string reason)
    {
        if (!IsIsland() || towerPhase != TowerPhase.None) return towerPhase != TowerPhase.None;
        var weatherId = GetCurrentWeatherId();
        log.Information($"天气检测（{reason}）：当前天气 ID={weatherId}，目标 ID={TowerWeatherId}");
        if (weatherId == 0)
            return false;
        weatherCheckPending = false;
        nextWeatherCheckAt = DateTime.MinValue;
        if (weatherId != TowerWeatherId)
        {
            towerWeatherHandled = false;
            towerWeatherNotificationSent = false;
            return false;
        }
        if (!towerWeatherNotificationSent)
        {
            towerWeatherNotificationSent = true;
            var now = DateTime.Now.ToString("yyyy年M月d日HH:mm", CultureInfo.InvariantCulture);
            if (config.NotifyTowerWeather)
                SendServerChanNotificationAsync("OCNFarmer检测到蜃景天气出现", $"出现时间：{now}", "蜃景天气通知");
        }
        // 未启用自动前往时，天气检测只用于通知，不能锁存为“已处理”；
        // 这样用户随后开启功能并点击开始时，当前 192 天气仍会立即接管流程。
        if (!config.AutoGoTower)
        {
            towerWeatherHandled = false;
            return false;
        }
        if (towerWeatherHandled) return towerPhase != TowerPhase.None;
        towerWeatherHandled = true;
        // 天气接管必须优先于钱币、魔寻宝及普通寻宝状态，清除所有已排队的普通动作。
        initialCurrencyCheckPending = false;
        pendingCurrencyCheckAt = pendingPurchaseAt = pendingScanAt = pendingReturnScanAt = pendingBocchiAt = DateTime.MinValue;
        purchaseRetryDeadline = DateTime.MinValue;
        waitingForScan = false;
        treasurePhase = TreasurePhase.None;
        treasurePhaseAt = DateTime.MinValue;
        if (bocchiEnabled) Send("/bocchiillegal off");
        bocchiEnabled = false;
        towerStartPending = true;
        towerStartAt = DateTime.UtcNow + ReturnWeatherCheckDelay;
        status = "检测到蜃景天气，正在前往大水晶";
        log.Information($"检测到天气 {TowerWeatherId}，将在 6 秒后开始前往大水晶，随后进入中转点 {TowerStagingCenter}");
        return true;
    }

    private void BeginTowerNavigation()
    {
        towerPhase = TowerPhase.MoveToCrystal;
        towerPhaseAt = DateTime.UtcNow;
        status = "检测到蜃景天气，正在前往大水晶";
        log.Information($"天气延迟等待结束，开始前往魔之塔流程：第一阶段前往大水晶，随后进入中转点 {TowerStagingCenter}");
    }

    private static Vector3 GetRandomTowerTarget()
    {
        var angle = Random.Shared.NextSingle() * MathF.PI * 2f;
        var radius = MathF.Sqrt(Random.Shared.NextSingle()) * TowerRadius;
        return new Vector3(TowerCenter.X + MathF.Cos(angle) * radius, TowerCenter.Y, TowerCenter.Z + MathF.Sin(angle) * radius);
    }

    private static Vector3 GetRandomTowerStagingTarget()
    {
        var angle = Random.Shared.NextSingle() * MathF.PI * 2f;
        var radius = MathF.Sqrt(Random.Shared.NextSingle()) * TowerStagingRadius;
        return new Vector3(TowerStagingCenter.X + MathF.Cos(angle) * radius, TowerStagingCenter.Y, TowerStagingCenter.Z + MathF.Sin(angle) * radius);
    }

    private void UpdateTowerProcedure()
    {
        if (towerPhaseAt != DateTime.MinValue && DateTime.UtcNow < towerPhaseAt) return;
        towerPhaseAt = DateTime.MinValue;
        switch (towerPhase)
        {
            case TowerPhase.MoveToCrystal:
                Send($"/vnav moveto {CrystalMoveTarget.X.ToString("0.###", CultureInfo.InvariantCulture)} {CrystalMoveTarget.Y.ToString("0.###", CultureInfo.InvariantCulture)} {CrystalMoveTarget.Z.ToString("0.###", CultureInfo.InvariantCulture)}");
                towerPhase = TowerPhase.CrystalTeleport;
                crystalMoveDeadline = DateTime.UtcNow + CrystalMoveTimeout;
                nextCrystalMoveCheckAt = DateTime.UtcNow;
                status = "检测到蜃景天气，正在前往大水晶";
                return;
            case TowerPhase.CrystalTeleport:
                if (DateTime.UtcNow < nextCrystalMoveCheckAt) return;
                nextCrystalMoveCheckAt = DateTime.UtcNow.AddSeconds(1);
                if (!IsAtCrystalMoveTarget())
                {
                    if (DateTime.UtcNow < crystalMoveDeadline) return;
                    Stop("未到达大水晶区域，请检查导航功能");
                    return;
                }
                crystalMoveDeadline = nextCrystalMoveCheckAt = DateTime.MinValue;
                Send("/pdr ptp 遗迹");
                towerPhase = TowerPhase.MountToTower;
                towerPhaseAt = DateTime.UtcNow + TowerCrystalDelay;
                mountRetryDeadline = DateTime.UtcNow + TowerCrystalDelay + MountRetryTimeout;
                nextMountRetryAt = DateTime.UtcNow + TowerCrystalDelay;
                status = "已到达小水晶，正在前往魔之塔入口";
                return;
            case TowerPhase.MountToTower:
                if (DateTime.UtcNow >= mountRetryDeadline)
                {
                    Stop("未能召唤随机坐骑，请检查坐骑可用性");
                    return;
                }

                var mountStarted = IsMountedOrMounting();
                if (!mountStarted && DateTime.UtcNow >= nextMountRetryAt)
                {
                    nextMountRetryAt = DateTime.UtcNow + MountRetryInterval;
                    log.Information("遗迹小水晶传送等待结束，尝试使用原生随机坐骑动作");
                    mountStarted = TryUseRandomMount("魔之塔");
                }
                if (!mountStarted) return;

                // 骑乘动作已接受或正在进行时即可移动，无需等待 Mounted 状态。
                mountRetryDeadline = nextMountRetryAt = DateTime.MinValue;
                towerStagingTarget = GetRandomTowerStagingTarget();
                towerPhase = TowerPhase.MoveToStaging;
                towerPhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                status = "已到达小水晶，正在前往魔之塔入口";
                return;
            case TowerPhase.MoveToStaging:
                Send($"/vnav moveto {towerStagingTarget.X.ToString("0.###", CultureInfo.InvariantCulture)} {towerStagingTarget.Y.ToString("0.###", CultureInfo.InvariantCulture)} {towerStagingTarget.Z.ToString("0.###", CultureInfo.InvariantCulture)}");
                towerPhase = TowerPhase.StagingArrived;
                towerMoveDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                nextTowerPositionCheckAt = DateTime.UtcNow;
                status = "已到达小水晶，正在前往魔之塔入口";
                log.Information($"已执行魔之塔中转点导航，目标坐标 {towerStagingTarget}");
                return;
            case TowerPhase.StagingArrived:
                if (DateTime.UtcNow < nextTowerPositionCheckAt) return;
                nextTowerPositionCheckAt = DateTime.UtcNow.AddSeconds(1);
                if (!IsNearPosition(towerStagingTarget, TowerStagingRadius))
                {
                    if (DateTime.UtcNow < towerMoveDeadline) return;
                    Stop("未到达魔之塔中转区域，请检查导航功能");
                    return;
                }
                towerMoveDeadline = nextTowerPositionCheckAt = DateTime.MinValue;
                towerTarget = GetRandomTowerTarget();
                towerPhase = TowerPhase.MoveToTower;
                towerPhaseAt = DateTime.UtcNow;
                status = "已到达小水晶，正在前往魔之塔入口";
                log.Information($"已到达魔之塔中转区域，开始第二阶段导航，最终入口目标 {towerTarget}");
                return;
            case TowerPhase.MoveToTower:
                Send($"/vnav moveto {towerTarget.X.ToString("0.###", CultureInfo.InvariantCulture)} {towerTarget.Y.ToString("0.###", CultureInfo.InvariantCulture)} {towerTarget.Z.ToString("0.###", CultureInfo.InvariantCulture)}");
                towerPhase = TowerPhase.Arrived;
                towerMoveDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                nextTowerPositionCheckAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                status = "已到达小水晶，正在前往魔之塔入口";
                log.Information($"开始最终魔之塔入口导航，目标坐标 {towerTarget}");
                return;
            case TowerPhase.Arrived:
                if (DateTime.UtcNow < nextTowerPositionCheckAt) return;
                nextTowerPositionCheckAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                if (!IsNearTowerTarget())
                {
                    if (DateTime.UtcNow < towerMoveDeadline) return;
                    Stop("未到达魔之塔进入区域，请检查导航功能");
                    return;
                }
                towerPhase = TowerPhase.None;
                towerPhaseAt = DateTime.MinValue;
                towerMoveDeadline = nextTowerPositionCheckAt = DateTime.MinValue;
                var arrivalWeatherId = GetCurrentWeatherId();
                log.Information($"到达魔之塔区域后二次天气检测：当前天气 ID={arrivalWeatherId}，目标 ID={TowerWeatherId}");
                if (arrivalWeatherId != TowerWeatherId)
                {
                    TryDismount("魔之塔天气消失后恢复插件功能");
                    towerWeatherHandled = false;
                    weatherCheckPending = false;
                    initialScan = true;
                    silver = copper = -1;
                    nextProblemCheckAt = DateTime.UtcNow + ProblemCheckInterval;
                    problemCheckBaselineReady = false;
                    towerPhase = TowerPhase.DismountBeforeResume;
                    towerResumeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                    towerPhaseAt = DateTime.UtcNow;
                    status = "由于到达魔之塔时天气条件已消失，已恢复插件功能";
                    return;
                }
                if (config.NotifyTowerArrival)
                {
                    var now = DateTime.Now.ToString("yyyy年M月d日HH:mm", CultureInfo.InvariantCulture);
                    SendServerChanNotificationAsync("OCNFarmer已到达魔之塔进入区域", $"{now}，插件功能已停止，请注意手动接管。如果你已经设置好了其他插件介入，请忽略。", "魔之塔到达通知");
                }
                Stop("已到达魔之塔入口，插件功能停止，等待接管");
                return;
            case TowerPhase.DismountBeforeResume:
                if (IsMounted())
                {
                    TryDismount("魔之塔天气消失后恢复插件功能");
                    if (DateTime.UtcNow >= towerResumeDeadline)
                    {
                        Stop("未能下坐骑，无法恢复魔寻宝流程");
                        return;
                    }
                    towerPhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return;
                }
                if (IsMounting())
                {
                    if (DateTime.UtcNow >= towerResumeDeadline)
                    {
                        Stop("坐骑动作未完成，无法恢复魔寻宝流程");
                        return;
                    }
                    towerPhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return;
                }
                towerPhase = TowerPhase.None;
                towerPhaseAt = towerResumeDeadline = DateTime.MinValue;
                ScheduleInitialCurrencyCheck("魔之塔天气结束后", JobChangeDelay);
                status = "由于到达魔之塔时天气条件已消失，已恢复插件功能";
                return;
        }
    }

    private bool IsNearTowerTarget()
    {
        var player = objects.LocalPlayer;
        if (player == null) return false;
        var dx = player.Position.X - TowerCenter.X;
        var dz = player.Position.Z - TowerCenter.Z;
        return dx * dx + dz * dz <= TowerRadius * TowerRadius;
    }

    private bool IsNearPosition(Vector3 target, float radius)
    {
        var player = objects.LocalPlayer;
        if (player == null) return false;
        var dx = player.Position.X - target.X;
        var dz = player.Position.Z - target.Z;
        return dx * dx + dz * dz <= radius * radius;
    }

    private void BeginTowerProcedureForTest()
    {
        if (bocchiEnabled) Send("/bocchiillegal off");
        bocchiEnabled = false;
        towerWeatherHandled = true;
        towerPhase = TowerPhase.MoveToCrystal;
        towerPhaseAt = DateTime.UtcNow;
        status = "检测到蜃景天气，正在前往大水晶";
    }

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

    private unsafe void UpdateCurrencyCounts()
    {
        silverCurrency = GetInventoryCount(SilverCurrencyItemId);
        goldCurrency = GetInventoryCount(GoldCurrencyItemId);
        if (!problemCheckBaselineReady)
        {
            lastProblemCheckSilver = silverCurrency;
            problemCheckBaselineReady = true;
        }
        log.Information($"钱币检测：白银币 {silverCurrency}/{CurrencyCap}，白金币 {goldCurrency}/{CurrencyCap}");
    }

    private void CheckSilverCurrencyHealth()
    {
        if (!config.NotifyProblem || !running || !IsIsland() || treasurePhase != TreasurePhase.None ||
            currencyBuyer.IsBusy || nextProblemCheckAt == DateTime.MinValue || DateTime.UtcNow < nextProblemCheckAt)
            return;

        nextProblemCheckAt = DateTime.UtcNow + ProblemCheckInterval;
        var hadBaseline = problemCheckBaselineReady;
        UpdateCurrencyCounts();
        if (!hadBaseline) return;

        if (silverCurrency == lastProblemCheckSilver)
            SendServerChanNotificationAsync("OCNFarmer可能遇到问题", "长时间未检测到战斗行为，请注意接管。", "白银币停滞检测");
        lastProblemCheckSilver = silverCurrency;
    }

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

    private void SendServerChanNotificationAsync(string title, string desp, string reason)
    {
        if (!config.ServerChanEnabled || string.IsNullOrWhiteSpace(config.ServerChanApiUrl)) return;
        var endpoint = config.ServerChanApiUrl.Trim();
        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["title"] = title,
                    ["desp"] = desp,
                });
                using var response = await NotificationHttpClient.PostAsync(endpoint, content).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    log.Warning($"Server酱通知发送失败（{reason}）：HTTP {(int)response.StatusCode}");
                else
                    log.Information($"Server酱通知已发送：{reason}");
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Server酱通知发送异常（{reason}）");
            }
        });
    }

    private void ScheduleInitialCurrencyCheck(string source, TimeSpan delay)
    {
        initialCurrencyCheckPending = true;
        initialCurrencyCheckSource = source;
        pendingCurrencyCheckAt = DateTime.UtcNow + delay;
        status = $"{source}：准备检测白银币和白金币...";
        log.Information($"{source}流程：先检测钱币并决定是否购买，未触发购买后再进行魔寻宝");
    }

    private void ContinueAfterInitialCurrencyCheck()
    {
        if (!initialCurrencyCheckPending)
            return;

        var source = initialCurrencyCheckSource;
        initialCurrencyCheckPending = false;
        RequestFreelancerScan(source);
    }

    private unsafe int GetInventoryCount(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null) return 0;
            return Math.Max(0, inventory->GetInventoryItemCount(itemId, false, true, true, 0));
        }
        catch (Exception ex)
        {
            log.Error(ex, $"读取钱币数量失败：物品 ID {itemId}");
            return 0;
        }
    }

    private bool TryBeginCurrencyPurchase()
    {
        var requests = CreateCurrencyPurchaseRequests();
        if (requests.Count == 0) return false;
        if (!currencyBuyer.Begin(requests)) return false;

        if (bocchiEnabled) Send("/bocchiillegal off");
        bocchiEnabled = false;
        Send("/vnav stop");
        initialCurrencyCheckPending = false;
        waitingForEntry = waitingForScan = false;
        pendingScanAt = pendingBocchiAt = pendingReturnScanAt = pendingCurrencyCheckAt = pendingPurchaseAt = DateTime.MinValue;
        purchaseRetryDeadline = DateTime.MinValue;
        treasurePhase = TreasurePhase.None;
        treasurePhaseAt = DateTime.MinValue;
        status = currencyBuyer.Status;
        currencyPurchaseStatus = currencyBuyer.Status;
        log.Information("自动购买已接管流程，其他插件行为已暂停");
        return true;
    }

    private bool HasCurrencyPurchaseRequest() => CreateCurrencyPurchaseRequests().Count > 0;

    private List<CurrencyPurchaseRequest> CreateCurrencyPurchaseRequests()
    {
        var requests = new List<CurrencyPurchaseRequest>();
        AddCurrencyPurchaseRequest(requests, CurrencyKind.Silver, silverCurrency, config.SilverPurchaseMode, config.SilverTriggerAmount);
        AddCurrencyPurchaseRequest(requests, CurrencyKind.Gold, goldCurrency, config.GoldPurchaseMode, config.GoldTriggerAmount);
        return requests;
    }

    private void AddCurrencyPurchaseRequest(
        List<CurrencyPurchaseRequest> requests,
        CurrencyKind kind,
        int currentAmount,
        CurrencyPurchaseMode mode,
        int triggerAmount)
    {
        if (mode == CurrencyPurchaseMode.None || currentAmount < triggerAmount) return;
        var cost = GetPurchaseCost(kind, mode);
        var configuredQuantity = GetConfiguredQuantity(kind, mode);
        var quantity = Math.Min(configuredQuantity, currentAmount / cost);
        if (quantity <= 0) return;

        var isSilver = kind == CurrencyKind.Silver;
        requests.Add(new CurrencyPurchaseRequest(
            kind,
            isSilver ? "十二城邦白银币" : "十二城邦白金币",
            isSilver ? SilverCurrencyItemId : GoldCurrencyItemId,
            isSilver ? 0x1B0614u : 0x1B0615u,
            mode == CurrencyPurchaseMode.OldCoffer ? "钱箱" : "终极固定剂",
            mode == CurrencyPurchaseMode.OldCoffer ? OldCofferItemId : UltimateFixativeItemId,
            cost,
            quantity));
    }

    private void OnCurrencyPurchaseFinished(bool success, string message)
    {
        pendingPurchaseAt = purchaseRetryDeadline = DateTime.MinValue;
        currencyPurchaseStatus = success ? message : $"自动购买失败：{message}";
        if (!running) return;
        if (!success)
        {
            Stop(currencyPurchaseStatus);
            return;
        }

        silverCurrency = goldCurrency = -1;
        lastProblemCheckSilver = -1;
        problemCheckBaselineReady = false;
        nextProblemCheckAt = DateTime.UtcNow + ProblemCheckInterval;
        silver = copper = -1;
        initialScan = true;
        nextAllowedScanAt = DateTime.MinValue;
        if (!IsIsland())
        {
            Send("/pdrfe ocn");
            waitingForEntry = true;
            status = "自动购买完成，正在重新进入蜃景幻界新月岛 北征之章...";
        }
        else
        {
            RequestFreelancerScan("自动购买完成后");
        }
    }

    private static int GetPurchaseCost(CurrencyKind kind, CurrencyPurchaseMode mode) => (kind, mode) switch
    {
        (CurrencyKind.Silver, CurrencyPurchaseMode.OldCoffer) => 40,
        (CurrencyKind.Gold, CurrencyPurchaseMode.OldCoffer) => 50,
        (CurrencyKind.Silver, CurrencyPurchaseMode.UltimateFixative) => 1200,
        (CurrencyKind.Gold, CurrencyPurchaseMode.UltimateFixative) => 1920,
        _ => 1,
    };

    private int GetConfiguredQuantity(CurrencyKind kind, CurrencyPurchaseMode mode) => (kind, mode) switch
    {
        (CurrencyKind.Silver, CurrencyPurchaseMode.OldCoffer) => config.SilverCofferQuantity,
        (CurrencyKind.Gold, CurrencyPurchaseMode.OldCoffer) => config.GoldCofferQuantity,
        (CurrencyKind.Silver, CurrencyPurchaseMode.UltimateFixative) => config.SilverFixativeQuantity,
        (CurrencyKind.Gold, CurrencyPurchaseMode.UltimateFixative) => config.GoldFixativeQuantity,
        _ => 0,
    };

    private void NormalizePurchaseConfig()
    {
        config.SilverTriggerAmount = Math.Clamp(config.SilverTriggerAmount, 0, CurrencyCap);
        config.GoldTriggerAmount = Math.Clamp(config.GoldTriggerAmount, 0, CurrencyCap);
        config.SilverCofferQuantity = Math.Clamp(config.SilverCofferQuantity, 1, Math.Max(1, CurrencyCap / 40));
        config.GoldCofferQuantity = Math.Clamp(config.GoldCofferQuantity, 1, Math.Max(1, CurrencyCap / 50));
        config.SilverFixativeQuantity = Math.Clamp(config.SilverFixativeQuantity, 1, Math.Max(1, CurrencyCap / 1200));
        config.GoldFixativeQuantity = Math.Clamp(config.GoldFixativeQuantity, 1, Math.Max(1, CurrencyCap / 1920));
        ClampSelectedPurchaseQuantity(CurrencyKind.Silver, config.SilverPurchaseMode);
        ClampSelectedPurchaseQuantity(CurrencyKind.Gold, config.GoldPurchaseMode);
        if (config.Version < 2)
        {
            config.Version = 2;
            config.Save();
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
        var notifyTowerWeather = config.NotifyTowerWeather;
        if (ImGui.Checkbox("魔之塔蜃景天气出现时通知", ref notifyTowerWeather))
        {
            config.NotifyTowerWeather = notifyTowerWeather;
            config.Save();
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
        var silverKind = kind == CurrencyKind.Silver;
        var name = silverKind ? "白银币" : "白金币";
        var currentAmount = silverKind ? silverCurrency : goldCurrency;
        var mode = silverKind ? config.SilverPurchaseMode : config.GoldPurchaseMode;
        var trigger = silverKind ? config.SilverTriggerAmount : config.GoldTriggerAmount;
        var modeText = mode switch
        {
            CurrencyPurchaseMode.OldCoffer => "自动买钱箱",
            CurrencyPurchaseMode.UltimateFixative => "自动买终极固定剂",
            _ => "不购买",
        };

        ImGui.Text($"{name}：{(currentAmount >= 0 ? currentAmount.ToString() : "未检测")}/{CurrencyCap}");
        ImGui.SetNextItemWidth(190f);
        if (ImGui.BeginCombo($"行为##{name}PurchaseMode", modeText))
        {
            foreach (var candidate in Enum.GetValues<CurrencyPurchaseMode>())
            {
                var label = candidate switch
                {
                    CurrencyPurchaseMode.OldCoffer => "自动买钱箱",
                    CurrencyPurchaseMode.UltimateFixative => "自动买终极固定剂",
                    _ => "不购买",
                };
                if (ImGui.Selectable(label, candidate == mode))
                {
                    mode = candidate;
                    if (silverKind) config.SilverPurchaseMode = mode;
                    else config.GoldPurchaseMode = mode;
                    ClampSelectedPurchaseQuantity(kind, mode);
                    config.Save();
                }
                if (candidate == mode) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.BeginDisabled(mode == CurrencyPurchaseMode.None);
        var cost = GetPurchaseCost(kind, mode);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt($"触发钱币数量##{name}Trigger", ref trigger))
        {
            trigger = Math.Clamp(trigger, cost, CurrencyCap);
            if (silverKind) config.SilverTriggerAmount = trigger;
            else config.GoldTriggerAmount = trigger;
            ClampSelectedPurchaseQuantity(kind, mode);
            config.Save();
        }

        var quantity = GetConfiguredQuantity(kind, mode);
        var maxAtTrigger = Math.Max(1, trigger / cost);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt($"购买数量##{name}Quantity", ref quantity))
        {
            quantity = Math.Clamp(quantity, 1, maxAtTrigger);
            SetConfiguredQuantity(kind, mode, quantity);
            config.Save();
        }
        if (mode != CurrencyPurchaseMode.None)
            ImGui.TextWrapped($"单价：{cost} {name}；触发值下最多可购买 {maxAtTrigger} 个。");
        ImGui.EndDisabled();
    }

    private void ClampSelectedPurchaseQuantity(CurrencyKind kind, CurrencyPurchaseMode mode)
    {
        if (mode == CurrencyPurchaseMode.None) return;
        var trigger = kind == CurrencyKind.Silver ? config.SilverTriggerAmount : config.GoldTriggerAmount;
        var cost = GetPurchaseCost(kind, mode);
        trigger = Math.Clamp(trigger, cost, CurrencyCap);
        if (kind == CurrencyKind.Silver) config.SilverTriggerAmount = trigger;
        else config.GoldTriggerAmount = trigger;
        var quantity = Math.Clamp(GetConfiguredQuantity(kind, mode), 1, Math.Max(1, trigger / cost));
        SetConfiguredQuantity(kind, mode, quantity);
    }

    private void SetConfiguredQuantity(CurrencyKind kind, CurrencyPurchaseMode mode, int quantity)
    {
        switch (kind, mode)
        {
            case (CurrencyKind.Silver, CurrencyPurchaseMode.OldCoffer): config.SilverCofferQuantity = quantity; break;
            case (CurrencyKind.Gold, CurrencyPurchaseMode.OldCoffer): config.GoldCofferQuantity = quantity; break;
            case (CurrencyKind.Silver, CurrencyPurchaseMode.UltimateFixative): config.SilverFixativeQuantity = quantity; break;
            case (CurrencyKind.Gold, CurrencyPurchaseMode.UltimateFixative): config.GoldFixativeQuantity = quantity; break;
        }
    }

    public void DrawStatus()
    {
        ImGui.Text($"状态：{status}");
        ImGui.Text($"当前区域 ID：{clientState.TerritoryType}（目标 1346）");
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
        if (ImGui.CollapsingHeader("Debug"))
        {
            if (ImGui.Button("直接开始寻宝流程（测试用）"))
            {
                if (!running) running = true;
                silver = MaxSilver;
                copper = 0;
                BeginTreasureProcedure();
            }
            if (ImGui.Button("直接开始前往魔之塔流程（测试用）"))
            {
                if (!running) running = true;
                BeginTowerProcedureForTest();
            }
        }
    }

    private sealed class MainWindow : Dalamud.Interface.Windowing.Window
    {
        private readonly Plugin plugin;
        private bool restoreSizePending;
        public MainWindow(Plugin plugin) : base($"OCNFarmer v{PluginVersion}##OCNFarmer")
        {
            this.plugin = plugin;
            if (plugin.config.WindowWidth > 0 && plugin.config.WindowHeight > 0)
            {
                Size = new Vector2(plugin.config.WindowWidth, plugin.config.WindowHeight);
                SizeCondition = ImGuiCond.Always;
                restoreSizePending = true;
            }
            IsOpen = false;
        }
        public override void Draw() => plugin.DrawStatus();

        public override void PostDraw()
        {
            base.PostDraw();
            var size = ImGui.GetWindowSize();
            if (size.X <= 0 || size.Y <= 0) return;
            if (restoreSizePending)
            {
                SizeCondition = ImGuiCond.FirstUseEver;
                restoreSizePending = false;
            }
            if (MathF.Abs(plugin.config.WindowWidth - size.X) < 0.5f &&
                MathF.Abs(plugin.config.WindowHeight - size.Y) < 0.5f) return;
            plugin.config.WindowWidth = size.X;
            plugin.config.WindowHeight = size.Y;
            plugin.config.Save();
        }
    }

    public sealed class PluginConfig : IPluginConfiguration
    {
        public int Version { get; set; } = 2;
        public string CombatJob { get; set; } = "辅助白魔法师";
        public string DiscardPreset { get; set; } = "";
        public bool AutoPurchaseExpanded { get; set; } = true;
        public bool ServerChanExpanded { get; set; } = true;
        public bool ServerChanEnabled { get; set; }
        public string ServerChanApiUrl { get; set; } = "";
        public bool NotifyProblem { get; set; } = true;
        public bool NotifyTreasureComplete { get; set; } = true;
        public bool AutoGoTower { get; set; }
        public bool NotifyTowerArrival { get; set; } = true;
        public bool NotifyTowerWeather { get; set; }
        public bool AutoGoTowerExpanded { get; set; }
        public float WindowWidth { get; set; }
        public float WindowHeight { get; set; }
        public CurrencyPurchaseMode SilverPurchaseMode { get; set; } = CurrencyPurchaseMode.None;
        public CurrencyPurchaseMode GoldPurchaseMode { get; set; } = CurrencyPurchaseMode.None;
        public int SilverTriggerAmount { get; set; } = 9000;
        public int GoldTriggerAmount { get; set; } = 9000;
        public int SilverCofferQuantity { get; set; } = 20;
        public int GoldCofferQuantity { get; set; } = 20;
        public int SilverFixativeQuantity { get; set; } = 1;
        public int GoldFixativeQuantity { get; set; } = 1;

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface) => this.pluginInterface = pluginInterface;

        public void Save() => pluginInterface?.SavePluginConfig(this);
    }
}
