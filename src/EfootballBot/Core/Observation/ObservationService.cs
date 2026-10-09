using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Observation;

/// <summary>
/// 观察服务：封装画面捕获 + OCR 识别 + 捕获节流，不含画面分类。
/// 持有 ICaptureProvider（DXGI Desktop Duplication 或 WGC），支持运行时切换捕获后端。
/// 分类逻辑（ProbeEngine.Classify）仍由 BotEngine 负责。
/// </summary>
public sealed class ObservationService : IDisposable
{
    private readonly object _gate = new();
    private ICaptureProvider _provider;
    private readonly OcrRecognizer _ocr;
    private readonly AppConfig _cfg;

    private FrameData? _frame;
    private FrameData? _lastOcrFrame;
    private OcrResult _lastOcr = OcrResult.Empty;
    private long _lastOcrTick;
    private readonly List<DateTime> _rebuildTimes = new();

    /// <summary>帧陈旧阈值：节流 2000ms/帧，10s 无新帧判定捕获中断。</summary>
    private const int StaleFrameMs = 10_000;
    private DateTime _lastCaptureRestart = DateTime.MinValue;

    /// <summary>当前游戏窗口（捕获自愈重建时使用）。</summary>
    public GameWindowInfo? Window { get; set; }

    /// <summary>日志回调（由 BotEngine 桥接）。</summary>
    public Action<string, LogLevel>? Log { get; set; }

    public event EventHandler? FrameUpdated;

    public FrameData? LatestFrame => _frame;
    public OcrResult LatestOcr => _lastOcr;
    public long LastOcrTick => _lastOcrTick;
    public ICaptureProvider Provider => _provider;

    public ObservationService(ICaptureProvider provider, OcrRecognizer ocr, AppConfig cfg)
    {
        _provider = provider;
        _ocr = ocr;
        _cfg = cfg;
        _provider.FrameUpdated += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var f = _provider.TryGetLatest();
        if (f is not null)
        {
            _frame = f;
            FrameUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>切换捕获提供者（DXGI / WGC）。会停止旧提供者并开始新提供者。</summary>
    public void SwitchCapture(ICaptureProvider newProvider)
    {
        lock (_gate)
        {
            _provider.FrameUpdated -= OnFrame;
            try { _provider.Stop(); } catch { }
            try { _provider.Dispose(); } catch { }
            _provider = newProvider;
            _provider.FrameUpdated += OnFrame;
        }
    }

    /// <summary>
    /// 仅切换帧事件订阅到另一个已管理的提供者，不停止/释放任何一方。
    /// 用于 DXGI↔常驻 WGC 实例之间的安全切换（生命周期由 BotEngine 管理）。
    /// </summary>
    public void AttachProvider(ICaptureProvider provider)
    {
        lock (_gate)
        {
            _provider.FrameUpdated -= OnFrame;
            _provider = provider;
            _provider.FrameUpdated += OnFrame;
            _frame = null;
        }
    }

    // ---------------- 捕获自愈 ----------------

    /// <summary>
    /// 等待一帧新鲜画面。捕获中断（全屏切换/最小化/D3D 设备丢失）时
    /// 自动整套重建捕获。只有游戏窗口真正消失才抛异常终止场景。
    /// </summary>
    public async Task<FrameData> WaitForFreshFrameAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var f = _frame;
            if (f is not null && (DateTime.Now - _provider.LastFrameAt).TotalMilliseconds < StaleFrameMs)
                return f;

            // 宽限 2s：启动首帧 / 切屏过渡 / 重建后第一帧
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(100, ct);
                f = _frame;
                if (f is not null && (DateTime.Now - _provider.LastFrameAt).TotalMilliseconds < StaleFrameMs)
                    return f;
            }

            // 窗口仍在（独占全屏/最小化也能查到）→ 重建捕获；窗口没了才是真终止
            if (Window is null || GameWindow.FindFirst(Window.ProcessName) is null)
                throw new InvalidOperationException("游戏窗口已关闭，停止挂机。");

            await RestartCaptureAsync(ct);
        }
    }

    /// <summary>整套停止并重建捕获（同窗口、按当前客户区尺寸）。两次重建至少间隔 10s。
    /// 3 分钟内重建 ≥3 次 → 熔断抛出 CaptureUnrecoverableException：
    /// 捕获链路系统性异常（典型：WGC 会话反复触发 DWM 崩溃黑屏），继续重建只会拖垮整个系统。</summary>
    private async Task RestartCaptureAsync(CancellationToken ct)
    {
        if ((DateTime.Now - _lastCaptureRestart).TotalSeconds < 10)
        {
            await Task.Delay(1000, ct);
            return;
        }
        _lastCaptureRestart = DateTime.Now;

        lock (_rebuildTimes)
        {
            _rebuildTimes.RemoveAll(t => (DateTime.Now - t).TotalMinutes >= 3);
            _rebuildTimes.Add(DateTime.Now);
            if (_rebuildTimes.Count >= 3)
            {
                _rebuildTimes.Clear();
                throw new CaptureUnrecoverableException(
                    "画面捕获 3 分钟内反复中断 3 次，已熔断停止，避免持续崩溃拖垮系统（黑屏/卡死）。" +
                    "常见原因：DWM 或显卡驱动异常（事件查看器可查 dwm.exe 崩溃记录）。" +
                    "建议：重启电脑 / 用 DDU 干净重装显卡驱动；或到设置中切换 DXGI 捕获模式再试。");
            }
        }

        LogInternal("画面捕获中断（10s 无新帧，可能在最小化/全屏切换/设备丢失），重建捕获会话…", LogLevel.Warn);

        _frame = null;
        _lastOcrFrame = null;
        try { _provider.Stop(); } catch { } // 会话已失效时 Stop 可能抛异常，兜住走重建
        await Task.Delay(500, ct);

        var (w, h) = GameWindow.GetClientSize(Window!.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        try
        {
            await Task.Run(() => _provider.Start(Window.Handle, w, h), ct);
            LogInternal($"捕获会话已重建（{w}×{h}），等待画面恢复…", LogLevel.Info);
        }
        catch (Exception ex)
        {
            LogInternal($"捕获重建失败：{ex.Message}；10s 后重试", LogLevel.Warn);
        }
    }

    // ---------------- OCR ----------------

    /// <summary>
    /// 获取一帧新鲜画面并执行 OCR。若距上次 OCR 不足 ocrIntervalMs 且有缓存，则返回缓存结果。
    /// 不含画面分类——由 BotEngine 调用 ProbeEngine 完成。
    /// </summary>
    public async Task<(FrameData Frame, OcrResult Ocr)> ObserveAsync(CancellationToken ct, int ocrIntervalMs)
    {
        var f = await WaitForFreshFrameAsync(ct);

        long now = Environment.TickCount64;

        if (now - _lastOcrTick < ocrIntervalMs && _lastOcrFrame is not null)
            return (_lastOcrFrame, _lastOcr);

        await Task.Yield();
        var ocr = await _ocr.RecognizeAsync(f, ct);
        _lastOcrFrame = f;
        _lastOcr = ocr;
        _lastOcrTick = now;
        return (f, ocr);
    }

    // ---------------- 捕获节流 ----------------

    /// <summary>
    /// 按场景状态调整捕获帧间隔（背压节流，降低全屏 DWM 负载）。
    /// 传 0 恢复全速（33ms 跳帧）。
    /// </summary>
    public void ApplyCaptureThrottle(int minFrameIntervalMs)
        => _provider.SetMinFrameInterval(minFrameIntervalMs <= 0 ? 33 : minFrameIntervalMs);

    private void LogInternal(string msg, LogLevel lv) => Log?.Invoke(msg, lv);

    public void Dispose()
    {
        _provider.FrameUpdated -= OnFrame;
        try { _provider.Dispose(); } catch { }
        GC.SuppressFinalize(this);
    }
}
