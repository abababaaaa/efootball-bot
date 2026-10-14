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
    // 左栏导航模式：游戏模块 / 设置 / 更新 / 日志历史
    private enum NavMode { Game, Settings, Update, LogViewer }
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
        // ViGEmBus
        bool vigem = VirtualGamepad360.IsDriverInstalled();
        DotVigem.Fill = vigem ? BrushOk : BrushBad;
        BtnInstallVigem.Visibility = vigem ? Visibility.Collapsed : Visibility.Visible;

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

        if (!vigem)
            AppendLog("未检测到 ViGEmBus 驱动，虚拟手柄无法工作。点击左侧「安装 ViGEmBus 驱动」，装完重启本程序。", LogLevel.Warn);
        if (win is null)
            AppendLog(cn
                ? "未找到国服游戏窗口（eFootballOnline / 实况足球在线），请先启动游戏（无需置顶）。"
                : "未找到 eFootball 游戏窗口，请先启动游戏（无需置顶）。", LogLevel.Warn);

        // 手柄环境自愈：shim 指向当前 exe + 遗留任务迁移 + 崩溃残留的禁用手柄恢复
        _ = EnsurePadEnvironmentAsync();
    }

    /// <summary>
    /// 启动自愈（后台，不阻塞 UI）：
    /// ① 重写提权任务 shim 指向当前 exe（程序移动/更新后任务动作恒有效）；
    /// ② 任务缺失/指向旧路径且真实手柄正占着玩家 1 → 自动补注册（一次 UAC）；
    /// ③ 上次挂机禁用的真实手柄未恢复（程序被强杀）→ 静默触发恢复任务。
    /// </summary>
    private async Task EnsurePadEnvironmentAsync()
    {
        try
        {
            HidHideManager.WriteShimScripts();

            bool padHoldingSlot0 = await Task.Run(() =>
                VirtualGamepad360.IsSlot0Occupied()
                && HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0);
            if (padHoldingSlot0 && !await Task.Run(HidHideManager.TasksReady))
            {
                AppendLog("检测到「释放玩家 1」系统任务未就绪（未注册或指向旧路径），正在补注册，请在授权窗口点「是」…", LogLevel.Info);
                var res = await RunHidHideJobAsync(new HidHideConfigJob.HidHideJobDto { Mode = "tasks" });
                AppendLog(res.Success
                    ? "释放玩家 1 任务已就绪。"
                    : $"释放玩家 1 任务注册失败：{res.Error}", res.Success ? LogLevel.Info : LogLevel.Warn);
            }

            if (await Task.Run(PhysicalPadControl.HasPendingRestore))
            {
                AppendLog("检测到上次挂机禁用的真实手柄未恢复，正在自动恢复…", LogLevel.Info);
                HidHideManager.RunTaskSilently(HidHideManager.PadRestoreTaskName);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"手柄环境自检异常：{ex.Message}", LogLevel.Warn);
        }
    }

    private void BtnRecheck_Click(object sender, RoutedEventArgs e) => RunEnvChecks();

    private async void BtnInstallVigem_Click(object sender, RoutedEventArgs e)
    {
        BtnInstallVigem.IsEnabled = false;
        BtnInstallVigem.Content = "正在安装…";
        try
        {
            var installer = new DriverInstaller(msg => AppendLog(msg, LogLevel.Info));
            DriverSetupResult result = await installer.InstallAsync();

            LogLevel level = result.Kind switch
            {
                DriverSetupResultKind.Installed or DriverSetupResultKind.AlreadyPresent when result.DriverReady
                    => LogLevel.Success,
                DriverSetupResultKind.RebootRequired => LogLevel.Warn,
                DriverSetupResultKind.UserCancelled or DriverSetupResultKind.Timeout => LogLevel.Warn,
                DriverSetupResultKind.Failed => LogLevel.Error,
                _ => LogLevel.Warn,
            };

            AppendLog(result.Message, level);
            RunEnvChecks();
        }
        catch (Exception ex)
        {
            AppendLog($"驱动安装异常：{ex}", LogLevel.Error);
        }
        finally
        {
            BtnInstallVigem.Content = "安装内置驱动";
            BtnInstallVigem.IsEnabled = true;
        }
    }

    // ---------------- 左栏导航切换 ----------------

    private void NavigateTo(NavMode mode)
    {
        _currentNav = mode;

        bool isGame = mode == NavMode.Game;
        bool isSettings = mode == NavMode.Settings;
        bool isUpdate = mode == NavMode.Update;
        bool isLog = mode == NavMode.LogViewer;

        // 游戏模块面板
        PanelIntl.Visibility = isGame && !IsCnSelected ? Visibility.Visible : Visibility.Collapsed;
        PanelCn.Visibility = isGame && IsCnSelected ? Visibility.Visible : Visibility.Collapsed;

        // 设置面板
        SettingsPanel.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;

        // 更新面板
        UpdatePanel.Visibility = isUpdate ? Visibility.Visible : Visibility.Collapsed;

        // 日志查看器
        LogViewerPanel.Visibility = isLog ? Visibility.Visible : Visibility.Collapsed;

        // 底部实时日志卡：仅在游戏模块显示
        LogCard.Visibility = isGame && _cfg.ShowRuntimeLog ? Visibility.Visible : Visibility.Collapsed;

        // 切换到日志查看器时刷新文件列表
        if (isLog) RefreshLogFileList();

        // 左栏工具按钮激活高亮（NavToolBtn 样式识别 Tag=Active）
        BtnSettings.Tag = isSettings ? "Active" : null;
        BtnUpdate.Tag = isUpdate ? "Active" : null;
        BtnLogViewer.Tag = isLog ? "Active" : null;
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
}
