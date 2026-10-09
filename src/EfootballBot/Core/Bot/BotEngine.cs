using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Inference;
using EfootballBot.Core.Input;
using EfootballBot.Core.Observation;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>挂机统计：业务计数由场景插件更新，引擎维护感知摘要。</summary>
public sealed class BotStats
{
    public int MatchesDone;
    public int KeyMatches;
    public int Forfeited;
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

    private readonly AppConfig _cfg;
    public AppConfig Config => _cfg;
    public readonly VirtualGamepad360 Pad360;
    public readonly Pad Pad;
    public readonly WindowCapture Capture = new();
    private DxgiDesktopCapture? _dxgi;
    public readonly OcrRecognizer Ocr;
    public readonly ObservationService Observation;
    private readonly OnnxSessionFactory _factory;
    private PerceptionPipeline? _perception;

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
        Ocr = new OcrRecognizer(cfg.OcrEngine, cfg.OcrLanguage);
        LogInternal($"OCR 引擎：{Ocr.LanguageTag}（{Ocr.EngineKind}）", LogLevel.Info);

        Observation = new ObservationService(Capture, Ocr, cfg);
        Observation.Log = LogInternal;

        Capture.CaptureClosed += (_, msg) => LogInternal($"画面捕获已结束：{msg}", LogLevel.Warn);
    }

    public IReadOnlyList<GameWindowInfo> DetectWindows() => GameWindow.FindAll();

    // ---------------- 生命周期 ----------------

    public async Task StartAsync(string mode)
    {
        if (IsRunning) return;
        var token = await PrepareSessionAsync(mode == "cndaily" ? CnProcessName : null);

        // 感知管线（YOLO 模型缺失时仅 OCR，不阻塞启动）
        _perception ??= PerceptionPipeline.Create(_cfg, _factory, LogInternal);
        LogInternal(
            $"引擎就绪（模式：{mode}）—— EP={_factory.ActiveProvider}，" +
            (_perception.YoloEnabled ? $"YOLO 已启用（{_perception.YoloLabel}）" : "YOLO 未启用（仅 OCR 观察）"),
            LogLevel.Success);
        if (!_perception.YoloEnabled)
            LogInternal("场景将在 YOLO 模型（models/yolo/yolo26n.onnx）就位后重建，当前为感知观察模式", LogLevel.Warn);

        _loop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 暂停：不再观察/操作，只轮询等待恢复或取消
                    if (_paused) { await Task.Delay(400, token); continue; }

                    await ObserveAsync(token);

                    int delayMs = 300;
                    if (Scenario is not null)
                        delayMs = await Scenario.RunOnceAsync(token);
                    await Task.Delay(delayMs, token);
                }
            }
            catch (OperationCanceledException)
            {
                LogInternal("已停止。", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Stats.LastError = ex.Message;
                LogInternal($"运行异常：{ex}", LogLevel.Error);
            }
            finally
            {
                StatsChanged?.Invoke(Stats);
            }
        }, token);

        EmitStats();
    }

    /// <summary>会话初始化：驱动检测 → 找窗口 → 启动捕获 → 接手柄 → 建取消令牌。</summary>
    private async Task<CancellationToken> PrepareSessionAsync(string? processName = null)
    {
        if (!VirtualGamepad360.IsDriverInstalled())
            throw new InvalidOperationException("未检测到 ViGEmBus 驱动，请先在“环境检测”中安装。");

        processName ??= GameWindow.GlobalProcessName;
        _window = GameWindow.FindFirst(processName)
            ?? throw new InvalidOperationException(processName.Contains("Online")
                ? "未找到正在运行的国服游戏窗口（eFootballOnline），请先启动「实况足球在线」。"
                : "未找到正在运行的 eFootball 游戏窗口，请先启动游戏。");

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

        Pad360.Connect();
        _ = Task.Run(EnsureHidHideIsolation); // 静默检查虚拟手柄黑名单覆盖（不阻塞启动）
        LogInternal($"虚拟手柄已连接（XInput 玩家 {Pad360.UserIndex + 1}）", LogLevel.Success);

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
            if (vigem.All(v => bl.Contains(v, StringComparer.OrdinalIgnoreCase))) return; // 已覆盖

            LogInternal($"检测到虚拟手柄实例未加入 HidHide 黑名单（{string.Join(";", vigem)}），触发静默同步", LogLevel.Info);
            Input.HidHideManager.RunSyncTaskSilently();
        }
        catch (Exception ex)
        {
            LogInternal($"HidHide 黑名单检查跳过：{ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 按配置启动捕获后端：DXGI 桌面复制（高性能/全分辨率）或 WGC（窗口专属/可被遮挡，默认）。
    /// DXGI 启动异常或 5s 内无桌面帧时自动回退 WGC，保证挂机可用。
    /// </summary>
    private async Task StartCaptureAsync(int w, int h)
    {
        if (_cfg.CaptureMode == CaptureMode.Dxgi)
        {
            try
            {
                _dxgi ??= new DxgiDesktopCapture();
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

                if (Observation.Provider != _dxgi)
                {
                    try { Capture.Stop(); } catch { }
                    Observation.AttachProvider(_dxgi);
                }
                LogInternal("DXGI 桌面复制捕获已就绪", LogLevel.Success);
                return;
            }
            catch (Exception ex)
            {
                LogInternal($"DXGI 捕获启动失败，自动回退 WGC：{ex.Message}", LogLevel.Warn);
                try { _dxgi?.Stop(); } catch { }
            }
        }

        if (!Capture.IsRunning)
        {
            LogInternal($"开始捕获游戏窗口（WGC 窗口捕获，{w}×{h}，后台可捕获/可被遮挡）", LogLevel.Info);
            await Task.Run(() => Capture.Start(_window!.Handle, w, h));
        }
        if (Observation.Provider != Capture)
            Observation.AttachProvider(Capture);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { Pad360.ResetAll(); } catch { }
        _lastObs = null;
        // 注意：此处不断开虚拟手柄。手柄一旦被游戏看到「断开→重连」，
        // eFootball 会把光标停靠到顶部头部的「手柄归属」图标上，重连设备的
        // Left/A 全部失效（焦点不在磁贴行）。设备在 Stop→Start 间保持存活，
        // XInput 槽位在应用退出（Dispose）时释放；如需立刻让位给真实手柄请关闭本程序。
    }

    // ---------------- 感知观察 ----------------

    /// <summary>
    /// 获取一帧新鲜画面 → OCR（节流）→ YOLO 检测（仅新帧）→ 组装 Observation 并派发事件。
    /// 捕获自愈 + OCR 节流由 ObservationService 负责。
    /// </summary>
    public async Task<Observation> ObserveAsync(CancellationToken ct)
    {
        var (f, ocr) = await Observation.ObserveAsync(ct, _cfg.Timing.OcrIntervalMs);

        // 距上次 OCR 不足间隔时返回缓存帧，直接复用上一次感知结果
        if (_lastObs is not null && _lastObs.Frame == f && _lastObs.Ocr == ocr)
            return _lastObs;

        var dets = await _perception!.DetectAsync(f, ct);
        var obs = new Observation(f, ocr, dets);
        _lastObs = obs;

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

        // 周期性输出 OCR 原文：YOLO 启用但零检测视为"未识别"（Warn 便于标注）；仅 OCR 模式用 Debug
        long now = Environment.TickCount64;
        if (now - _lastOcrDumpTick > 5000 && (!yoloOn || dets.Count == 0))
        {
            _lastOcrDumpTick = now;
            var words = ocr.MergedWords.Take(25).Select(w => w.Text);
            LogInternal($"[OCR原文] {string.Join(" | ", words)}", yoloOn ? LogLevel.Warn : LogLevel.Debug);
        }

        Observed?.Invoke(obs);
        return obs;
    }

    // ---------------- 杂项 ----------------

    public void LogInternal(string msg, LogLevel lv)
    {
        Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}", lv);
        EmitStats();
    }

    private void EmitStats() => StatsChanged?.Invoke(Stats);

    public void Dispose()
    {
        Stop();
        try { _perception?.Dispose(); } catch { }
        try { Observation.Dispose(); } catch { }
        try { Ocr.Dispose(); } catch { }
        try { _dxgi?.Dispose(); } catch { }
        try { Pad360.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
