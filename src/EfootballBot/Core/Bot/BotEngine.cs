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
    public readonly VirtualGamepad360 Pad360;
    public readonly Pad Pad;
    public readonly WindowCapture Capture = new();
    private DxgiDesktopCapture? _dxgi;
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

    /// <summary>暂停/继续场景循环（幂等）。暂停即刻复位虚拟手柄，恢复后从下一轮观察继续。</summary>
    public void SetPaused(bool on)
    {
        if (_paused == on) return;
        _paused = on;
        if (on)
        {
            try { Pad360.ResetAll(); } catch { }
            LogInternal("已暂停：手柄已复位，暂停观察与操作", LogLevel.Warn);
        }
        else
        {
            LogInternal("已继续", LogLevel.Info);
        }
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
        Pad360 = new VirtualGamepad360();
        Pad = new Pad(Pad360, cfg.Timing);

        // EP 降级工厂：OCR 与 YOLO 共享同一 SessionOptions（进程内仅一份 onnxruntime.dll）
        _factory = new OnnxSessionFactory(cfg.Inference.PreferredProvider, LogInternal);
        Ocr = new OcrRecognizer(cfg.OcrEngine, cfg.OcrLanguage,
            downscaleLongSide: cfg.ModuleFallback.DisableOcrDownscale ? 0 : OcrRecognizer.DefaultDownscaleLongSide,
            preferInt8: !cfg.ModuleFallback.UseFp32Ocr);
        LogInternal($"OCR 引擎：{Ocr.LanguageTag}（{Ocr.EngineKind}）", LogLevel.Info);

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
    /// 感知观察模式传 connectPad=false：不连接虚拟手柄、不做 slot0 冲突检测、不触发 HidHide 隔离，真实手柄可直接使用。</summary>
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

        if (connectPad && !VirtualGamepad360.IsDriverInstalled())
        {
            // 自动安装内嵌的 ViGEmBus 驱动（提权静默装），不再直接失败让用户手动去环境检测
            LogInternal("未检测到 ViGEmBus 驱动：自动开始安装（请在弹出的授权窗口点「是」）…", LogLevel.Warn);
            var installCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var result = await new DriverInstaller(m => LogInternal(m, LogLevel.Info)).InstallAsync(installCts.Token);
            LogInternal(result.Message, result.DriverReady ? LogLevel.Success : LogLevel.Error);
            if (result.Kind == DriverSetupResultKind.RebootRequired)
                throw new InvalidOperationException("ViGEmBus 驱动安装完成，需要重启电脑后再开始挂机。");
            if (!result.DriverReady)
                throw new InvalidOperationException("ViGEmBus 驱动未就绪：" + result.Message);
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
            // 真实手柄占用玩家 1 时：按设置自动禁用真实手柄（设备管理器级）释放玩家 1，
            // 无需用户拔下手柄或重启游戏。不走 HidHide 黑名单——它只过滤 HID 层打开，
            // XInput（XUSB IOCTL）不吃这套（2026-10-12 实机：黑名单+Active=1 仍占 slot0），
            // 设备节点禁用对 XInput/DInput 全部生效。全局生效：挂机期间真实手柄不可见，
            // 关闭本程序时由 GUI 触发恢复任务自动还原。
            // 本进程已持有虚拟手柄（Stop→Start 热重启）时 slot0 是我们自己的，直接跳过。
            if (!Pad360.IsConnected && Input.VirtualGamepad360.IsSlot0Occupied()
                && _cfg.HidHide.HidePhysicalGamepad)
            {
                LogInternal("玩家 1 被真实手柄占用：自动禁用真实手柄以释放玩家 1（退出程序后自动恢复）…", LogLevel.Warn);
                Input.HidHideManager.RequestPadHide();
                var deadline = DateTime.Now.AddSeconds(20);
                while (Input.VirtualGamepad360.IsSlot0Occupied() && DateTime.Now < deadline)
                    await Task.Delay(500);
                if (Input.VirtualGamepad360.IsSlot0Occupied())
                    LogInternal("自动禁用未生效（提权计划任务未注册或执行失败）", LogLevel.Warn);
                else
                    LogInternal("玩家 1 已释放（真实手柄已禁用，关闭本程序后自动恢复）", LogLevel.Success);
            }
            Pad360.Connect();
            _ = Task.Run(EnsureHidHideIsolation); // 静默检查虚拟手柄黑名单覆盖（不阻塞启动）
            LogInternal($"虚拟手柄已连接（XInput 玩家 {Pad360.UserIndex + 1}）", LogLevel.Success);
        }
        else
        {
            LogInternal("感知观察模式：不接管手柄，真实手柄可直接使用", LogLevel.Info);
        }

        Stats.StartedAt = DateTime.Now;
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    /// <summary>
    /// 虚拟手柄实例可能跨会话变化（黑名单按完整实例路径匹配）。
    /// 连接后轮询枚举：若实例未被黑名单覆盖且已启用隔离，静默触发最高权限计划任务同步。
    /// 读黑名单（GET）普通权限可用；sync 子进程自行提权执行 SET，全程无 UAC 弹窗。
    /// </summary>
    private async Task EnsureHidHideIsolation()
    {
        try
        {
            var hh = _cfg.HidHide;
            if (!hh.Enabled || !Input.HidHideManager.IsDriverInstalled()) return;

            // ViGEm 设备经系统注册有延迟，最多等 3 秒
            List<string> vigem = new();
            for (int i = 0; i < 10; i++)
            {
                vigem = Input.HidDeviceEnumerator.FindViGEmInstances();
                if (vigem.Count > 0) break;
                await Task.Delay(300);
            }
            if (vigem.Count == 0) return;

            var bl = Input.HidHideIoctl.GetMultiString(Input.HidHideIoctl.IoctlGetBlacklist);
            if (vigem.All(v => bl.Contains(v, StringComparer.OrdinalIgnoreCase)))
                return; // 已覆盖

            LogInternal($"检测到虚拟手柄实例未加入 HidHide 黑名单（{string.Join(";", vigem)}），触发静默同步", LogLevel.Info);
            Input.HidHideManager.RunSyncTaskSilently();
            await Task.Delay(2500);

            // 隔离生效验证（最多再等 6 秒）：Active + 虚拟手柄已隐藏 + 白名单含游戏
            for (int i = 0; i < 12; i++)
            {
                var st = Input.HidHideManager.QueryStatus();
                if (st.Active && st.VigemHidden)
                {
                    var gamePath = string.IsNullOrEmpty(hh.GameImagePath)
                        ? Input.HidHideManager.ResolveGameImagePath()
                        : hh.GameImagePath;
                    if (gamePath is not null)
                    {
                        var wl = Input.HidHideIoctl.GetMultiString(Input.HidHideIoctl.IoctlGetWhitelist);
                        if (wl.Contains(gamePath, StringComparer.OrdinalIgnoreCase))
                        {
                            var gameName = gamePath[(gamePath.LastIndexOfAny(PathSeparatorChars) + 1)..];
                            LogInternal($"手柄隔离生效：仅「{gameName}」可见虚拟手柄", LogLevel.Success);
                        }
                        else
                        {
                            LogInternal("手柄隔离白名单缺少游戏路径——其他应用仍可能收到手柄输入，" +
                                "下次同步将自动补全（需游戏运行中）", LogLevel.Warn);
                            Input.HidHideManager.RunSyncTaskSilently();
                        }
                    }
                    return;
                }
                await Task.Delay(500);
            }
            LogInternal("手柄隔离未生效（HidHide 未激活或虚拟手柄未隐藏）——手柄输入可能泄漏到其他前台应用。" +
                "请在设置中把手柄隔离关一次再开（会弹出一次管理员授权）。", LogLevel.Warn);
        }
        catch (Exception ex)
        {
            LogInternal($"HidHide 黑名单检查跳过：{ex.Message}", LogLevel.Warn);
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
                _dxgi ??= new DxgiDesktopCapture();
                if (!_dxgiStepWired)
                {
                    _dxgi.StepLog += s => LogInternal($"[捕获] {s}", LogLevel.Info);
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
        try { Pad360.ResetAll(); } catch { }
        // 先还原后台停靠的窗口位置，再断捕获（还原依赖窗口仍在捕获会话里无影响，但先还原能让用户立刻看到游戏）
        try { _parker?.Stop(); } catch { }
        // 显式停止捕获会话：挂机停止后不再后台抓帧。
        // 长时间运行的 WGC 捕获会使 dwm.exe 内存累积膨胀，空闲期必须断开。
        try { Capture.Stop(); } catch { }
        try { _dxgi?.Stop(); } catch { }
        try { _gdi?.Stop(); } catch { }
        _lastObs = null;
        // 注意：此处不断开虚拟手柄。手柄一旦被游戏看到「断开→重连」，
        // eFootball 会把光标停靠到顶部头部的「手柄归属」图标上，重连设备的
        // Left/A 全部失效（焦点不在磁贴行）。设备在 Stop→Start 间保持存活，
        // XInput 槽位在应用退出（Dispose）时释放；如需立刻让位给真实手柄请关闭本程序。
    }

    /// <summary>异常停止（场景报错终止）：自动留档当前画面后正常停止。
    /// 用户反馈排障从「读日志猜画面」变成直接看帧。</summary>
    public void StopWithError(string reason)
    {
        SaveFailureDump(reason);
        Stop();
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
            var words = ocr.MergedWords.Take(25).Select(w => w.Text);
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
