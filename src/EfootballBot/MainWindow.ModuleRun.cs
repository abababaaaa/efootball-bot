using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Bot.Modules;
using EfootballBot.Core.Capture;
using EfootballBot.Core.System;

// ===========================================================================
// 架构锁 · 界面接线 MainWindow.ModuleRun.cs（v7.2）
// 模块 id 与接力候选都来自 BotModuleCatalog：这里只把按钮与 id 绑起来。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许写模块 id 字面量（用 ModuleIds），接力下拉候选不许手写（用 HandoffCandidates）
// 除非用户明确要求更换写法，否则按本约定写。
// ===========================================================================

namespace EfootballBot;

/// <summary>
/// 各模块统一的「开始 / 暂停 / 发现问题手动停止」三按钮状态与手动停止反馈流程。
/// 所有模块共用同一个 Worker（同一时刻只跑一个场景），因此只有当前运行的模块
/// 的暂停与手动停止按钮可用，其余模块的开始按钮在运行期间禁用，避免误触切换。
/// </summary>
public partial class MainWindow
{
    private sealed record ModuleRun(Button Start, Button Pause, Button Manual, string Mode, Button? Step);

    private ModuleRun[] _moduleRuns = Array.Empty<ModuleRun>();

    private bool _manualStopBusy;

    private void InitModuleRuns()
    {
        _moduleRuns = new[]
        {
            // 模块 id 全部来自 BotModuleCatalog（ModuleIds）：加模块只改注册表与这里一行按钮接线
            new ModuleRun(BtnStartMyLeague, BtnPauseMyLeague, BtnManualMyLeague, ModuleIds.IntlMyLeague, BtnStep),
            // 满级换人测试（独立会话）：无挂机时可直接开始，跑完自动停；暂停/手动停止共用我的联赛按钮
            new ModuleRun(BtnTestSwap, BtnPauseMyLeague, BtnManualMyLeague, ModuleIds.IntlSwapTest, null),
            new ModuleRun(BtnStartWeekly, BtnPauseWeekly, BtnManualWeekly, ModuleIds.IntlWeekly, null),
            new ModuleRun(BtnStartAiLadder, BtnPauseAiLadder, BtnManualAiLadder, ModuleIds.IntlAiLadder, null),
            // 种马模块共用一枚开始按钮：具体模式由当前子页签在 BtnStartStud_Click 里决定
            new ModuleRun(BtnStartStud, BtnPauseStud, BtnManualStud, ModuleIds.IntlStudBuy, null),
            new ModuleRun(BtnStartCn, BtnPauseCn, BtnManualCn, ModuleIds.CnLadder, null),
            new ModuleRun(BtnStartCnWeekly, BtnPauseCnWeekly, BtnManualCnWeekly, ModuleIds.CnWeekly, null),
            new ModuleRun(BtnStartCnMyLeague, BtnPauseCnMyLeague, BtnManualCnMyLeague, ModuleIds.CnMyLeague, null),
        };
    }

    /// <summary>刷新三按钮状态：开始↔一键结束、暂停启用/置暗、手动停止启用。</summary>
    private void RefreshRunButtons()
    {
        // ★看场景而不是看进程：Worker 在场景结束/启动失败后会保活等重连，
        // 用进程存活判断会让「开始」永久禁用（用户报障：非正常退出后点不动）。
        bool running = _workerHost is { ScenarioRunning: true };
        bool orphanRunning = running && _activeStartBtn is null; // 状态不一致的自愈出口
        // 热键提示：运行中直接标在按钮上（一键结束=再按一次即终止；暂停/继续=第一次按即暂停）
        string hk = HotkeyLabel is { } h ? $"（{h}）" : "";
        foreach (var m in _moduleRuns)
        {
            bool active = running && _activeStartBtn == m.Start;
            m.Start.Content = active ? $"一键结束{hk}" : "开始";
            m.Start.Style = (Style)FindResource(active ? "DangerBtn" : "PrimaryBtn");
            m.Start.IsEnabled = (!_starting && (!running || orphanRunning)) || active;
            // 暂停/手动停止作用于当前唯一运行的引擎：开始后所有模块页上都可用
            m.Pause.IsEnabled = running;
            m.Pause.Content = (running && _paused ? "继续" : "暂停") + hk;
            m.Manual.IsEnabled = running;
            if (m.Step is not null)
                m.Step.IsEnabled = active && _paused;
        }
        // 换人测试按钮：未激活时显示专用文案（ModuleRun 循环会把它改成「开始」）
        bool swapActive = running && _activeStartBtn == BtnTestSwap;
        BtnTestSwap.Content = swapActive ? $"一键结束{hk}" : "一键测试换人";
        UpdateTopStatusPill();
    }

    /// <summary>同步顶栏实时状态胶囊（就绪 / 启动中 / 运行中 / 已暂停 + 当前模块与耗时）。</summary>
    internal void UpdateTopStatusPill()
    {
        if (DotTopStatus is null || TxtTopStatus is null) return;
        bool running = _workerHost is { ScenarioRunning: true };
        if (_starting)
        {
            DotTopStatus.Fill = BrushWarn;
            TxtTopStatus.Text = "正在启动…";
            return;
        }
        if (!running)
        {
            DotTopStatus.Fill = BrushIdle;
            TxtTopStatus.Text = "就绪";
            return;
        }

        string modeName = BotModuleCatalog.Find(_activeMode) is { } desc
            ? (desc.Server == ServerRegion.Cn ? $"国服 · {desc.DisplayName}" : desc.DisplayName)
            : "挂机";
        var s = _workerHost?.LastStats;
        string elapsed = s?.StartedAt is { } st ? $" · {(DateTime.Now - st):hh\\:mm\\:ss}" : "";
        if (_paused)
        {
            DotTopStatus.Fill = BrushWarn;
            TxtTopStatus.Text = $"已暂停 · {modeName}";
        }
        else
        {
            DotTopStatus.Fill = BrushOk;
            TxtTopStatus.Text = $"运行中 · {modeName}{elapsed}";
        }
    }

    private async void BtnManualStop_Click(object sender, RoutedEventArgs e)
        => await ManualStopWithReportAsync();
    /// <summary>
    /// 发现问题手动停止：≈一键结束，但先自动截取当前游戏画面，走诊断报告渠道生成反馈包，
    /// 并提示用户把包发给作者、可一键打开目录。
    /// 顺序：截图（趁游戏还在运行）→ 停止 → 可选补充说明弹窗 → 生成反馈包 → 提示+跳转目录。
    /// </summary>
    private async Task ManualStopWithReportAsync()
    {
        if (_manualStopBusy) return;
        if (_workerHost is not { ScenarioRunning: true })
        {
            AppendLog("【手动停止】当前没有运行中的挂机。", LogLevel.Info);
            return;
        }
        _manualStopBusy = true;
        foreach (var m in _moduleRuns) m.Manual.IsEnabled = false;
        try
        {
            AppendLog("【手动停止】正在截取当前游戏画面…", LogLevel.Info);
            var (frame, info) = await Task.Run(() => GameFrameGrabber.GrabOnce(TimeSpan.FromSeconds(3)));
            string shotPath = "";
            if (frame is not null)
            {
                try
                {
                    string dir = Path.Combine(AppPaths.DataDir, "ManualStopShots");
                    Directory.CreateDirectory(dir);
                    shotPath = Path.Combine(dir, $"game_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    SaveFramePng(frame, shotPath);
                    AppendLog($"【手动停止】已截图：{shotPath}（{info}）", LogLevel.Success);
                }
                catch (Exception ex)
                {
                    shotPath = "";
                    AppendLog($"【手动停止】截图保存失败：{ex.Message}", LogLevel.Warn);
                }
            }
            else
            {
                AppendLog($"【手动停止】未取到游戏画面：{info}（反馈包仍会生成）", LogLevel.Warn);
            }
            await StopScenarioAsync(allowAutoClose: false);
            AppendLog("【手动停止】挂机已停止。", LogLevel.Info);
            string? note = NotePromptWindow.Ask(this,
                "已为你截取当前游戏画面。可选：描述遇到的问题（卡在哪一步、点了什么没反应、什么时候开始），" +
                "会写进反馈包，帮助作者快速定位。");
            if (note is null)
            {
                AppendLog("【手动停止】已取消生成反馈包（挂机已停止，截图保留在 data\\ManualStopShots）。", LogLevel.Info);
                return;
            }
            AppendLog("【手动停止】正在生成反馈包（约 1~2 分钟，请勿关闭程序）…", LogLevel.Info);
            string result = await Task.Run(() => DiagReporter.CollectAsync(userNote: note, screenshotPath: shotPath));
            AppendLog($"【手动停止】反馈包已生成：{result}", LogLevel.Success);
            var ans = MessageBox.Show(this,
                "反馈包已生成完毕。\n\n请把下面这个文件直接发给作者（或发到反馈群），即可帮你定位问题：\n\n" +
                result + "\n\n是否现在打开反馈包所在目录？",
                "反馈包已生成", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (ans == MessageBoxResult.Yes)
                OpenDiagResult(result);
        }
        catch (Exception ex)
        {
            AppendLog($"【手动停止】生成反馈包失败：{ex}", LogLevel.Error);
            MessageBox.Show(this,
                "生成反馈包失败：" + ex.Message + "\n\n截图与日志仍保留在程序 data 目录。",
                "反馈包生成失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _manualStopBusy = false;
            RefreshRunButtons();
        }
    }

    /// <summary>把捕获帧编码为 PNG（与 App.SavePng 一致）。</summary>
    private static void SaveFramePng(FrameData f, string path)
    {
        var wb = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
        using var fs = File.OpenWrite(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(wb));
        enc.Save(fs);
    }
}





