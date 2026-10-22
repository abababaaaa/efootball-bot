using global::System.Diagnostics;

namespace EfootballBot.Core.Capture;

/// <summary>
/// 一次性截取当前游戏窗口画面（用于「发现问题手动停止」反馈包）。
/// 复用 <see cref="GdiWindowCapture"/>：PrintWindow(PW_RENDERFULLCONTENT) 后台可取，
/// 全屏 D3D 黑帧时自动退化为屏幕区域 BitBlt。返回的帧已按捕获链路规则降采样（≤720p），
/// 与 Bot 实际观察到的画面一致，便于对照诊断。
/// </summary>
public static class GameFrameGrabber
{
    // 国际服 / 国服常见进程名（国际服也可能是 eFootball2024 / efootballsteam）
    private static readonly string[] ProcessCandidates =
        { "eFootball", "eFootball2024", "efootballsteam", "eFootballOnline" };

    /// <summary>抓取一帧。成功返回帧与说明；失败返回 null 与原因（不抛异常）。</summary>
    public static (FrameData? Frame, string Info) GrabOnce(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        foreach (var name in ProcessCandidates)
        {
            EfootballBot.Core.System.GameWindowInfo? win;
            try { win = EfootballBot.Core.System.GameWindow.FindFirst(name); }
            catch { continue; }
            if (win is null) continue;

            var cap = new GdiWindowCapture();
            try
            {
                cap.Start(win.Handle, 0, 0);
                while (sw.Elapsed < timeout)
                {
                    var f = cap.TryGetLatest();
                    if (f is not null) return (f, $"{name}（{f.Width}×{f.Height}）");
                    Thread.Sleep(40);
                }
                return (null, $"{name} 截图超时（窗口可能已最小化或渲染为黑屏）");
            }
            catch (Exception ex)
            {
                return (null, $"截图失败：{ex.Message}");
            }
            finally
            {
                cap.Dispose();
            }
        }
        return (null, "未找到游戏窗口（国际服 eFootball / 国服 eFootballOnline）");
    }
}
