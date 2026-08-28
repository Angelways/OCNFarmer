using System.Numerics;
using System.Text.RegularExpressions;
using System.Globalization;
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

    private enum TreasurePhase { None, FirstMove, FirstCrystal, FirstWaitPlayers, InnerMount, InnerStart, InnerReturn, SecondMove, SecondCrystal, SecondWaitPlayers, OuterMount, OuterStart, OuterReturn, LeaveDuty, Reentry }

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
    private string status = "未运行";

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
        if (!IsIsland())
        {
            Send("/pdrfe ocn");
            waitingForEntry = true;
            status = "正在进入蜃景幻界新月岛 北征之章...";
            return;
        }
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
        if (treasurePhase != TreasurePhase.None)
        {
            UpdateTreasureProcedure();
            return;
        }
        if (!IsIsland())
        {
            if (bocchiEnabled) Send("/bocchiillegal off");
            bocchiEnabled = false;
            status = "当前未在蜃景幻界新月岛 北征之章中，正在自动进入...";
            return;
        }
        // 从副本外进入时只接受品级同步聊天消息，不使用时间兜底判断。
        if (waitingForEntry) return;
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

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;

        if (!running) return;

        // 该系统消息比 TerritoryType 更能说明副本已经完成加载。
        if (text.Contains("当前任务设有品级同步限制", StringComparison.Ordinal))
        {
            log.Information("检测到蜃景幻界新月岛 北征之章品级同步系统消息，开始首次钱币检测");
            if (waitingForEntry)
            {
                waitingForEntry = false;
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
            treasurePhase = TreasurePhase.LeaveDuty;
            treasurePhaseAt = DateTime.UtcNow + ReturnScanDelay;
            status = "外环寻宝完成，即将自动重进副本";
            log.Information("检测到寻宝外环的本角色亚返回完成消息");
            return;
        }
        if (!initialScan && ownReturnCompleted)
        {
            log.Information($"检测到本角色 {localPlayerName} 的亚返回完成消息，将在 5 秒后检测钱币，并按间隔决定是否检测宝箱");
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
                Send("/vnav moveto 882 258.5 882");
                treasurePhase = TreasurePhase.FirstCrystal;
                treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(12);
                status = "正在移动至小水晶区域，等待 12 秒...";
                return;
            case TreasurePhase.FirstCrystal:
                BeginCrystalWait(true);
                return;
            case TreasurePhase.SecondMove:
                Send("/vnav moveto 882 258.5 882");
                treasurePhase = TreasurePhase.SecondCrystal;
                treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(12);
                status = "正在移动至小水晶区域，等待 12 秒...";
                return;
            case TreasurePhase.SecondCrystal:
                BeginCrystalWait(false);
                return;
            case TreasurePhase.FirstWaitPlayers:
            case TreasurePhase.SecondWaitPlayers:
                CheckCrystalPlayers();
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
                status = "已开始内环寻宝...";
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
                    RequestFreelancerScan("新循环");
                }
                break;
        }

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
            status = "周围 50 米内无玩家，1 秒后召唤随机坐骑...";
        }
        else
        {
            treasurePhase = TreasurePhase.OuterMount;
            treasurePhaseAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            status = "周围 50 米内无玩家，1 秒后召唤随机坐骑...";
        }
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
        log.Information($"钱币检测：白银币 {silverCurrency}/{CurrencyCap}，白金币 {goldCurrency}/{CurrencyCap}");
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
        if (ImGui.CollapsingHeader("Debug"))
        {
            if (ImGui.Button("直接开始寻宝流程（测试用）"))
            {
                if (!running) running = true;
                silver = MaxSilver;
                copper = 0;
                BeginTreasureProcedure();
            }
        }
    }

    private sealed class MainWindow : Dalamud.Interface.Windowing.Window
    {
        private readonly Plugin plugin;
        public MainWindow(Plugin plugin) : base($"OCNFarmer v{PluginVersion}##OCNFarmer") { this.plugin = plugin; IsOpen = false; }
        public override void Draw() => plugin.DrawStatus();
    }

    public sealed class PluginConfig : IPluginConfiguration
    {
        public int Version { get; set; } = 2;
        public string CombatJob { get; set; } = "辅助白魔法师";
        public string DiscardPreset { get; set; } = "";
        public bool AutoPurchaseExpanded { get; set; } = true;
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
