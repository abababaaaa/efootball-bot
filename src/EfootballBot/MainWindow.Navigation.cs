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
        if (_workerHost?.ScenarioRunning != true)
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
        // 虚拟手柄驱动：ViGEmBus（唯一后端，内核驱动服务；缺失时开始挂机自动安装）。
        // 绿点 = 驱动已装；虚拟手柄仅挂机连接期间在线
        bool vigem = VigemPad.IsDriverInstalled();
        DotPad.Fill = vigem ? BrushOk : BrushBad;
        TxtPad.Text = vigem ? "虚拟手柄驱动（ViGEmBus 已就绪）"
            : "ViGEmBus 驱动未安装（开始挂机时自动装）";
        bool padMissing = !vigem;

        // WGC 后台捕获
        bool wgc = WindowCapture.IsSupported();
        DotWgc.Fill = wgc ? BrushOk : BrushBad;

        // OCR 引擎：PP-OCRv6 模型随程序自带、不依赖系统语言包
        try
        {
            DotOcr.Fill = BrushOk;
            TxtOcr.Text = _cfg.OcrEngine switch
            {
                OcrEngineKind.RapidV6 => "OCR 引擎（RapidOCR Small）",
                OcrEngineKind.UnlimitedOcr => "OCR 引擎（Unlimited-OCR 外部服务）",
                _ => "OCR 引擎（SimdPaddle）",
            };
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
            AppendLog("ViGEmBus 驱动未安装：开始挂机时会自动静默安装（需管理员授权，首次约 10-30 秒）。", LogLevel.Warn);
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
    /// ② 幂等注册提权任务（pad-hide / pad-restore）+ 一次性清理已剔除的 HidHide 遗留；
    /// ③ 挂机中崩溃残留的禁用真实手柄 → 触发 pad-restore 恢复（未提权时 schtasks 静默失败，下次重试）。
    /// </summary>
    private async Task EnsurePadEnvironmentAsync()
    {
        try
        {
            EfootballBot.Core.Input.PhysicalPadControl.WriteShimScripts();

            await Task.Run(() =>
            {
                try
                {
                    EfootballBot.Core.Input.PhysicalPadControl.LegacyHidHideCleanupOnce();
                    EfootballBot.Core.Input.PhysicalPadControl.EnsureTasksRegistered();

                    if (EfootballBot.Core.Input.PhysicalPadControl.HasPendingRestore()
                        || EfootballBot.Core.Input.PhysicalPadControl.HasDisabledGamepad())
                    {
                        Dispatcher.BeginInvoke(() =>
                            AppendLog("检测到仍被禁用的真实手柄，触发自动恢复…", LogLevel.Info));
                        EfootballBot.Core.Input.PhysicalPadControl.RunTaskSilently(
                            EfootballBot.Core.Input.PhysicalPadControl.PadRestoreTaskName);
                        Dispatcher.BeginInvoke(() =>
                            AppendLog("手柄恢复任务已触发（详见 data\\pad-control.log）", LogLevel.Info));
                    }
                }
                catch { /* 未提权等：下次启动重试 */ }
            });
        }
        catch (Exception ex)
        {
            AppendLog($"手柄环境自检异常：{ex.Message}", LogLevel.Warn);
        }
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

        // 模块胶囊同一套 Tag=Active 门控：只有正在看游戏模块时才亮，否则会和工具按钮同时亮
        // （ModuleRadio 模板要求 IsChecked && Tag==Active，见 App.xaml）
        RbCn.Tag = isGame && IsCnSelected ? "Active" : null;
        RbIntl.Tag = isGame && !IsCnSelected ? "Active" : null;
    }

    /// <summary>
    /// 点击模块胶囊：已选中的那一项再点一次也能回到游戏模块页。
    /// 必须用 Click 而不是 Checked——点已选中的 RadioButton 时 IsChecked 不变，Checked 不触发，
    /// 旧实现下从设置页想回国际服只能先点国服再点回来（用户 2026-10-28 反馈）。
    /// 切换服务器时 Checked 先跑（已是目标面板），这里因 _currentNav 已是 Game 而跳过。
    /// </summary>
    private void RbModule_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNav != NavMode.Game)
            NavigateTo(NavMode.Game);
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
