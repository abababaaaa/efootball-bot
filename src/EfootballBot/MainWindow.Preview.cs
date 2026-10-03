using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.System;
using EfootballBot.Core.Logging;
using EfootballBot.Core.UI;
using EfootballBot.Core.Vision;

namespace EfootballBot;

public partial class MainWindow
{
    // ---------------- 预览 ----------------

    private void BtnPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewOn)
        {
            try
            {
                var engine = GetOrCreateEngine();
                if (!engine.Capture.IsRunning)
                {
                    var win = GameWindow.FindFirst(ActiveProcessName);
                    if (win is null)
                    {
                        AppendLog("未找到游戏窗口，无法预览。请先启动游戏。", LogLevel.Warn);
                        return;
                    }
                    var (w, h) = GameWindow.GetClientSize(win.Handle);
                    if (w <= 0 || h <= 0) (w, h) = (1280, 720);
                    engine.Capture.Start(win.Handle, w, h);
                    AppendLog($"预览捕获已启动（{w}×{h}）。", LogLevel.Success);
                }
                _previewTimer.Start();
                _previewOn = true;
                BtnPreview.Content = "关闭预览";
            }
            catch (Exception ex)
            {
                AppendLog($"预览启动失败：{ex.Message}", LogLevel.Error);
            }
        }
        else
        {
            _previewTimer.Stop();
            _previewOn = false;
            // 清空画面，否则最后一帧会一直留在控件里，看起来像"关不掉"
            Preview.Source = null;
            _wb = null;
            // 引擎未运行时，捕获是仅为预览启动的，关闭预览时一并释放
            if (_engine is { IsRunning: false } && _engine.Capture.IsRunning)
            {
                _engine.Capture.Stop();
                AppendLog("预览捕获已停止。", LogLevel.Info);
            }
            BtnPreview.Content = "开启预览";
        }
    }

    private void UpdatePreviewFrame()
    {
        var f = _engine?.Capture.TryGetLatest();
        if (f is null) return;
        // 预览降采样到固定 480×270：显示区域就这么大，全分辨率写入纯属浪费（每 tick 省 ~3.4MB 拷贝与渲染）
        const int pw = 480, ph = 270;
        if (_wb is null || _wb.PixelWidth != pw || _wb.PixelHeight != ph)
        {
            _wb = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
            Preview.Source = _wb;
        }
        _previewBuf ??= new byte[pw * ph * 4];
        var src = f.Bgra;
        int sw = f.Width, sh = f.Height;
        for (int y = 0; y < ph; y++)
        {
            int srow = (y * sh / ph) * sw * 4;
            int drow = y * pw * 4;
            for (int x = 0; x < pw; x++)
            {
                int si = srow + (x * sw / pw) * 4;
                int di = drow + x * 4;
                _previewBuf[di] = src[si];
                _previewBuf[di + 1] = src[si + 1];
                _previewBuf[di + 2] = src[si + 2];
                _previewBuf[di + 3] = src[si + 3];
            }
        }
        _wb.WritePixels(new Int32Rect(0, 0, pw, ph), _previewBuf, pw * 4, 0);
    }
}
