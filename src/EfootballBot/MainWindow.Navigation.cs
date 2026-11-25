using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EfootballBot.Core.Tools;
using EfootballBot.Core;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.System;
using EfootballBot.Core.Logging;
using EfootballBot.Core.UI;
using EfootballBot.Core.Vision;
using EfootballBot.Shell;

namespace EfootballBot;

public partial class MainWindow
{
    // 用户要求：每次开启软件默认停在「更新」页（不再默认游戏模块）。
    private NavMode _currentNav = NavMode.Home;

    private NavMode _lastToolNav = NavMode.Settings;
    private bool _suppressTopNav;
    private bool _suppressSubTab;

    private void BtnHomeStart_Click(object sender, RoutedEventArgs e) => NavigateTo(NavMode.Game);

    /// <summary>首页功能磁贴：直接跳到对应模块（服务器 + 三级页签一起切好）。</summary>
    private void BtnHomeTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        switch (tag)
        {
            case "League":
                RbIntl.IsChecked = true;
                NavigateTo(NavMode.Game);
                RbMyLeague.IsChecked = true;
                break;
            case "Weekly":
                RbIntl.IsChecked = true;
                NavigateTo(NavMode.Game);
                RbWeekly.IsChecked = true;
                break;
            case "AiLadder":
                RbIntl.IsChecked = true;
                NavigateTo(NavMode.Game);
                RbAiLadder.IsChecked = true;
                break;
            case "Stud":
                RbIntl.IsChecked = true;
                NavigateTo(NavMode.Game);
                RbStud.IsChecked = true;
                break;
            case "CnLeague":
                RbCn.IsChecked = true;
                NavigateTo(NavMode.Game);
                RbCnLeague.IsChecked = true;
                break;
            case "CnWeekly":
                RbCn.IsChecked = true;
                NavigateTo(NavMode.Game);
                RbCnWeekly.IsChecked = true;
                break;
            case "CnMyLeague":
                RbCn.IsChecked = true;
                NavigateTo(NavMode.Game);
                RbCnMyLeague.IsChecked = true;
                break;
            case "Network":
                NavigateTo(NavMode.Network);
                return;
            case "Settings":
                _devMode = false;
                NavigateTo(NavMode.Settings);
                return;
        }
        SyncSubTabs();
    }

    private void BtnHomeFeedback_Click(object sender, RoutedEventArgs e) => NavigateTo(NavMode.Feedback);

    private void TopNav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressTopNav) return;
        if (sender is not RadioButton { Tag: string tag }) return;
        switch (tag)
        {
            case "Home": NavigateTo(NavMode.Home); break;
            case "Game": NavigateTo(NavMode.Game); break;
            case "About": NavigateTo(NavMode.About); break;
            default:
                NavigateTo(_lastToolNav is NavMode.Game or NavMode.About ? NavMode.Settings : _lastToolNav);
                break;
        }
    }

    /// <summary>三级菜单点击 → 转发给面板里原来的隐藏页签（它才是真正的状态机）。</summary>
    private void SubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressSubTab) return;
        if (sender is not RadioButton { Tag: string target }) return;
        // 直接按 Tag 切换内容面板：镜像页签转发不可靠（目标本来就是选中态时 Checked 不触发），
        // 单选框只作为视觉状态，内容可见性由这里统一决定。
        ShowByTag(target);

        if (FindName(target) is not RadioButton src) return;
        if (src.IsChecked == true)
        {
            // 目标页签本来就是选中态（默认页）：先复位再置位，强制触发一次 Checked，
            // 否则「我的联赛」这类默认页点了没反应，看起来像按钮失效。
            src.IsChecked = false;
        }
        src.IsChecked = true;
    }

    /// <summary>
    /// 工具页分段：Tag 形如 "UpdatePanel:1" —— 把该 ScrollViewer 的内容按顺序只显示第 N 段。
    /// 不改动页面本身的结构，因此不会影响任何控件与绑定。
    /// </summary>
    private void SecTabByIndex_Checked(object sender, RoutedEventArgs e) { }

    /// <summary>设置页子分段：只显示 Tag 指定的那块（防检测 / 赛后活动 / 挂机节奏）。</summary>
    private void SecTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not RadioButton { Tag: string target }) return;

        // 形如 "UpdatePanel:1" → 按索引切段（统一走 ShowSection：可见性、滚动条策略、切页动效一处决定）
        if (target.Contains(':'))
        {
            var parts = target.Split(':');
            if (!int.TryParse(parts[1], out var idx)) return;
            ShowSection(parts[0], idx);
            // 对战线路段：每次切进来都实读一次状态（防火墙规则 / hosts 可能被别的软件或上一次会话改过）
            if (parts[0] == "NetworkPanel" && idx == 2) RefreshLineState();
            return;
        }

        // 设置页镜像页签：Imp1/2/3 走 Important 块内的子分段；
        // RbSetDisplay/Input/Daily 走各自独立的内容块。之前后三个误用 SubTab_Checked，
        // 其 ShowByTag 不认识 RbSet*，导致内容不切换、选中态也不常驻 —— 这里统一处理。
        ApplySettingsSection(target);
    }

    /// <summary>按设置二级页签 Tag 同步显示设置内容区。</summary>
    private void ApplySettingsSection(string target)
    {
        bool isImp = target.StartsWith("Imp");
        SetTabImportant.Visibility = isImp ? Visibility.Visible : Visibility.Collapsed;
        SetTabDisplay.Visibility = target == "RbSetDisplay" ? Visibility.Visible : Visibility.Collapsed;
        SetTabInput.Visibility = target == "RbSetInput" ? Visibility.Visible : Visibility.Collapsed;
        SetTabDaily.Visibility = target == "RbSetDaily" ? Visibility.Visible : Visibility.Collapsed;
        SetTabDev.Visibility = Visibility.Collapsed;
        if (isImp)
        {
            foreach (UIElement child in SetTabImportant.Children)
                if (child is FrameworkElement fe2 && fe2.Tag is string tg && tg.StartsWith("Imp"))
                    fe2.Visibility = tg == target ? Visibility.Visible : Visibility.Collapsed;
        }
        if (target == "RbSetInput")
            RefreshPadBackendState();
    }

    /// <summary>只显示某个工具页滚动内容里的第 idx 段。</summary>
    private void ShowSection(string panelName, int idx)
    {
        if (FindName(panelName) is not ScrollViewer sv || sv.Content is not Panel content) return;
        if (idx < 0 || idx >= content.Children.Count) return;
        for (var k = 0; k < content.Children.Count; k++)
            content.Children[k].Visibility = k == idx ? Visibility.Visible : Visibility.Collapsed;
        AnimatePageIn(content.Children[idx] as FrameworkElement);

        // 服务器测速页：关掉外层纵向滚动，内容才能拿到视口高度，
        // 表格的 Star 行才会真正向下撑满（否则 ScrollViewer 用无限高度测量，* 等于 Auto）。
        var fill = (panelName == "NetworkPanel" && idx == 0)
                || (panelName == "UpdatePanel" && idx == 1);
        sv.VerticalScrollBarVisibility = fill ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        sv.ScrollToTop();
    }

    /// <summary>切页动效时长：180 ms（短到不拖节奏，长到能看清方向）。</summary>
    private static readonly Duration PageFade = new(TimeSpan.FromMilliseconds(180));

    /// <summary>
    /// 切页动效：淡入 + 4 px 上浮，EaseOut。页面已有的 RenderTransform 不动它（只做淡入）；
    /// 配置关闭或窗口未加载时直接落到稳定态，避免出现半透明页面。
    /// </summary>
    private void AnimatePageIn(FrameworkElement? el)
    {
        if (el is null) return;
        if (_cfg.Appearance.PageTransition != true || !IsLoaded)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, PageFade) { EasingFunction = ease });

        if (el.RenderTransform is null or MatrixTransform)
        {
            var slide = new TranslateTransform();
            el.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(4, 0, PageFade) { EasingFunction = ease });
        }
    }

    private void ShowByTag(string tag)
    {
        void Set(FrameworkElement panel, bool on)
        {
            var target = on ? Visibility.Visible : Visibility.Collapsed;
            if (panel.Visibility == target) return;
            panel.Visibility = target;
            if (on) AnimatePageIn(panel);
        }

        switch (tag)
        {
            case "RbPreMatch": Set(PreMatchContent, true); Set(LeagueContent, false); Set(WeeklyContent, false); Set(AiLadderContent, false); Set(StudContent, false); break;
            case "RbMyLeague": Set(PreMatchContent, false); Set(LeagueContent, true); Set(WeeklyContent, false); Set(AiLadderContent, false); Set(StudContent, false); break;
            case "RbWeekly": Set(PreMatchContent, false); Set(LeagueContent, false); Set(WeeklyContent, true); Set(AiLadderContent, false); Set(StudContent, false); break;
            case "RbAiLadder": Set(PreMatchContent, false); Set(LeagueContent, false); Set(WeeklyContent, false); Set(AiLadderContent, true); Set(StudContent, false); break;
            case "RbStud": Set(PreMatchContent, false); Set(LeagueContent, false); Set(WeeklyContent, false); Set(AiLadderContent, false); Set(StudContent, true); break;
            case "RbCnPreMatch": Set(CnPreMatchContent, true); Set(CnLeagueContent, false); Set(CnWeeklyContent, false); Set(CnMyLeagueContent, false); break;
            case "RbCnLeague": Set(CnPreMatchContent, false); Set(CnLeagueContent, true); Set(CnWeeklyContent, false); Set(CnMyLeagueContent, false); break;
            case "RbCnWeekly": Set(CnPreMatchContent, false); Set(CnLeagueContent, false); Set(CnWeeklyContent, true); Set(CnMyLeagueContent, false); break;
            case "RbCnMyLeague": Set(CnPreMatchContent, false); Set(CnLeagueContent, false); Set(CnWeeklyContent, false); Set(CnMyLeagueContent, true); break;
        }
    }

    private void SyncSubTabs()
    {
        void Sync(RadioButton sub, RadioButton src)
        {
            if (src?.IsChecked == true && sub.IsChecked != true)
            {
                _suppressSubTab = true;
                sub.IsChecked = true;
                _suppressSubTab = false;
            }
        }
        Sync(SubRbPreMatch, RbPreMatch);
        Sync(SubRbMyLeague, RbMyLeague);
        Sync(SubRbWeekly, RbWeekly);
        Sync(SubRbAiLadder, RbAiLadder);
        Sync(SubRbStud, RbStud);
        Sync(SubRbCnPreMatch, RbCnPreMatch);
        Sync(SubRbCnLeague, RbCnLeague);
        Sync(SubRbCnWeekly, RbCnWeekly);
        Sync(SubRbCnMyLeague, RbCnMyLeague);
        Sync(SubRbSetImportant, RbSetImportant);
        Sync(SubRbSetDisplay, RbSetDisplay);
        Sync(SubRbSetInput, RbSetInput);
        Sync(SubRbSetDaily, RbSetDaily);
        // 开发者选项已独立成左栏入口，不再参与三级菜单同步
    }

    /// <summary>拖动由 TopBar 手动处理；不再二次设置 WindowChrome，
    /// 否则会和 FluentWindow 模板里的 chrome 叠成两套，导致点击命中偏移（控件看着在、点不到）。</summary>
    private void ApplyCaptionChrome()
    {
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 命中任何 Control（按钮、下拉框、输入框、勾选框…）都不抢鼠标：
        // 早期只排除了 ButtonBase，导致点 ComboBox 时也会进 DragMove 的模态循环，
        // 窗口被拖住，所有下拉/输入都「看着在、点不动」。
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null && d != sender;
             d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Control) return;
        }
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

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

    /// <summary>显示主题选择覆盖层，current 为当前生效主题（高亮选中），world 为当前外观世界。</summary>
    private void ShowThemeOverlay(string current, string? world = null)
    {
        _origTheme = current;
        _origWorld = ThemeManager.NormalizeWorld(world ?? _cfg.Appearance.World);
        _pendingTheme = _origTheme;
        _pendingWorld = _origWorld;
        HighlightThemeCard(_pendingTheme);
        HighlightWorldCard(_pendingWorld);
        ThemeOverlay.Visibility = Visibility.Visible;
        BtnThemeConfirm.Visibility = Visibility.Visible;
    }

    private void HideThemeOverlay()
    {
        ThemeOverlay.Visibility = Visibility.Collapsed;
    }

    private void ApplyThemePreview()
    {
        ThemeManager.ApplyTheme(_pendingTheme, _pendingWorld);
        ApplyWindowAppearance();
        HighlightThemeCard(_pendingTheme);
        HighlightWorldCard(_pendingWorld);
    }

    private void CancelThemeOverlay()
    {
        if (ThemeOverlay.Visibility != Visibility.Visible) return;
        ThemeManager.ApplyTheme(_origTheme, _origWorld);
        ApplyWindowAppearance();
        HideThemeOverlay();
    }

    private void HighlightThemeCard(string theme)
    {
        void Reset(Border card)
        {
            card.Background = Brushes.Transparent;
            card.BorderBrush = (Brush)Application.Current.Resources["CardBorder"];
        }

        Reset(CardDark);
        Reset(CardLight);
        Reset(CardDeepBlue);

        var selected = theme switch
        {
            ThemeManager.Light => CardLight,
            ThemeManager.DeepBlue => CardDeepBlue,
            _ => CardDark,
        };
        selected.Background = (Brush)Application.Current.Resources["AccentLight"];
        selected.BorderBrush = (Brush)Application.Current.Resources["Accent"];
    }

    /// <summary>外观风格（视觉世界）卡片选中态：Fluent = Win11 云母，Glass = Apple 黑玻璃。</summary>
    private void HighlightWorldCard(string world)
    {
        void Reset(Border card)
        {
            card.Background = Brushes.Transparent;
            card.BorderBrush = (Brush)Application.Current.Resources["CardBorder"];
        }

        Reset(CardWorldFluent);
        Reset(CardWorldGlass);

        var selected = ThemeManager.IsGlassWorld(world) ? CardWorldGlass : CardWorldFluent;
        selected.Background = (Brush)Application.Current.Resources["AccentLight"];
        selected.BorderBrush = (Brush)Application.Current.Resources["Accent"];
    }

    private void ThemeCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string tag })
        {
            _pendingTheme = tag;
            ApplyThemePreview();
        }
    }

    private void WorldCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string tag })
        {
            _pendingWorld = ThemeManager.NormalizeWorld(tag);
            ApplyThemePreview();
        }
    }

    private void BtnThemeCancel_Click(object sender, RoutedEventArgs e) => CancelThemeOverlay();

    private void ThemeOverlay_BackdropClick(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d is not null;
             d = VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, ThemeDialogCard)) return;
        }
        CancelThemeOverlay();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ThemeOverlay.Visibility == Visibility.Visible)
        {
            CancelThemeOverlay();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void BtnThemeConfirm_Click(object sender, RoutedEventArgs e)
    {
        _cfg.Theme = _pendingTheme;
        _cfg.Appearance.World = ThemeManager.NormalizeWorld(_pendingWorld);
        // 两个轴一起下发：颜色 Token + 渐变/投影/噪点 + 窗口不透明度都由 ApplyTheme 重建
        ThemeManager.ApplyTheme(_cfg.Theme, _cfg.Appearance.World);
        ApplyWindowAppearance();   // ApplyTheme 复原了纯色外壳，材质要重新贴上去
        _cfg.Save();
        HideThemeOverlay();
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        ShowThemeOverlay(_cfg.Theme, _cfg.Appearance.World);
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
            if (EfootballBot.Core.Input.PhysicalPadControl.IsBotSessionActive()
                || _workerHost is { ScenarioRunning: true } || _starting)
                return;

            EfootballBot.Core.Input.PhysicalPadControl.WriteShimScripts();

            await Task.Run(() =>
            {
                try
                {
                    EfootballBot.Core.Input.PhysicalPadControl.LegacyHidHideCleanupOnce();
                    EfootballBot.Core.Input.PhysicalPadControl.EnsureTasksRegistered();

                    if (EfootballBot.Core.Input.PhysicalPadControl.IsBotSessionActive())
                        return;

                    if (EfootballBot.Core.Input.PhysicalPadControl.HasPendingRestore()
                        || EfootballBot.Core.Input.PhysicalPadControl.HasDisabledGamepad())
                    {
                        Dispatcher.BeginInvoke(() =>
                            AppendLog("检测到仍被禁用的真实手柄，正在恢复…", LogLevel.Info));
                        if (EfootballBot.Core.System.Elevation.IsCurrentProcessElevated())
                        {
                            if (!EfootballBot.Core.Input.PhysicalPadControl.IsBotSessionActive())
                            {
                                var (_, msg) = EfootballBot.Core.Input.PhysicalPadControl.RestoreAll();
                                Dispatcher.BeginInvoke(() =>
                                    AppendLog($"真实手柄恢复：{msg}", LogLevel.Info));
                            }
                        }
                        else
                        {
                            EfootballBot.Core.Input.PhysicalPadControl.RunTaskSilently(
                                EfootballBot.Core.Input.PhysicalPadControl.PadRestoreTaskName);
                            Dispatcher.BeginInvoke(() =>
                                AppendLog("手柄恢复任务已触发（详见 data\\pad-control.log）", LogLevel.Info));
                        }
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

    /// <summary>窗口防卡死：把被留在屏幕外的游戏窗口一键拉回（先按停靠记录还原原位置，否则主屏居中）。
    /// 实现在工具层（Core/Tools/WindowRescueTool → BackgroundParker），界面只派发。</summary>
    private async void BtnWindowRescue_Click(object sender, RoutedEventArgs e)
        => await RunToolAsync(ToolIds.WindowRescue, ToolAction.RescueWindow, ServerRegion.Intl);

    // ---------------- 左栏导航切换 ----------------

    /// <summary>收起/展开右栏：收起时右栏列宽归零、中栏自动铺满，右侧留一条展开窄条。</summary>
    private void SetRightPanelCollapsed(bool collapsed)
    {
        RightPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RightRail.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        RightCol.MinWidth = collapsed ? 0 : 320;
        RightCol.MaxWidth = collapsed ? 0 : 400;
        RightCol.Width = collapsed ? new GridLength(0) : new GridLength(0.55, GridUnitType.Star);
    }


    private bool _rightUserCollapsed;

    /// <summary>首页：整块右栏（实时画面/统计/日志）与展开条一起隐藏，中栏占满整行。</summary>
    private void ApplyHomeLayout()
    {
        SetRightPanelCollapsed(true);
        RightRail.Visibility = Visibility.Collapsed;
    }

    private void BtnRightCollapse_Click(object sender, RoutedEventArgs e)
    {
        _rightUserCollapsed = true;
        SetRightPanelCollapsed(true);
    }

    private void BtnRightExpand_Click(object sender, RoutedEventArgs e)
    {
        _rightUserCollapsed = false;
        SetRightPanelCollapsed(false);
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
        _devMode = false;
        SaveUiToConfig();
        NavigateTo(NavMode.Settings);
    }

    private bool _devMode;

    /// <summary>
    /// 左栏「开发者」：进开发者页。只负责置位 _devMode 再交给 NavigateTo ——
    /// 页签显隐、页签条显隐、左栏高亮全部由路由表决定，这里不再手写一遍
    /// （旧写法有 8 行 Visibility 镜像逻辑，和 NavigateTo 里那份必须永远保持一致）。
    /// </summary>
    private void BtnDevTools_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        _devMode = true;
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

    private void BtnGitHub_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.GitHub);
    }

    private void BtnNetwork_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.Network);
    }

    private void BtnFeedback_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.Feedback);
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        NavigateTo(NavMode.About);
    }
}


