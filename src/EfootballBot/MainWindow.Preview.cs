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
        if (!_previewOn || _workerHost is not { ScenarioRunning: true }) return;
        if ((DateTime.Now - _lastPreviewAt).TotalSeconds > 4 &&
            (DateTime.Now - _lastPreviewResend).TotalSeconds > 4)
        {
            _lastPreviewResend = DateTime.Now;
            _ = _workerHost.SendPreviewAsync(true);
        }
    }
}
