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
    // 左栏导航模式：游戏模块 / 设置 / 更新 / 日志历史 / 关于
    private enum NavMode { Game, Settings, Update, LogViewer, About }
    private NavMode _currentNav = NavMode.Game;

    private void RbModule_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // 初始化期不处理

        // 允许运行中切换国服/国际服视图（只看另一服务器的配置面板，不停引擎）
        // 引擎已绑定当前 scenario 的进程，切换 RadioButton 仅影响中栏面板显示
        NavigateTo(NavMode.Game);

        // 运行中不重新检测环境（避免切换视图时误报另一服务器窗口状态）
        if (_workerHost?.IsRunning != true)
            RunEnvChecks();
    }

    // ---------------- 主题选择 ----------------

    /// <summary>显示主题选择覆盖层，current 为当前生效主题（高亮选中）。</summary>
    private void ShowThemeOverlay(string current)
    {
        _pendingTheme = current;
        HighlightThemeCard(current);
        ThemeOverlay.Visibility = Visibility.Visible;
        BtnThemeConfirm.Visibility = Visibility.Visible;
    }

    private void HideThemeOverlay()
    {
        ThemeOverlay.Visibility = Visibility.Collapsed;
    }

    private void HighlightThemeCard(string theme)
    {
        // 重置三张卡片
        CardDark.Background = Brushes.Transparent;
        CardDark.BorderBrush = (Brush)Application.Current.Resources["CardBorder"];
        CardLight.Background = Brushes.Transparent;
        CardLight.BorderBrush = (Brush)Application.Current.Resources["CardBorder"];
        CardDeepBlue.Background = Brushes.Transparent;
        CardDeepBlue.BorderBrush = (Brush)Application.Current.Resources["CardBorder"];

        var selected = theme switch
        {
            ThemeManager.Light => CardLight,
            ThemeManager.DeepBlue => CardDeepBlue,
            _ => CardDark,
        };
        selected.Background = (Brush)Application.Current.Resources["AccentLight"];
        selected.BorderBrush = (Brush)Application.Current.Resources["Accent"];
    }

    private void ThemeCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string tag })
        {
            _pendingTheme = tag;
            HighlightThemeCard(tag);
        }
    }

    private void BtnThemeConfirm_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Theme = _pendingTheme;
        ThemeManager.ApplyTheme(_pendingTheme);
        _cfg.Save();
        HideThemeOverlay();
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ShowThemeOverlay(_cfg.Theme);
    }

    // ---------------- 环境检测 ----------------

    private void RunEnvChecks()
    {
        // 虚拟手柄驱动：HIDMaestro（唯一后端，驱动在开始挂机首次连接时经 SDK 幂等安装）。
        // 绿点 = 驱动已装；虚拟设备节点仅挂机连接期间在线，观察其在线状态补充文案
        bool hidm = HidMaestroPad.IsDriverInstalled();
        DotPad.Fill = hidm ? BrushOk : BrushBad;
        bool padOnline = false;
        try
        {
            padOnline = HidDeviceEnumerator.EnumerateAllInstances()
                .Any(i => HidDeviceEnumerator.IsHidMaestroInstance(i.InstancePath));
        }
        catch { /* 枚举失败不影响面板 */ }
        TxtPad.Text = !hidm ? "HIDMaestro 驱动未安装（开始挂机时自动装）"
            : padOnline ? "虚拟手柄驱动（HIDMaestro，设备在线）"
            : "虚拟手柄驱动（HIDMaestro，挂机时连接）";
        bool padMissing = !hidm;

        // WGC 后台捕获
        bool wgc = WindowCapture.IsSupported();
        DotWgc.Fill = wgc ? BrushOk : BrushBad;

        // OCR 引擎：PP-OCRv6 模型随程序自带、不依赖系统语言包；Windows 引擎才检查中文语言包
        try
        {
            if (_cfg.OcrEngine == OcrEngineKind.Windows)
            {
                var langs = OcrRecognizer.AvailableLanguages();
                bool zh = langs.Any(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                DotOcr.Fill = zh ? BrushOk : BrushBad;
                TxtOcr.Text = zh ? $"OCR 引擎（Windows {string.Join(",", langs.Take(2))}）" : "OCR 缺中文语言包";
            }
            else
            {
                DotOcr.Fill = BrushOk;
                TxtOcr.Text = "OCR 引擎（RapidOCR PP-OCRv6）";
            }
        }
        catch
        {
            DotOcr.Fill = BrushBad;
            TxtOcr.Text = "OCR 不可用";
        }

        // 游戏窗口（跟随左侧选中的模块）
        bool cn = IsCnSelected;
        TxtGame.Text = cn ? "游戏窗口（国服）" : "游戏窗口（国际服）";
        var win = GameWindow.FindFirst(ActiveProcessName);
        DotGame.Fill = win is not null ? BrushOk : BrushIdle;

        if (padMissing)
        {
            AppendLog("HIDMaestro 驱动未安装：开始挂机时会自动下载 SDK 并安装（需管理员授权，首次约 5-15 秒）。", LogLevel.Warn);
        }
        if (win is null)
            AppendLog(cn
                ? "未找到国服游戏窗口（eFootballOnline / 实况足球在线），请先启动游戏（无需置顶）。"
                : "未找到 eFootball 游戏窗口，请先启动游戏（无需置顶）。", LogLevel.Warn);

        // 手柄环境自愈：shim 指向当前 exe + 遗留禁用手柄一次性恢复 + 遗留禁用任务清理
        _ = EnsurePadEnvironmentAsync();
    }

    /// <summary>
    /// 启动自愈（后台，不阻塞 UI）：
    /// ① 重写提权任务 shim 指向当前 exe（程序移动/更新后任务动作恒有效）；
    /// ② 旧版本遗留的禁用手柄 → 进程内一次性恢复（3.2.0 共存模式已移除禁用机制）；
    /// ③ 清理旧版注册的 pad-hide/pad-restore 遗留任务与 shim（登录触发指向已不存在的参数，残留无害但难看）。
    /// </summary>
    private async Task EnsurePadEnvironmentAsync()
    {
        try
        {
            HidHideManager.WriteShimScripts();

            await Task.Run(() =>
            {
                try
                {
                    if (LegacyPadRestore.HasLegacyDisabled())
                    {
                        Dispatcher.BeginInvoke(() =>
                            AppendLog("检测到旧版本遗留的禁用手柄，正在自动恢复…", LogLevel.Info));
                        var (_, msg) = LegacyPadRestore.RestoreAll();
                        Dispatcher.BeginInvoke(() => AppendLog($"遗留手柄恢复：{msg}", LogLevel.Info));
                    }
                }
                catch { /* 未提权等：下次启动重试 */ }
            });

            await Task.Run(() =>
            {
                // 3.2.0 移除的两个提权任务：残留会在每次登录时触发已不存在的模式，静默删除（幂等）
                foreach (var name in new[] { "EfootballBotPadHide", "EfootballBotPadRestore" })
                    DeleteScheduledTaskQuietly(name);
                foreach (var shim in new[] { "hidhide-pad-hide.vbs", "hidhide-pad-restore.vbs" })
                {
                    try { File.Delete(Path.Combine(HidHideManager.ShimDir, shim)); } catch { }
                }
                // 旧版 pad-hide 一次性凭证：已无消费者，删
                try { File.Delete(Path.Combine(EfootballBot.Core.System.AppPaths.TempDir, "padhide.req")); } catch { }
            });
        }
        catch (Exception ex)
        {
            AppendLog($"手柄环境自检异常：{ex.Message}", LogLevel.Warn);
        }
    }

    private static void DeleteScheduledTaskQuietly(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", $"/Delete /TN {name} /F")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
        }
        catch { /* 未提权删除最高权限任务会失败：无害，下次启动重试 */ }
    }

    private void BtnRecheck_Click(object sender, RoutedEventArgs e) => RunEnvChecks();

    /// <summary>窗口防卡死：把被留在屏幕外的游戏窗口一键拉回（先按停靠记录还原原位置，否则主屏居中）。</summary>
    private void BtnWindowRescue_Click(object sender, RoutedEventArgs e)
        => EfootballBot.Core.System.BackgroundParker.RescueGameWindow(
            (msg, lv) => Dispatcher.BeginInvoke(() => AppendLog(msg, lv)));

    // ---------------- 左栏导航切换 ----------------

    private void NavigateTo(NavMode mode)
    {
        _currentNav = mode;

        bool isGame = mode == NavMode.Game;
        bool isSettings = mode == NavMode.Settings;
        bool isUpdate = mode == NavMode.Update;
        bool isLog = mode == NavMode.LogViewer;
        bool isAbout = mode == NavMode.About;

        // 游戏模块面板
        PanelIntl.Visibility = isGame && !IsCnSelected ? Visibility.Visible : Visibility.Collapsed;
        PanelCn.Visibility = isGame && IsCnSelected ? Visibility.Visible : Visibility.Collapsed;

        // 设置面板
        SettingsPanel.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;

        // 更新面板
        UpdatePanel.Visibility = isUpdate ? Visibility.Visible : Visibility.Collapsed;

        // 日志查看器
        LogViewerPanel.Visibility = isLog ? Visibility.Visible : Visibility.Collapsed;

        // 关于面板
        AboutPanel.Visibility = isAbout ? Visibility.Visible : Visibility.Collapsed;

        // 底部实时日志卡：仅在游戏模块显示
        LogCard.Visibility = isGame && _cfg.ShowRuntimeLog ? Visibility.Visible : Visibility.Collapsed;

        // 切换到日志查看器时刷新文件列表
        if (isLog) RefreshLogFileList();

        // 左栏工具按钮激活高亮（NavToolBtn 样式识别 Tag=Active）
        BtnSettings.Tag = isSettings ? "Active" : null;
        BtnUpdate.Tag = isUpdate ? "Active" : null;
        BtnLogViewer.Tag = isLog ? "Active" : null;
        BtnAbout.Tag = isAbout ? "Active" : null;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.Settings);
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.Update);
    }

    private void BtnLogViewer_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(NavMode.LogViewer);
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.About);
    }
}
