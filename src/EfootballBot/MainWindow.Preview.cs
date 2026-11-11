using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Logging;
using EfootballBot.Core.System;
using EfootballBot.Core.UI;
using EfootballBot.Core.Vision;

namespace EfootballBot;

public partial class MainWindow
{
    // ---------------- 预览 ----------------

    private async void BtnPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewOn)
        {
            // 首选 DWM 零拷贝缩略图（任务栏悬停预览同一机制）：不经过 Worker 抓帧/PNG/管道，
            // 帧率等于显示器刷新率、CPU≈0；注册失败（或 DWM 合成关闭）再退回原来的帧流预览。
            if (TryStartDwmPreview())
            {
                _previewOn = true;
                BtnPreview.Content = "关闭预览";
                _previewTimer.Start();
                return;
            }
            var host = GetOrCreateWorkerHost();
            if (!host.ScenarioRunning)
            {
                AppendLog("预览需要先开始挂机（Worker 未运行）。", LogLevel.Warn);
                return;
            }
            try
            {
                await host.SendPreviewAsync(true);
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
            StopDwmPreview();
            try
            {
                if (_workerHost is { ScenarioRunning: true })
                    await _workerHost.SendPreviewAsync(false);
            }
            catch { }
            _previewTimer.Stop();
            _previewOn = false;
            // 清空画面，否则最后一帧会一直留在控件里，看起来像"关不掉"
            Preview.Source = null;
            BtnPreview.Content = "开启预览";
        }
    }

    /// <summary>
    /// 预览帧由 Worker 推送 PNG base64 解码显示（ShowPreviewPng）。
    /// 本定时器兼作看门狗：开关挂机会重建 Worker 进程，新进程的预览推送是停着的——
    /// 若开启状态下超过 4 秒没收到帧，自动重发开启命令，不再需要手动重启预览。
    /// </summary>
    private DateTime _lastPreviewAt = DateTime.MinValue;
    private DateTime _lastPreviewResend = DateTime.MinValue;

    private void UpdatePreviewFrame()
    {
        if (!_previewOn) return;
        if (_dwmPreview is not null) return; // DWM 路径不依赖 Worker 推帧，不参与看门狗重发
        if (_workerHost is not { ScenarioRunning: true }) return;
        if ((DateTime.Now - _lastPreviewAt).TotalSeconds > 4 &&
            (DateTime.Now - _lastPreviewResend).TotalSeconds > 4)
        {
            _lastPreviewResend = DateTime.Now;
            _ = SendPreviewOnQuietlyAsync();
        }
    }

    /// <summary>开始挂机后自动开预览：不阻塞、不报错——此刻 Worker 正忙着引擎启动
    /// （手柄接管/停靠，实测最长 20s+），预览命令暂时发不进去是常态；置位后由帧看门狗
    /// （UpdatePreviewFrame）每 4 秒重发，引擎就绪后第一帧自然到达。旧实现 await 5s 写超时
    /// 后报「Worker 可能已卡死」的假错误（2026-10-24 反馈包实机）。</summary>
    private void AutoOpenPreview()
    {
        if (_previewOn) return;
        // 先试 DWM 零拷贝路径：挂机时游戏窗口通常已停靠，若 DWM 无表面会自动回退帧流
        if (TryStartDwmPreview())
        {
            _previewOn = true;
            BtnPreview.Content = "关闭预览";
            _previewTimer.Start();
            return;
        }
        _previewOn = true;
        _lastPreviewAt = DateTime.MinValue;
        _lastPreviewResend = DateTime.MinValue;
        BtnPreview.Content = "关闭预览";
        _previewTimer.Start();
    }

    /// <summary>看门狗重发预览开启命令：失败静默，留给下一次 4 秒重试。</summary>
    private async Task SendPreviewOnQuietlyAsync()
    {
        if (_workerHost is not { } host) return;
        try { await host.SendPreviewAsync(true); }
        catch { /* 启动期/暂停期发送失败属预期，下一拍重试 */ }
    }

    // ---------------- DWM 零拷贝预览（与任务栏悬停预览同一机制） ----------------

    private DwmLivePreview? _dwmPreview;
    private DispatcherTimer? _dwmLayoutTimer;
    private int _dwmUnhealthyTicks;

    /// <summary>游戏进程名（与 GameFrameGrabber / BackgroundParker 保持一致）。</summary>
    private static readonly string[] DwmGameProcesses =
        { "eFootball", "eFootball2024", "efootballsteam", "eFootballOnline" };

    /// <summary>
    /// 用 DWM 缩略图接管右侧实时画面：DwmRegisterThumbnail(主窗口 ← 游戏窗口)，
    /// 之后每一帧由 DWM 在合成阶段直接搬运，不抓帧、不编码、不走命名管道，CPU≈0。
    /// 找不到游戏窗口或 DWM 拒绝注册时返回 false，调用方回退到原有的 Worker 帧流预览。
    /// </summary>
    private bool TryStartDwmPreview()
    {
        StopDwmPreview();

        nint src = nint.Zero;
        string srcName = "";
        foreach (var name in DwmGameProcesses)
        {
            try
            {
                var win = GameWindow.FindFirst(name);
                if (win is not null) { src = win.Handle; srcName = name; break; }
            }
            catch { /* 进程枚举权限异常：换下一个候选 */ }
        }
        if (src == nint.Zero) return false;

        var dest = new WindowInteropHelper(this).Handle;
        if (!DwmLivePreview.TryAttach(dest, src, out var pv, out var err) || pv is null)
        {
            AppendLog($"实时画面使用帧流预览（DWM 缩略图不可用：{err}）", LogLevel.Info);
            return false;
        }

        _dwmPreview = pv;
        _dwmUnhealthyTicks = 0;
        Preview.Source = null; // 让位：占位提示被缩略图覆盖，不必留旧帧
        UpdateDwmPlacement();

        _dwmLayoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _dwmLayoutTimer.Tick += (_, _) => UpdateDwmPlacement();
        _dwmLayoutTimer.Start();

        AppendLog($"实时画面已开启（DWM 零拷贝缩略图，源窗口 {srcName} #0x{src:X}）", LogLevel.Success);
        return true;
    }

    /// <summary>摆放缩略图矩形 + 可用性自检（源窗口销毁 / DWM 无表面即回退帧流）。</summary>
    private void UpdateDwmPlacement()
    {
        if (_dwmPreview is null) return;

        // 游戏进程退出/重启：旧句柄作废，直接放弃 DWM 路径
        if (!IsDwmSourceAlive(_dwmPreview.SourceHandle))
        {
            StopDwmPreview();
            return;
        }

        // 预览区当前不可见（右栏折叠、切到别的页、窗口最小化、主题遮罩盖住）→ 缩略图必须一起藏。
        // DWM 缩略图是直接合成在窗口表面之上的，不跟随 WPF 的 Visibility，不显式隐藏就会一直
        // 浮在原位置挡住界面——这就是"折叠右栏后画面还在、关不掉"的原因。
        if (!ShouldShowDwmPreview(out var rectPx))
        {
            _dwmPreview.SetVisible(false);
            return;
        }

        // Border 有 10px 圆角，DWM 缩略图是硬边矩形：内缩 2px 让四角落在背景色上，观感不露角
        const int inset = 2;
        if (rectPx.Width > inset * 2 && rectPx.Height > inset * 2)
            rectPx = new Int32Rect(rectPx.X + inset, rectPx.Y + inset,
                rectPx.Width - inset * 2, rectPx.Height - inset * 2);
        _dwmPreview.UpdatePlacement(rectPx); // 该调用自带 Visible=1，折叠恢复后自动显示

        // 自检：DWM 没为源窗口维持合成表面时 DwmQueryThumbnailSourceSize 返回 0 分量。
        // 典型场景=游戏窗口被后台停靠到所有显示器之外（此时 DWM 不再合成它），
        // 连续约 0.6 秒仍为 0 就回退到帧流预览，避免用户盯着一块空白。
        if (_dwmPreview.TryGetSourceSize(out _, out _))
        {
            _dwmUnhealthyTicks = 0;
            return;
        }
        if (++_dwmUnhealthyTicks < 8) return;

        AppendLog("DWM 缩略图不可用（游戏窗口可能已停靠到屏幕外），实时画面回退为帧流预览", LogLevel.Warn);
        StopDwmPreview();
        _ = SendPreviewOnQuietlyAsync(); // 帧流预览由看门狗周期重发，这里先推一次
    }

    /// <summary>
    /// 预览区此刻是否应当显示缩略图，并给出它在主窗口客户区的像素矩形。
    /// 折叠、切页、最小化、被主题遮罩盖住时返回 false（调用方随即 SetVisible(false)）。
    /// </summary>
    private bool ShouldShowDwmPreview(out Int32Rect rectPx)
    {
        rectPx = default;
        if (WindowState == WindowState.Minimized) return false;
        if (!IsVisible) return false;
        if (PreviewFrame is null || !PreviewFrame.IsVisible) return false;
        if (PreviewFrame.ActualWidth < 8 || PreviewFrame.ActualHeight < 8) return false;
        // 主题选择弹窗盖住整窗：缩略图会浮在遮罩之上，必须让位
        if (ThemeOverlay is { IsVisible: true }) return false;
        if (!DwmLivePreview.TryGetElementRectPx(PreviewFrame, this, out rectPx)) return false;
        return rectPx.Width >= 8 && rectPx.Height >= 8;
    }

    /// <summary>源窗口句柄仍有效且客户区还有实际尺寸（停靠缩窗到 70×39 时由 BackgroundParker 负责恢复）。</summary>
    private static bool IsDwmSourceAlive(nint hwnd)
    {
        if (hwnd == nint.Zero || !DwmNative.IsWindow(hwnd)) return false;
        return DwmNative.GetClientRect(hwnd, out var rc) && rc.Right > 8 && rc.Bottom > 8;
    }

    /// <summary>注销缩略图并停掉布局定时器（幂等）。</summary>
    private void StopDwmPreview()
    {
        if (_dwmLayoutTimer is not null)
        {
            _dwmLayoutTimer.Stop();
            _dwmLayoutTimer = null;
        }
        if (_dwmPreview is not null)
        {
            _dwmPreview.Dispose();
            _dwmPreview = null;
        }
        _dwmUnhealthyTicks = 0;
    }
}
