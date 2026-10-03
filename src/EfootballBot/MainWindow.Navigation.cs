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
    // 左栏导航模式：游戏模块 / 设置 / 日志历史
    private enum NavMode { Game, Settings, LogViewer }
    private NavMode _currentNav = NavMode.Game;

    private void RbModule_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // 初始化期不处理

        // 允许运行中切换国服/国际服视图（只看另一服务器的配置面板，不停引擎）
        // 引擎已绑定当前 scenario 的进程，切换 RadioButton 仅影响中栏面板显示
        NavigateTo(NavMode.Game);

        // 运行中不重新检测环境（避免切换视图时误报另一服务器窗口状态）
        if (_engine?.IsRunning != true)
            RunEnvChecks();
    }

    /// <summary>国际服功能分段切换：只显示所选功能的详情面板。</summary>
    private void IntlTab_Checked(object sender, RoutedEventArgs e)
    {
        // XAML 初始化期（IsChecked=True 内联设置）控件可能尚未全部就绪
        if (PaneLeague == null || PaneWeekly == null) return;
        PaneLeague.Visibility = TabLeague.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaneWeekly.Visibility = TabWeekly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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

        // OCR 语言
        try
        {
            var langs = OcrRecognizer.AvailableLanguages();
            bool zh = langs.Any(l => l.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
            DotOcr.Fill = zh ? BrushOk : BrushBad;
            TxtOcr.Text = zh ? $"OCR 语言（{string.Join(",", langs.Take(2))}）" : "OCR 缺中文语言包";
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
    }

    private void BtnRecheck_Click(object sender, RoutedEventArgs e) => RunEnvChecks();

    private void BtnInstallVigem_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/nefarius/ViGEmBus/releases") { UseShellExecute = true });
        AppendLog("已打开 ViGEmBus 下载页：下载并运行 ViGEmBusSetup_x64.msi，装完重启本程序。", LogLevel.Info);
    }

    // ---------------- 左栏导航切换 ----------------

    private void NavigateTo(NavMode mode)
    {
        _currentNav = mode;

        bool isGame = mode == NavMode.Game;
        bool isSettings = mode == NavMode.Settings;
        bool isLog = mode == NavMode.LogViewer;

        // 游戏模块面板
        PanelIntl.Visibility = isGame && !IsCnSelected ? Visibility.Visible : Visibility.Collapsed;
        PanelCn.Visibility = isGame && IsCnSelected ? Visibility.Visible : Visibility.Collapsed;

        // 设置面板
        SettingsPanel.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;

        // 日志查看器
        LogViewerPanel.Visibility = isLog ? Visibility.Visible : Visibility.Collapsed;

        // 底部实时日志卡：仅在游戏模块显示
        LogCard.Visibility = isGame && _cfg.ShowRuntimeLog ? Visibility.Visible : Visibility.Collapsed;

        // Tab 分段栏（Grid.Row="0"）：设置/日志模式时隐藏
        IntlTabsBar.Visibility = isGame && !IsCnSelected ? Visibility.Visible : Visibility.Collapsed;
        if (CnTabsBar != null) CnTabsBar.Visibility = isGame && IsCnSelected ? Visibility.Visible : Visibility.Collapsed;

        // 切换到日志查看器时刷新文件列表
        if (isLog) RefreshLogFileList();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.Settings);
    }

    private void BtnLogViewer_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(NavMode.LogViewer);
    }
}
