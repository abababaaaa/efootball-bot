using System.IO;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Input;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Bot.Scenarios.CnMyLeague;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;
using EfootballBot.Core.Bot.Scenarios.AiLadder;
using EfootballBot.Core.Observation;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;
using SkiaSharp;

namespace EfootballBot.Core.Bot;

/// <summary>挂机统计：业务计数由场景插件更新，引擎维护感知摘要。</summary>
public sealed class BotStats
{
    public int MatchesDone;
    public int KeyMatches;
    /// <summary>球员满级提示次数（比赛中"满级"通知检测；统计接口，GUI 可展示）。</summary>
    public int PlayersMaxed;
    public DateTime? StartedAt;
    /// <summary>最近一次感知摘要（如 "OCR 12 词 / 检测 3 框"）。</summary>
    public string LastScreen = "";
    public string LastScreenEvidence = "";
    public string LastError = "";
}

/// <summary>
/// 场景插件接口：感知→决策→执行的业务循环在此实现。
/// RunOnceAsync 内部自行观察（BotEngine.ObserveAsync）与操作（Pad/Pad360），
/// 返回建议的下一轮延时 ms。具体场景在 YOLO 模型就位后基于 Detection/Ocr 重建。
/// </summary>
public interface IScenario
{
    string Name { get; }
    Task<int> RunOnceAsync(CancellationToken ct);
}

public sealed class BotEngine : IDisposable
{
    /// <summary>国服游戏进程名（窗口标题：实况足球在线）。</summary>
    public const string CnProcessName = "eFootballOnline";

    /// <summary>路径分隔符集合（文件名提取用；避免 System.IO 命名空间遮蔽问题）。</summary>
    private static readonly char[] PathSeparatorChars = { '\\', '/' };

    private readonly AppConfig _cfg;
    public AppConfig Config => _cfg;
    /// <summary>捕获家族开关：true=整个捕获家族走旧 SharpDX 实现（回退），false=Vortice 新实现（默认）。</summary>
    private readonly bool _captureUseSharpDx;
    /// <summary>虚拟手柄（连接失败按 <see cref="PadBackendInfo.FallbackChain"/> 回退时整体换实例）。</summary>
    public IVirtualPad Pad360 { get; private set; }
    public Pad Pad { get; private set; }
    public readonly WindowCapture Capture;
    /// <summary>DXGI 桌面复制捕获器（Vortice/SharpDX 按 _captureUseSharpDx 二选一，均实现 ICaptureProvider）。</summary>
    private ICaptureProvider? _dxgi;
    private bool _dxgiStepWired;
    private GdiWindowCapture? _gdi;
    public readonly OcrRecognizer Ocr;
    public readonly ObservationService Observation;
    private readonly OnnxSessionFactory _factory;
    private PerceptionPipeline? _perception;
    private readonly SessionScheduler _scheduler;
    private BackgroundParker? _parker;
    /// <summary>GDI+停靠黑帧自动还原是否已执行（每次运行一次，避免反复横跳）。</summary>
    private bool _blackGdiUnparked;
    /// <summary>捕获熔断后是否已降级 GDI（每次运行一次，降级失败才真正停机）。</summary>
    private bool _gdiFallbackTried;
    /// <summary>黑帧暂停告警上次输出时间（30s 节流）。</summary>
    private DateTime _lastBlackLogAt;

    /// <summary>当前场景插件（null = 纯感知观察模式）。</summary>
    public IScenario? Scenario { get; set; }

    // 暂停标志：置位后主循环不再观察/操作，且立即清空手柄输出防残留半按状态
    private volatile bool _paused;

    /// <summary>单步请求：暂停状态下执行一轮「观察 + 决策」后自动回到暂停（见 RequestStep）。</summary>
    private volatile bool _stepOnce;

    /// <summary>暂停/继续场景循环（幂等）。暂停即刻清空手柄输出防残留半按状态
    /// （真实手柄本就处于禁用让位态，无需断开虚拟手柄）。</summary>
    public void SetPaused(bool on)
    {
        if (_paused == on) return;
        _paused = on;
        if (on)
        {
            try { Pad360.ResetAll(); } catch { }
            LogInternal("已暂停：已清空手柄输出，暂停观察与操作", LogLevel.Warn);
        }
        else
        {
            LogInternal("已继续", LogLevel.Info);
        }
    }

    /// <summary>
    /// 单步执行一拍（仅暂停状态有效）：观察一次当前画面、让场景决策一次，然后继续暂停。
    /// 用于陌生界面排障——先看 Bot 对当前画面打算做什么，且只做一步（不会连按跑飞）。
    /// </summary>
    public void RequestStep()
    {
        if (!_paused)
        {
            LogInternal("单步：当前未暂停（挂机本就在连续执行），已忽略", LogLevel.Info);
            return;
        }
        _stepOnce = true;
        LogInternal("单步：执行一轮观察 + 决策…", LogLevel.Info);
    }

    public readonly BotStats Stats = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Observation? _lastObs;
    private long _lastOcrDumpTick;
    private long _lastFrameDumpTick;
    private GameWindowInfo? _window;

    public event Action<string, LogLevel>? Log;
    public event Action<BotStats>? StatsChanged;
    public event Action<Observation>? Observed;

    /// <summary>感知摘要事件（Worker→GUI），Screen 为摘要文本（管道协议旧字段名）。</summary>
    public event Action<ScreenEvent>? ScreenChanged;

    public bool IsRunning => _loop is { IsCompleted: false };
    public GameWindowInfo? Window => _window;
    public FrameData? LatestFrame => Observation.LatestFrame;
    public OcrResult LatestOcr => Observation.LatestOcr;
    /// <summary>感知管线（StartAsync 后可用）；YOLO 模型缺失时仅 OCR。</summary>
    public PerceptionPipeline Perception => _perception
        ?? throw new InvalidOperationException("引擎尚未启动（StartAsync）。");

    public BotEngine(AppConfig cfg)
    {
        _cfg = cfg;
        // 捕获家族整体切换（ModuleFallback.UseSharpDxCapture，默认 false=Vortice）：
        // WGC 设备/GPU 缩放（WindowCapture）与 DXGI 捕获器必须走同一家族，绝不混挂设备指针
        _captureUseSharpDx = cfg.ModuleFallback.UseSharpDxCapture;
        Capture = new WindowCapture(_captureUseSharpDx);
        // 手柄后端（AppConfig.Pad.Backend，唯一后端 Vigem=Xbox 360 形态、占用 XInput 玩家 1）。
        // 任一时刻只存在一只虚拟手柄。
        Pad360 = CreatePad(cfg.Pad.Backend);
        LogInternal($"虚拟手柄后端：{PadBackendInfo.DisplayName(cfg.Pad.Backend)}", LogLevel.Info);
        if (cfg.DryRun)
            LogInternal("影子模式：手柄输出全部拦截，只在日志记录「本来会按什么」——本次不会操作游戏", LogLevel.Warn);
        Pad = new Pad(Pad360, cfg.Timing);

        // EP 降级工厂：OCR 与 YOLO 共享同一 SessionOptions（进程内仅一份 onnxruntime.dll）
        _factory = new OnnxSessionFactory(cfg.Inference.PreferredProvider, LogInternal);
        Ocr = new OcrRecognizer(cfg.OcrEngine,
            downscaleLongSide: cfg.ModuleFallback.DisableOcrDownscale ? 0 : OcrRecognizer.DefaultDownscaleLongSide,
            preferInt8: !cfg.ModuleFallback.UseFp32Ocr);
        if (Ocr.FallbackNote is { } fallbackNote)
            LogInternal($"OCR 引擎回退：{fallbackNote}（现用 {Ocr.LanguageTag}）", LogLevel.Warn);
        else
            LogInternal($"OCR 引擎：{Ocr.LanguageTag}（{Ocr.EngineKind}）", LogLevel.Info);
        LogInternal($"捕获家族：{(_captureUseSharpDx ? "SharpDX（回退）" : "Vortice（默认）")}", LogLevel.Info);

        Observation = new ObservationService(Capture, Ocr, cfg);
        Observation.Log = LogInternal;

        _scheduler = new SessionScheduler(cfg.Schedule, LogInternal);

        Capture.CaptureClosed += (_, msg) => LogInternal($"画面捕获已结束：{msg}", LogLevel.Warn);
    }

    public IReadOnlyList<GameWindowInfo> DetectWindows() => GameWindow.FindAll();

    // ---------------- 生命周期 ----------------

    public async Task StartAsync(string mode)
    {
        if (IsRunning) return;
        _summaryWritten = false; // 新会话：允许再写一次摘要
        Stats.StartedAt ??= DateTime.Now;
        // 场景模式（cndaily=国服天梯巅峰赛 / cnweekly=国服周活动 / cnmyleague=国服我的联赛 /
        // intl=国际服我的联赛 / intlweekly=国际服周活动 / intlladder=国际服AI天梯联赛）
        // 需接管虚拟手柄（B 键逃生依赖）；observe 纯观察不操作游戏
        bool scenarioMode = mode is "cndaily" or "cnweekly" or "cnmyleague" or "intl" or "intlweekly" or "intlladder";
        CancellationToken token;
        try
        {
            bool cn = mode is "cndaily" or "cnweekly" or "cnmyleague";
            token = await PrepareSessionAsync(cn ? CnProcessName : null, connectPad: scenarioMode);
        }
        catch
        {
            // 启动中途失败（如找不到窗口/捕获异常）：捕获会话可能已开启，必须停掉，
            // 否则 Worker 空闲却在后台全速抓帧（僵尸捕获），DWM/CPU 持续被压
            try { Capture.Stop(); } catch { }
            try { _dxgi?.Stop(); } catch { }
            try { _gdi?.Stop(); } catch { }
            throw;
        }

        // 所有模式统一节流到 300ms（场景决策周期 ≥2s，300ms 感知粒度足够）：
        // 全速 30fps 逐帧 SoftwareBitmap 拷贝 + CPU 降采样（LOH 高速churn）会打满 CPU 卡死系统
        Observation.ApplyCaptureThrottle(300);

        // 感知管线（YOLO 模型缺失时仅 OCR，不阻塞启动）
        _perception ??= PerceptionPipeline.Create(_cfg, _factory, LogInternal);
        // 场景体系：cndaily=国服天梯巅峰赛 / cnweekly=国服周活动 / cnmyleague=国服我的联赛 /
        // intl=国际服我的联赛 / intlweekly=国际服周活动 / intlladder=国际服AI天梯联赛
        Scenario = mode switch
        {
            "intlweekly" => new WeeklyEventScenario(this, LogInternal),
            "cnweekly" => new CnWeeklyScenario(this, LogInternal),
            "cndaily" => new PeakLadderScenario(this, LogInternal),
            "cnmyleague" => new CnMyLeagueScenario(this, LogInternal),
            "intlladder" => new AiLadderScenario(this, LogInternal),
            _ when scenarioMode => new MyLeagueScenario(this, LogInternal),
            _ => null,
        };
        LogInternal(
            $"引擎就绪（模式：{mode}）—— EP={_factory.ActiveProvider}，" +
            (_perception.YoloEnabled ? $"YOLO 已启用（{_perception.YoloLabel}）" : "YOLO 未启用（仅 OCR 观察）") +
            (Scenario is not null ? $"，场景：{Scenario.Name}" : ""),
            LogLevel.Success);
        if (Scenario is not null && !_perception.YoloEnabled)
            LogInternal("YOLO 模型缺失，场景决策将大量退化为等待模式", LogLevel.Warn);
        else if (Scenario is null)
            LogInternal("当前为感知观察模式（无场景插件）", LogLevel.Info);
        if (_cfg.FrameDump.Enabled)
            LogInternal($"训练数据采集已开启：每 {_cfg.FrameDump.IntervalMs / 1000.0:F0} 秒存帧至 captures\\（exe 同级目录）", LogLevel.Info);

        // 后台停靠：场景模式下把游戏窗口移出屏幕（观察模式用户要亲眼看画面，不隐藏）。
        // 放在捕获会话建立之后：WGC/GDI 绑定的是窗口本身，与窗口是否在屏幕上无关。
        if (scenarioMode && _cfg.BackgroundParking.Enabled && _window is { } parkWin)
        {
            _parker ??= new BackgroundParker(LogInternal, _cfg.BackgroundParking.WatchdogIntervalMs);
            _parker.Start(parkWin.Handle);
        }

        // 共存让步传感器已随 3.3.0 共存模式移除（全权接管：真实手柄挂机期间禁用让位）

        _gdiFallbackTried = false;
        _loop = Task.Run(async () =>
        {
            try
            {
                int failStreak = 0;
                bool idleThrottled = false; // 暂停/作息休息期间已把捕获降到 2s/帧
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // 暂停：不再观察/操作，只轮询等待恢复或取消。
                        // 捕获同时降到 2s/帧——暂停可能持续整夜，300ms 全速抓帧是纯空转
                        if (_paused)
                        {
                            if (!idleThrottled) { idleThrottled = true; Observation.ApplyCaptureThrottle(2000); }
                            // 单步：临时恢复常规捕获节奏跑一轮「观察 + 场景决策」，然后回到暂停。
                            // 排障用——陌生界面上先看 Bot 会做什么，且只做一步。
                            if (_stepOnce)
                            {
                                _stepOnce = false;
                                Observation.ApplyCaptureThrottle(300);
                                try
                                {
                                    await ObserveAsync(token);
                                    if (Scenario is not null)
                                    {
                                        int delay = await Scenario.RunOnceAsync(token);
                                        LogInternal($"单步完成（场景建议下一轮 {delay}ms），继续暂停", LogLevel.Info);
                                    }
                                    else
                                    {
                                        LogInternal("单步完成（观察模式无场景，仅刷新感知），继续暂停", LogLevel.Info);
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    LogInternal($"单步执行失败：{ex.Message}", LogLevel.Warn);
                                }
                                finally
                                {
                                    Observation.ApplyCaptureThrottle(2000);
                                }
                            }
                            await Task.Delay(400, token);
                            continue;
                        }

                        // 作息调度：超出活跃时段/当日上限/模拟短暂离开时，复位手柄空转等待自动恢复
                        if (!_scheduler.ShouldRunNow(DateTime.Now))
                        {
                            if (!idleThrottled) { idleThrottled = true; Observation.ApplyCaptureThrottle(2000); }
                            if (_scheduler.PauseJustBegan)
                            {
                                try { Pad360.ResetAll(); } catch { }
                            }
                            await Task.Delay(2500, token);
                            continue;
                        }

                        // 恢复活跃：还原常规捕获节奏
                        if (idleThrottled)
                        {
                            idleThrottled = false;
                            Observation.ApplyCaptureThrottle(300);
                        }

                        await ObserveAsync(token);

                        // GDI 兜底 + 后台停靠 = 黑帧（PrintWindow 取不到屏外 D3D 内容）：
                        // 停靠看门狗与还原会互相打架——整体停掉停靠（还原窗口 + 停看门狗），
                        // 放弃隐藏保挂机（WGC/DXGI 均不可用时的唯一可行组合）
                        if (_cfg.BackgroundParking.Enabled &&
                            Observation.Provider is GdiWindowCapture &&
                            _parker is { IsParked: true } &&
                            Observation.LatestFrame is { } gdiFrame &&
                            IsMostlyBlack(gdiFrame) && !_blackGdiUnparked)
                        {
                            _blackGdiUnparked = true;
                            LogInternal("GDI 兜底捕获在停靠状态下取到黑帧（PrintWindow 取不到屏外 D3D 内容）", LogLevel.Warn);
                            _parker.Stop();
                            LogInternal("已自动还原窗口位置并停用本次运行的停靠：放弃隐藏，优先保证挂机可用。" +
                                "重启电脑修复显卡驱动后，WGC 捕获 + 后台停靠即可兼得", LogLevel.Warn);
                        }

                        // 盲操作防护：捕获产出全黑帧（PrintWindow 取不到屏外 D3D 内容/驱动异常/
                        // 输出属主不匹配）→ 感知全盲，此时的任何"识别"都是噪声。挂起场景决策，
                        // 绝不盲按：2026-10-11 用户实机，黑帧被角区探针连续判成 InMatch，
                        // 随机按 A 把联赛主页当比赛挂机、误开新比赛、不选难度不买道具。
                        if (Observation.LatestFrame is { } blindFrame && IsMostlyBlack(blindFrame))
                        {
                            if ((DateTime.Now - _lastBlackLogAt).TotalSeconds >= 30)
                            {
                                _lastBlackLogAt = DateTime.Now;
                                LogInternal("捕获画面全黑：已暂停挂机操作（黑帧下不识别不按键），等待画面恢复…" +
                                    "若持续出现，请勿最小化游戏窗口，或到设置里切换捕获模式", LogLevel.Warn);
                            }
                            await Task.Delay(1000, token);
                            continue;
                        }

                        int delayMs = 300;
                        if (Scenario is not null)
                            delayMs = await Scenario.RunOnceAsync(token);
                        await Task.Delay(delayMs, token);

                        if (failStreak > 0)
                        {
                            failStreak = 0;
                            Stats.LastError = "";
                            EmitStats();
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (CaptureUnrecoverableException ex)
                    {
                        // 后台监察熔断：捕获链路短时间内反复中断，停止重建防止"重建→崩溃→再重建"
                        // 死亡循环拖垮系统。但先给一次 GDI 降级机会——PrintWindow 不创建 D3D 设备、
                        // 不走 DWM，WGC/DXGI 因显卡环境系统性失效（无输出矿卡/驱动异常/远程会话）
                        // 时仍能出图（2026-10-13 矿卡用户：WGC 绑 CMP 30HX 后停靠即断帧，重建无效）。
                        if (!_gdiFallbackTried && Observation.Provider is not GdiWindowCapture)
                        {
                            _gdiFallbackTried = true;
                            LogInternal($"捕获链路熔断，自动降级 GDI 捕获继续挂机：{ex.Message}", LogLevel.Error);
                            // 立即落盘本机判定：下次启动直连 GDI，不再重撞探测链
                            // （若 GDI 随后启动失败，下次启动的直连分支会自动回退完整探测链）
                            CaptureBackendMemory.Save(CaptureBackendKind.Gdi, "WGC/DXGI 捕获熔断降级");
                            var (gw, gh) = _window is { } wwin ? GameWindow.GetClientSize(wwin.Handle) : (1280, 720);
                            if (gw <= 0 || gh <= 0) (gw, gh) = (1280, 720);
                            if (await TryStartGdiCaptureAsync(gw, gh))
                            {
                                Observation.ResetCaptureHealth();
                                Observation.ApplyCaptureThrottle(300);
                                Stats.LastError = "捕获反复中断，已降级 GDI PrintWindow 捕获";
                                EmitStats();
                                continue;
                            }
                        }
                        Stats.LastError = ex.Message;
                        LogInternal(ex.Message, LogLevel.Error);
                        SaveFailureDump("捕获熔断终停");
                        EmitStats();
                        return;
                    }
                    catch (PadSubmitException ex)
                    {
                        // 虚拟手柄提交失败：先按指数退避重试；连续 3 次仍失败 → 重建控制器自愈
                        // （ViGEm 客户端与总线驱动通信失效/设备掉线）
                        failStreak = Math.Min(failStreak + 1, 10);
                        Stats.LastError = ex.Message;
                        LogInternal($"虚拟手柄提交失败（连续第 {failStreak} 次）：{ex.Message}", LogLevel.Warn);
                        if (failStreak >= 3)
                        {
                            await ReconnectPadAsync();
                            failStreak = 0;
                            Stats.LastError = "";
                        }
                        else
                            await Task.Delay(1000, token);
                    }
                    catch (Exception ex)
                    {
                        // 系统级瞬时故障（如 DWM 崩溃重启导致捕获会话失效、设备丢失）：
                        // 指数退避重试而非终止任务，黑屏恢复后挂机自动继续
                        failStreak = Math.Min(failStreak + 1, 10);
                        int backoffMs = Math.Min(1000 << (failStreak - 1), 8000);
                        Stats.LastError = ex.Message;
                        LogInternal($"观察循环异常（连续第 {failStreak} 次，{backoffMs / 1000.0:F0}s 后重试）：{ex.Message}", LogLevel.Warn);
                        if (failStreak == 3)
                            SaveFailureDump("观察循环连续异常");
                        await Task.Delay(backoffMs, token);
                    }
                }
                LogInternal("已停止。", LogLevel.Info);
            }
            catch (OperationCanceledException)
            {
                LogInternal("已停止。", LogLevel.Info);
            }
            finally
            {
                StatsChanged?.Invoke(Stats);
            }
        }, token);

        EmitStats();
    }

    /// <summary>会话初始化：驱动检测 → 找窗口 → 启动捕获 → 接手柄 → 建取消令牌。
    /// 感知观察模式传 connectPad=false：不连接虚拟手柄、不做 slot0 冲突检测、不禁用真实手柄，真实手柄可直接使用。</summary>
    private async Task<CancellationToken> PrepareSessionAsync(string? processName = null, bool connectPad = true)
    {
        // 双显卡笔记本独显直连（HKCU 用户级，无需管理员）：GUI 启动时已写入 → 本 Worker
        // 进程创建时即按独显运行；此处校验兜底并把显卡环境写进日志（远程排障必需）
        try
        {
            var (written, multiGpu, adapters) = GpuPreference.EnsureHighPerformance();
            if (multiGpu && written)
                LogInternal($"检测到双显卡（{adapters}）：已自动设置本程序使用高性能独显运行" +
                    "（等效 Windows 图形设置-高性能），完全生效请重启软件一次", LogLevel.Warn);
            else if (multiGpu)
                LogInternal($"双显卡环境（{adapters}）：独显直连偏好已就绪", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogInternal($"独显偏好检查失败（不影响使用）：{ex.Message}", LogLevel.Debug);
        }

        processName ??= GameWindow.GlobalProcessName;
        _window = GameWindow.FindFirst(processName)
            ?? throw new InvalidOperationException(processName.Contains("Online")
                ? "未找到正在运行的国服游戏窗口（eFootballOnline），请先启动「实况足球在线」。"
                : "未找到正在运行的 eFootball 游戏窗口，请先启动游戏。");

        // 管理员权限失配检测：游戏提权而本程序未提权时，UIPI 会拦截跨完整性级别的窗口操作与
        // 窗口消息（后台停靠 SetWindowPos 恒 ERROR_ACCESS_DENIED、后台鼠标注入失效；
        // WGC 捕获走 DWM，部分环境仍正常出图——2026-10-15 实机：画面正常但停靠失效）。
        // 只提示不强提——UAC 弹窗由用户决定。
        try
        {
            if (!Elevation.IsCurrentProcessElevated() && Elevation.IsProcessElevated(_window.ProcessId))
                LogInternal("检测到游戏正以管理员权限运行，而本程序未提权：系统(UIPI)会拦截本程序的窗口停靠与后台鼠标注入" +
                    "（后台停靠失效；画面捕获部分环境黑屏）。请右键本程序选「以管理员身份运行」，" +
                    "或到 属性→兼容性 勾选「以管理员身份运行此程序」", LogLevel.Warn);
        }
        catch { /* 权限检测失败不影响启动 */ }

        // 非 720p 只在开始捕获时给一条性能提醒；高分辨率的捕获开销由帧池高≤720 降采样缓解。

        // 已在捕获另一个窗口（如国际服→国服切换）时，先停掉旧捕获
        if (Capture.IsRunning && Capture.Hwnd != _window.Handle)
        {
            LogInternal("切换游戏模块：重新绑定画面捕获窗口", LogLevel.Info);
            Capture.Stop();
        }
        if (_dxgi is { IsRunning: true } && _dxgi.Hwnd != _window.Handle)
            _dxgi.Stop();

        if (!Capture.IsRunning && !(_dxgi?.IsRunning ?? false))
        {
            var (w, h) = GameWindow.GetClientSize(_window.Handle);
            if (w <= 0 || h <= 0) (w, h) = (1280, 720);
            if (h > 760)
                LogInternal($"当前游戏分辨率 {w}×{h} 高于 720p，已自动降采样；DXGI 模式可显著降低高分辨率下的捕获开销", LogLevel.Warn);
            await StartCaptureAsync(w, h);
        }

        Observation.Window = _window;

        if (connectPad)
        {
            // ViGEmBus 驱动缺失时静默安装（内置官方安装器；本程序自 v2.0.6 强制管理员启动，
            // 提权上下文内 runas 不再弹 UAC）。失败不阻断——Connect 还会再报一次明确错误。
            if (!Input.VigemPad.IsDriverInstalled())
            {
                LogInternal("未检测到 ViGEmBus 驱动，开始静默安装（内置官方安装器，首次约 10-30 秒）…", LogLevel.Warn);
                try
                {
                    var result = await new EfootballBot.Core.System.DriverInstaller(m => LogInternal(m, LogLevel.Info))
                        .InstallAsync();
                    LogInternal(result.Message, result.DriverReady ? LogLevel.Success : LogLevel.Error);
                    if (!result.DriverReady && result.Kind != EfootballBot.Core.System.DriverSetupResultKind.RebootRequired)
                        throw new InvalidOperationException(result.Message);
                    if (result.Kind == EfootballBot.Core.System.DriverSetupResultKind.RebootRequired)
                        throw new InvalidOperationException(result.Message);
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"ViGEmBus 驱动安装失败：{ex.Message}", ex);
                }
            }

            // 全权接管：存在「已启用」的物理手柄节点时按设置自动禁用（设备管理器级）——
            // XInput 手柄释放玩家 1，虚拟 Xbox 360 手柄成为游戏唯一可见的手柄。
            // 触发条件只看节点不看 slot0 占用：
            // AMD Controller Emulation 等常驻虚拟设备会永久占着玩家 1，没接手柄也会误报空等。
            // 「已禁用」的节点不算在用，禁用生效后本条件立即转 false，不再空等。
            // 影子模式不禁用真实手柄：它只是「看 Bot 想做什么」，用户随时可以自己上手玩
            if (_cfg.DryRun && _cfg.HidHide.HidePhysicalGamepad)
                LogInternal("影子模式：跳过真实手柄禁用（不会影响你正常使用手柄）", LogLevel.Info);
            if (!Pad360.IsConnected && _cfg.HidHide.HidePhysicalGamepad && !_cfg.DryRun
                && HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0)
            {
                LogInternal("检测到真实手柄：自动禁用以让虚拟手柄独占游戏输入（停止挂机时自动恢复）…", LogLevel.Warn);
                Input.PhysicalPadControl.RequestPadHide();
                var deadline = DateTime.Now.AddSeconds(20);
                while (HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0
                       && DateTime.Now < deadline)
                    await Task.Delay(500);
                if (HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0)
                    LogInternal("自动禁用未完全生效——真实手柄可能仍在向游戏输入（若蓝牙手柄仍响应请手动关闭其电源，详见 data\\pad-control.log）", LogLevel.Warn);
                else
                    LogInternal("真实手柄已暂时禁用（虚拟手柄独占输入）", LogLevel.Success);
            }
            await ConnectPadWithFallbackAsync();
            LogInternal("虚拟手柄已连接（Xbox 360 形态，需占玩家 1 才能被游戏消费）",
                LogLevel.Success);

            // 关键一步：连上之后必须验证「玩家 1 到底是虚拟手柄还是真实手柄」。
            // 旧流程只在上面的"连接前"按设备节点禁用真实手柄，两种情形会漏：
            // ① 真实手柄在虚拟手柄连上之后才被系统认作玩家 1（蓝牙重连/开机时序）；
            // ② 设备节点已禁用但 XInput 仍把它报为连接（有线手柄常见）。
            // 漏掉的表现就是用户看到的那一幕：bot 在跑但游戏对任何按键都没反应。
            var (outcome, message) = PadSlotWitness.EnsureVirtualPadInSlot0(
                () => Pad360, _cfg.HidHide.HidePhysicalGamepad, _cfg.DryRun, LogInternal);
            if (outcome == PadSlotWitness.YieldOutcome.AbortAskUser)
            {
                // 玩家 1 抢不回来 = 游戏不会读 bot 的任何输入，继续挂机只会白跑并让用户以为程序坏了。
                // 直接终止本次流程，错误码由 GUI 转成弹窗，明确请用户物理断开手柄。
                try { Pad360.Disconnect(); } catch { }
                throw new PadTakeoverBlockedException(PadTakeoverBlockedException.Code + " " + message);
            }
            if (outcome is PadSlotWitness.YieldOutcome.Ready or PadSlotWitness.YieldOutcome.Recovered)
                LogInternal("玩家 1 归属已确认：虚拟手柄独占游戏输入", LogLevel.Success);
        }
        else
        {
            LogInternal("感知观察模式：不接管手柄，真实手柄可直接使用", LogLevel.Info);
        }

        Stats.StartedAt = DateTime.Now;
        _cts = new CancellationTokenSource();
        // 会话标记：sync 提权任务凭它跳过 pad-restore（挂机中绝不能复活被禁用的真实手柄）
        Input.PhysicalPadControl.WriteSessionMarker();
        return _cts.Token;
    }

    /// <summary>按后端种类创建虚拟手柄实例（仅构造不连接）。唯一后端 ViGEm Xbox 360。
    /// 影子模式（cfg.DryRun）下包一层 <see cref="DryRunPad"/>：连接/断开照常，输入全部拦下并记日志。</summary>
    private IVirtualPad CreatePad(PadBackendKind kind)
    {
        IVirtualPad pad = new VigemPad(m => LogInternal(m, LogLevel.Info));
        return _cfg.DryRun ? new DryRunPad(pad, LogInternal) : pad;
    }

    /// <summary>
    /// 连接虚拟手柄。单后端后无回退可言（保留换实例重连的骨架：连接失败时重建一次实例再试，
    /// ViGEm 客户端连接态异常时重建实例可跳过损坏的控制器对象）。
    /// </summary>
    private async Task ConnectPadWithFallbackAsync()
    {
        PadBackendKind[] chain = PadBackendInfo.FallbackChain(_cfg.Pad.Backend);
        for (int i = 0; i < chain.Length; i++)
        {
            if (Pad360.Kind != chain[i])
            {
                try { Pad360.Dispose(); } catch { }
                Pad360 = CreatePad(chain[i]);
                Pad = new Pad(Pad360, _cfg.Timing);
            }

            try
            {
                Pad360.Connect();
                if (i > 0)
                    LogInternal($"已回退到 {PadBackendInfo.DisplayName(chain[i])}", LogLevel.Warn);
                return;
            }
            catch (Exception ex)
            {
                LogInternal($"{PadBackendInfo.DisplayName(chain[i])} 连接失败：{ex.Message}", LogLevel.Warn);
            }
        }
        throw new InvalidOperationException("虚拟手柄连接失败，请查看上方日志后重试。");
    }

    /// <summary>
    /// 运行期手柄级自愈：断开→重建控制器→（Connect 内部）重新探测槽位。
    /// 提交失败多为 ViGEm 客户端与总线驱动通信失效（设备掉线/服务重启），
    /// 重建实例可跳过损坏的控制器对象。失败不抛——交回主循环退避，
    /// 下一轮提交失败会再次触发重建。
    /// </summary>
    private async Task ReconnectPadAsync()
    {
        LogInternal("虚拟手柄提交连续失败，正在重建控制器自愈…", LogLevel.Warn);
        try { Pad360.Disconnect(); } catch { }
        await Task.Delay(500);
        try
        {
            Pad360.Connect();
            LogInternal("虚拟手柄已重建，挂机继续。", LogLevel.Success);
        }
        catch (Exception ex)
        {
            LogInternal($"虚拟手柄重建失败：{ex.Message}（继续退避，下次提交失败再触发重建）", LogLevel.Error);
        }
    }

    /// <summary>
    /// 按配置启动捕获后端，失败逐级降级：WGC / DXGI（按配置优先）→ GDI PrintWindow 兜底。
    /// WGC 在无硬件 GPU（WARP）时被 WindowCapture 拒绝启动——WGC 帧池建在 WARP 设备上
    /// 会确定性触发 dwm.exe (d3d10warp.dll) 崩溃（系统卡死黑屏），降级链保证任何环境
    /// 下要么用上安全后端、要么明确报错，绝不走 WARP+WGC 的致命路径。
    /// </summary>
    private async Task StartCaptureAsync(int w, int h)
    {
        var failures = new List<string>();

        async Task<bool> TryStartDxgiAsync()
        {
            try
            {
                _dxgi ??= _captureUseSharpDx
                    ? new DxgiDesktopCapture()
                    : (ICaptureProvider)new DxgiDesktopCaptureVortice();
                if (!_dxgiStepWired)
                {
                    switch (_dxgi)
                    {
                        case DxgiDesktopCapture sharp:
                            sharp.StepLog += s => LogInternal($"[捕获] {s}", LogLevel.Info);
                            break;
                        case DxgiDesktopCaptureVortice vortice:
                            vortice.StepLog += s => LogInternal($"[捕获] {s}", LogLevel.Info);
                            break;
                    }
                    _dxgiStepWired = true;
                }
                if (!_dxgi.IsRunning)
                {
                    LogInternal($"开始捕获游戏窗口（DXGI 桌面复制，{w}×{h}，高性能全分辨率）", LogLevel.Info);
                    LogInternal("DXGI 捕获整个显示器画面：挂机时游戏窗口请勿最小化或被其他窗口遮挡", LogLevel.Warn);
                    await Task.Run(() => _dxgi.Start(_window!.Handle, w, h));
                }
                // Desktop Duplication 仅在桌面内容变化时产出帧，等待首帧最多 5s
                var deadline = DateTime.Now.AddSeconds(5);
                while (DateTime.Now < deadline && _dxgi.LastFrameAt == DateTime.MinValue)
                    await Task.Delay(100);
                if (_dxgi.LastFrameAt == DateTime.MinValue)
                    throw new TimeoutException("DXGI 5s 内未收到桌面帧（画面静止/设备不支持/被远程桌面占用）");

                // 黑帧校验（3s 窗口）：驱动异常/虚拟显示器环境下桌面复制会产出全黑帧
                // （2026-10-10 15:48 实机：帧在流但 OCR 0 词，感知全盲）。全黑=假成功，
                // 判失败走 GDI 兜底；正常桌面首帧即有内容，开销仅 ~250ms。
                var checkDeadline = DateTime.Now.AddSeconds(3);
                bool sawContent = false;
                while (DateTime.Now < checkDeadline)
                {
                    await Task.Delay(250);
                    var probeFrame = _dxgi.TryGetLatest();
                    if (probeFrame is not null && !IsMostlyBlack(probeFrame)) { sawContent = true; break; }
                }
                if (!sawContent)
                    throw new TimeoutException("DXGI 捕获画面全黑（显示器输出/显卡驱动异常），不可用于感知");

                if (Observation.Provider != _dxgi)
                {
                    try { Capture.Stop(); } catch { }
                    try { _gdi?.Stop(); } catch { }
                    Observation.AttachProvider(_dxgi);
                }
                LogInternal("DXGI 桌面复制捕获已就绪", LogLevel.Success);
                return true;
            }
            catch (Exception ex)
            {
                LogInternal($"DXGI 捕获不可用：{ex.Message}", LogLevel.Warn);
                try { _dxgi?.Stop(); } catch { }
                failures.Add($"DXGI：{ex.Message}");
                return false;
            }
        }

        async Task<bool> TryStartWgcAsync()
        {
            try
            {
                if (!Capture.IsRunning)
                {
                    LogInternal($"开始捕获游戏窗口（WGC 窗口捕获，{w}×{h}，后台可捕获/可被遮挡）", LogLevel.Info);
                    await Task.Run(() => Capture.Start(_window!.Handle, w, h));
                }
                if (Observation.Provider != Capture)
                {
                    try { _dxgi?.Stop(); } catch { }
                    try { _gdi?.Stop(); } catch { }
                    Observation.AttachProvider(Capture);
                }
                return true;
            }
            catch (Exception ex)
            {
                LogInternal($"WGC 捕获不可用：{ex.Message}", LogLevel.Warn);
                try { Capture.Stop(); } catch { }
                failures.Add($"WGC：{ex.Message}");
                return false;
            }
        }

        // 上次运行的本机判定（data\capture-backend.json）：Wgc 模式下直连已验证的后端，
        // 免撞完整探测链（虚拟显示器/无输出显卡机器上探测全程曾需约 1 分钟）。
        // 用户显式选择 DXGI 模式时不消费记忆——那是"我就要用 DXGI"的明确意图。
        if (_cfg.CaptureMode == CaptureMode.Wgc && CaptureBackendMemory.Load() is { } remembered)
        {
            switch (remembered.Kind)
            {
                case CaptureBackendKind.Gdi:
                    LogInternal($"上次运行判定本机 WGC 捕获始终无帧（{remembered.At:yyyy-MM-dd HH:mm}），" +
                        "本次直接使用 GDI 捕获。若显卡驱动已修复，删除 data\\capture-backend.json 可恢复自动探测", LogLevel.Info);
                    if (await TryStartGdiCaptureAsync(w, h)) return;
                    LogInternal("GDI 捕获启动失败，忽略上次判定，走完整探测链", LogLevel.Warn);
                    break;
                case CaptureBackendKind.Warp:
                    Capture.ForceWarpDevice = true;
                    LogInternal("上次运行判定本机需 WARP 兼容模式，WGC 直接以软渲染设备启动", LogLevel.Info);
                    break;
            }
        }

        // 后台停靠会把窗口移出屏幕：DXGI 桌面复制抓的是屏幕上那块区域，窗口不在屏幕上即黑屏失效，
        // 此时无论用户选什么都强制走 WGC → GDI 链（两者按窗口捕获，窗口在屏幕外照常出帧）
        bool park = _cfg.BackgroundParking.Enabled;
        if (park && _cfg.CaptureMode == CaptureMode.Dxgi)
            LogInternal("后台停靠已开启：游戏窗口将移出屏幕，DXGI 桌面复制无法捕获，本次自动改用 WGC/GDI 捕获链", LogLevel.Warn);

        if (!park && _cfg.CaptureMode == CaptureMode.Dxgi)
        {
            if (await TryStartDxgiAsync()) return;
            if (await TryStartWgcAsync()) return;
        }
        else
        {
            if (await TryStartWgcAsync()) return;
            if (!park && await TryStartDxgiAsync()) return;
        }
        if (await TryStartGdiCaptureAsync(w, h, failures)) return;

        throw new InvalidOperationException(
            "所有捕获后端均不可用：" + string.Join("；", failures) +
            "。请确认游戏窗口存在、显卡驱动正常（事件查看器查 dwm.exe 记录），必要时重启电脑。");
    }

    /// <summary>启动/切换到 GDI PrintWindow 捕获（无 D3D 依赖的最终兜底）。
    /// 启动降级链与运行期熔断降级共用：GDI 不创建 D3D 设备、不参与 DWM，
    /// WGC/DXGI 因显卡环境系统性失效（矿卡/无头卡/远程会话/驱动挂起）时仍能出图。</summary>
    private async Task<bool> TryStartGdiCaptureAsync(int w, int h, ICollection<string>? failures = null)
    {
        try
        {
            _gdi ??= new GdiWindowCapture();
            if (!_gdi.IsRunning)
            {
                LogInternal("开始捕获游戏窗口（GDI PrintWindow 兜底，无 D3D 依赖，后台可捕获）", LogLevel.Warn);
                await Task.Run(() => _gdi.Start(_window!.Handle, w, h));
            }
            if (Observation.Provider != _gdi)
            {
                try { Capture.Stop(); } catch { }
                try { _dxgi?.Stop(); } catch { }
                Observation.AttachProvider(_gdi);
            }
            LogInternal("GDI 兜底捕获已就绪", LogLevel.Success);
            return true;
        }
        catch (Exception ex)
        {
            LogInternal($"GDI 捕获不可用：{ex.Message}", LogLevel.Warn);
            try { _gdi?.Stop(); } catch { }
            failures?.Add($"GDI：{ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { Pad360.ResetAll(); } catch { }        // 停止即拔掉虚拟手柄：其他应用立即看不到任何手柄（旧实现保持在线到引擎销毁，
        // 用户停了挂机别的游戏里还挂着一只“幽灵手柄”）。下次开始重新连接。
        try { Pad360.Disconnect(); } catch { }
        // 全权接管收尾：恢复被禁用的真实手柄（火后不管，不拖慢停止流程）
        try
        {
            Input.PhysicalPadControl.RemoveSessionMarker();
            if (_cfg.HidHide.HidePhysicalGamepad && Input.PhysicalPadControl.HasPendingRestore())
                Input.PhysicalPadControl.RunTaskSilently(Input.PhysicalPadControl.PadRestoreTaskName, waitForExit: false);
        }
        catch { }
        // 先还原后台停靠的窗口位置，再断捕获（还原依赖窗口仍在捕获会话里无影响，但先还原能让用户立刻看到游戏）
        try { _parker?.Stop(); } catch { }
        // 显式停止捕获会话：挂机停止后不再后台抓帧。
        // 长时间运行的 WGC 捕获会使 dwm.exe 内存累积膨胀，空闲期必须断开。
        try { Capture.Stop(); } catch { }
        try { _dxgi?.Stop(); } catch { }
        try { _gdi?.Stop(); } catch { }
        _lastObs = null;
        // 注意：上面 Disconnect 只在停止时发生。挂机运行期间绝不反复断开/重连虚拟手柄——
        // 手柄一旦被游戏看到「断开→重连」，eFootball 会把光标停靠到顶部头部的「手柄归属」图标上，
        // 重连设备的 Left/A 全部失效（焦点不在磁贴行）。运行期自愈走 ReconnectPadAsync，
        // 仅在提交连续失败（手柄实质已失效）时才重建。

        WriteSessionSummary("正常停止", isError: false);
    }

    /// <summary>异常停止（场景报错终止）：自动留档当前画面后正常停止。
    /// 用户反馈排障从「读日志猜画面」变成直接看帧。</summary>
    public void StopWithError(string reason)
    {
        SaveFailureDump(reason);
        Stop();
        WriteSessionSummary($"异常停止：{reason}", isError: true);
    }

    /// <summary>本次运行是否已写过会话摘要（Stop 可能被多处调用，摘要只写一次）。</summary>
    private bool _summaryWritten;

    /// <summary>
    /// 会话摘要：一行日志 + 一条 JSONL（data\session-summary.jsonl）。
    /// 用途是把「这次挂了多久、打了多少场、为什么停」变成可聚合数据——
    /// 此前这些信息只散落在日志里，想比较两天的成绩得自己翻。
    /// </summary>
    private void WriteSessionSummary(string reason, bool isError)
    {
        if (_summaryWritten) return;
        _summaryWritten = true;
        try
        {
            var s = Stats;
            TimeSpan dur = s.StartedAt is { } t ? DateTime.Now - t : TimeSpan.Zero;
            int suppressed = Pad360 is DryRunPad dr ? dr.SuppressedActions : 0;
            string line = $"本次会话摘要：场次 {s.MatchesDone}（关键 {s.KeyMatches}）· 用时 {dur:hh\\:mm} · " +
                          (Scenario is not null ? $"场景 {Scenario.Name} · " : "") +
                          (suppressed > 0 ? $"影子模式拦截 {suppressed} 次输入 · " : "") +
                          $"结束原因：{reason}";
            LogInternal(line, isError ? LogLevel.Warn : LogLevel.Success);

            var record = new
            {
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                mode = Scenario?.Name ?? "observe",
                matches = s.MatchesDone,
                keyMatches = s.KeyMatches,
                playersMaxed = s.PlayersMaxed,
                durationMinutes = Math.Round(dur.TotalMinutes, 1),
                dryRun = _cfg.DryRun,
                reason,
                error = isError,
            };
            string path = Path.Combine(AppPaths.DataDir, "session-summary.jsonl");
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(path, global::System.Text.Json.JsonSerializer.Serialize(record) + Environment.NewLine);

            Notifier.Send(_cfg, $"挂机{(isError ? "异常停止" : "已停止")}", line, isError, LogInternal);
        }
        catch (Exception ex)
        {
            LogInternal($"会话摘要写入失败：{ex.Message}", LogLevel.Debug);
        }
    }

    /// <summary>
    /// 异常留档：当前帧存 JPG 到 data\failures\日期\（文件名带原因），滚动保留最近 7 天 / 200MB。
    /// ModuleFallback.DisableFailureDumps 可关；存图失败静默（留档不影响挂机）。
    /// </summary>
    public void SaveFailureDump(string reason)
    {
        if (_cfg.ModuleFallback.DisableFailureDumps) return;
        try
        {
            if (LatestFrame is not { } f || f.Bgra.Length == 0) return;
            var pixels = (byte[])f.Bgra.Clone();
            string safeReason = new string(reason.Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(40).ToArray());
            string day = DateTime.Now.ToString("yyyyMMdd");
            string dir = Path.Combine(AppPaths.DataDir, "failures", day);
            LogInternal($"已保存异常画面留档：data\\failures\\{day}\\", LogLevel.Info);
            _ = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var info = new SKImageInfo(f.Width, f.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    unsafe
                    {
                        fixed (byte* p = pixels)
                        {
                            using var bmp = new SKBitmap();
                            if (!bmp.InstallPixels(info, (nint)p, info.RowBytes)) return;
                            using var img = SKImage.FromBitmap(bmp);
                            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
                            using var fs = File.Create(Path.Combine(dir, $"{DateTime.Now:HHmmss_fff}_{safeReason}.jpg"));
                            data.SaveTo(fs);
                        }
                    }
                    CleanupFailureDumps();
                }
                catch { /* 留档失败静默 */ }
            });
        }
        catch { }
    }

    /// <summary>failure 留档滚动清理：删 7 天前的日目录；总量超 200MB 从最旧开始删。</summary>
    private static void CleanupFailureDumps()
    {
        try
        {
            string root = Path.Combine(AppPaths.DataDir, "failures");
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.Now.AddDays(-7);
            long totalBytes = 0;
            var dayDirs = Directory.GetDirectories(root).OrderBy(d => d).ToList();
            foreach (var d in dayDirs.Where(d => global::System.DateTime.TryParseExact(
                         Path.GetFileName(d), "yyyyMMdd", null,
                         global::System.Globalization.DateTimeStyles.None, out var t) && t < cutoff).ToList())
            {
                try { Directory.Delete(d, recursive: true); } catch { }
            }
            dayDirs = Directory.GetDirectories(root).OrderBy(d => d).ToList();
            var files = dayDirs.SelectMany(Directory.EnumerateFiles).Select(f => new FileInfo(f)).ToList();
            totalBytes = files.Sum(fi => fi.Length);
            const long cap = 200L * 1024 * 1024;
            foreach (var fi in files)
            {
                if (totalBytes <= cap) break;
                try
                {
                    totalBytes -= fi.Length;
                    fi.Delete();
                }
                catch { }
            }
        }
        catch { }
    }

    // ---------------- 感知观察 ----------------

    /// <summary>
    /// 获取一帧新鲜画面 → OCR（节流）→ YOLO 检测（仅新帧）→ 组装 Observation 并派发事件。
    /// 捕获自愈 + OCR 节流由 ObservationService 负责。
    /// </summary>
    public async Task<Observation> ObserveAsync(CancellationToken ct)
    {
        // 比赛画面（OCR 零词 + YOLO 零框）文字密度天然为零——全帧 OCR 是观察循环里最大
        // 单项 CPU，比赛中基本全浪费（比赛判定靠角区探针 + YOLO，见下方注释）。上一轮感知
        // 为全空时把 OCR 间隔放宽 3 倍（≤5s 封顶）：文字/按钮一出现（弹窗、终场、回菜单）
        // 下一轮立刻恢复常规节奏，最多慢一轮。
        int ocrIntervalMs = _cfg.Timing.OcrIntervalMs;
        if (_lastObs is { } prev && prev.Ocr.Words.Count == 0 && prev.Detections.Count == 0)
            ocrIntervalMs = Math.Min(ocrIntervalMs * 3, 5000);

        var (f, ocr) = await Observation.ObserveAsync(ct, ocrIntervalMs);

        // 距上次 OCR 不足间隔时返回缓存帧，直接复用上一次感知结果
        if (_lastObs is not null && _lastObs.Frame == f && _lastObs.Ocr == ocr)
            return _lastObs;

        var dets = await _perception!.DetectAsync(f, ct);
        var obs = new Observation(f, ocr, dets);
        _lastObs = obs;
        DumpFrame(f);

        // ---------------- 事件派发 ----------------
        bool yoloOn = _perception.YoloEnabled;
        string summary = $"OCR {ocr.Words.Count} 词" + (yoloOn ? $" / 检测 {dets.Count} 框" : "");
        var evidence = dets.Select(d => $"{d.ClassName} {d.Score:F2}").Take(8).ToList();

        ScreenChanged?.Invoke(new ScreenEvent
        {
            Screen = summary,
            Score = yoloOn ? dets.Count : ocr.Words.Count,
            Evidence = evidence,
        });

        if (Stats.LastScreen != summary)
        {
            Stats.LastScreen = summary;
            Stats.LastScreenEvidence = string.Join(",", evidence);
            LogInternal($"感知 → {summary}（{Stats.LastScreenEvidence}）", LogLevel.Debug);
            EmitStats();
        }

        // 周期性输出 OCR 原文（Debug 级：比赛中 YOLO 无检测是常态，Warn 会刷屏）。
        // 零检测帧 5s 一条（过场/比赛画面）；有检测帧 30s 一条——此前只在零检测时输出，
        // 带按钮框的弹窗帧（如关键比赛弹窗）恰恰永远没有 OCR 线索，远程排障抓瞎
        // （2026-10-15 关键比赛弹窗反馈包实测）。
        long now = Environment.TickCount64;
        if (now - _lastOcrDumpTick > (dets.Count == 0 ? 5000 : 30000))
        {
            _lastOcrDumpTick = now;
            // 全量输出：Take(25) 在 90~125 词的页面上只见零头（2026-10-21 反馈包：
            // 组别主页的 前往比赛/组别分布 是否在剩余 66 词里全靠猜，远程排障瞎子）
            var words = ocr.MergedWords.Select(w => w.Text);
            LogInternal($"[OCR原文] {string.Join(" | ", words)}", LogLevel.Debug);
        }

        Observed?.Invoke(obs);
        return obs;
    }

    // ---------------- 杂项 ----------------

    /// <summary>
    /// 训练数据采集：按间隔把观察帧存为 JPG（exe 同级 captures\日期\HHmmss_fff.jpg），
    /// 后台任务执行不阻塞观察循环；失败静默（采集不影响挂机）。仅新帧触发，间隔内重复画面跳过。
    /// </summary>
    private void DumpFrame(FrameData f)
    {
        var cfg = _cfg.FrameDump;
        if (!cfg.Enabled || cfg.IntervalMs < 2000) return;
        long now = Environment.TickCount64;
        if (now - _lastFrameDumpTick < cfg.IntervalMs) return;
        _lastFrameDumpTick = now;

        // 帧池会复用数组：先在观察线程拷贝像素，后台任务只做编码写盘
        var pixels = (byte[])f.Bgra.Clone();
        // exe 真实目录（AppContext.BaseDirectory 在单文件自解压下指向 %TEMP% 解压目录，不能用）
        string dir = Path.Combine(AppPaths.ExeDir, "captures", DateTime.Now.ToString("yyyyMMdd"));
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                var info = new SKImageInfo(f.Width, f.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                unsafe
                {
                    fixed (byte* p = pixels)
                    {
                        // InstallPixels 零拷贝借用拷贝出的数组（fixed 固定），编码结束前不释放
                        using var bmp = new SKBitmap();
                        if (!bmp.InstallPixels(info, (nint)p, info.RowBytes)) return;
                        using var img = SKImage.FromBitmap(bmp);
                        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
                        using var fs = File.Create(Path.Combine(dir, $"{DateTime.Now:HHmmss_fff}.jpg"));
                        data.SaveTo(fs);
                    }
                }
            }
            catch { /* 存帧失败静默 */ }
        });
    }

    public void LogInternal(string msg, LogLevel lv)
    {
        Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}", lv);
        EmitStats();
    }

    /// <summary>
    /// 采样判定一帧是否接近全黑（RGB 均 &lt;8 的采样点 ≥99%）。
    /// 用于 DXGI 启动黑帧校验：驱动异常/虚拟显示器下桌面复制产出黑帧，感知不可用。
    /// </summary>
    internal static bool IsMostlyBlack(FrameData f)
    {
        var bgra = f.Bgra;
        if (bgra.Length < 4) return true;
        const int step = 97; // 步长取素数散布采样，覆盖整幅画面
        int total = 0, dark = 0;
        for (int i = 0; i + 3 < bgra.Length; i += step * 4)
        {
            total++;
            if (bgra[i] < 8 && bgra[i + 1] < 8 && bgra[i + 2] < 8) dark++;
        }
        return total > 0 && dark * 100 / total >= 99;
    }

    private void EmitStats() => StatsChanged?.Invoke(Stats);

    public void Dispose()
    {
        Stop();
        try { _perception?.Dispose(); } catch { }
        try { Observation.Dispose(); } catch { }
        try { Ocr.Dispose(); } catch { }
        try { _dxgi?.Dispose(); } catch { }
        try { _gdi?.Dispose(); } catch { }
        try { Pad360.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}

/// <summary>后台监察熔断异常：捕获链路短时间内反复中断，继续重试只会拖垮系统（典型：WGC 反复触发 DWM 崩溃黑屏）。主循环收到后直接停止任务，不再自愈重试。</summary>
public sealed class CaptureUnrecoverableException : Exception
{
    public CaptureUnrecoverableException(string message) : base(message) { }
}
