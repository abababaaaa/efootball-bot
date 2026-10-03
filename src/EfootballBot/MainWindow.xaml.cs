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

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg;
    private BotEngine? _engine;
    private readonly GlobalHotkey _hotkey = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statsTimer;
    private WriteableBitmap? _wb;
    private byte[]? _previewBuf;
    private bool _previewOn;
    private bool _starting;
    private string _pendingTheme = ThemeManager.Dark;

    // 代码颜色改为运行时从 Application 资源读取（跟随主题）
    private static Brush BrushOk => (Brush)Application.Current.Resources["AccentGlow"];
    private static Brush BrushBad => (Brush)Application.Current.Resources["Danger"];
    private static Brush BrushIdle => (Brush)Application.Current.Resources["EnvDotIdle"];
    private static Brush BrushWarn => (Brush)Application.Current.Resources["Warning"];
    private static Brush BrushLogInfo => (Brush)Application.Current.Resources["LogInfo"];

    /// <summary>第二个实例尝试启动时，由单实例管道回调：恢复最小化并前置本窗口。</summary>
    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public MainWindow()
    {
        InitializeComponent();
        _cfg = AppConfig.Load();

        // 首次启动（无 Theme 字段或为空）→ 显示主题选择覆盖层
        if (string.IsNullOrWhiteSpace(_cfg.Theme))
            _cfg.Theme = ThemeManager.Dark;
        ThemeManager.ApplyTheme(_cfg.Theme);

        LoadConfigToUi();

        // 初始化文件日志（早于 BotEngine 创建，否则第一条日志会丢失）
        FileLogger.Instance.Initialize(_cfg.FileLogEnabled, _cfg.FileLogLevel, _cfg.MaxLogFiles);

        // 启动后自动检查更新（不阻塞 UI）
        _ = AutoCheckUpdateOnStartupAsync();

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewTimer.Tick += (_, _) => UpdatePreviewFrame();
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdateStats();

        // 挂机最小化时暂停预览重采样（300ms 逐帧缩放纯属浪费）；恢复窗口时若预览开着则重启
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _previewTimer.Stop();
            else if (_previewOn) _previewTimer.Start();
        };

        Loaded += (_, _) =>
        {
            // 从未保存过主题选择 → 显示选择界面
            if (!System.IO.File.Exists(AppConfig.ConfigPath))
                ShowThemeOverlay(_cfg.Theme);
            RunEnvChecks();

            // 绑定全局热键到本窗口句柄
            _hotkey.Attach(this);
            _hotkey.Triggered += () => Dispatcher.BeginInvoke(Hotkey_Triggered);
            RefreshHotkeyUi();
        };
    }

    /// <summary>当前左侧选中的模块对应的游戏进程名。</summary>
    private string ActiveProcessName =>
        RbCn.IsChecked == true ? CnConfig.ProcessName : GameWindow.GlobalProcessName;

    private bool IsCnSelected => RbCn.IsChecked == true;

    // ---------------- BotEngine ----------------

    private BotEngine GetOrCreateEngine()
    {
        if (_engine is not null) return _engine;
        _engine = new BotEngine(_cfg);
        _engine.Log += (msg, lv) => Dispatcher.BeginInvoke(() => AppendLog(msg, lv));
        // 文件日志：Debug 级也写（UI AppendLog 跳过 Debug，但这里不跳过）
        _engine.Log += (msg, lv) => FileLogger.Instance.Write(msg, lv);
        _engine.StatsChanged += _ => Dispatcher.BeginInvoke(UpdateStats);
        _engine.Observed += o => Dispatcher.BeginInvoke(() =>
            TxtScreen.Text = $"当前画面：{BotEngine.ScreenName(o.Screen)}（{o.Score:F1}）");
        _engine.Capture.FrameUpdated += (_, _) => { /* 预览由定时器拉取 */ };
        _engine.Capture.StepLog += s => Dispatcher.BeginInvoke(() => AppendLog($"[捕获] {s}", LogLevel.Info));
        return _engine;
    }

    // ---------------- 开始 / 停止 ----------------

    private async void BtnMyLeague_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnMyLeague, "myleague");

    private async void BtnCnDaily_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnCnDaily, "cndaily");

    private async void BtnWeekly_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnWeekly, "intlweekly");

    private async Task ToggleScenarioAsync(Button btn, string? mode)
    {
        if (_starting) return;
        var engine = GetOrCreateEngine();

        if (engine.IsRunning)
        {
            _hotkey.Unregister();
            engine.Stop();
            AppendLog("正在停止…", LogLevel.Info);
            ResetButtons();
            // AutoCloseWhenDone：等待几秒让日志落盘，然后关闭
            if (_cfg.AutoCloseWhenDone)
            {
                AppendLog("全部流程完毕，2 秒后自动关闭…", LogLevel.Success);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Close();
                };
                timer.Start();
            }
            return;
        }

        _starting = true;
        try
        {
            SaveUiToConfig();
            SetStartButtonsEnabled(false);
            TabLeague.IsEnabled = TabWeekly.IsEnabled = TabCnTour.IsEnabled = false;
            await engine.StartAsync(mode!);
            if (engine.IsRunning)
            {
                btn.Content = "停止";
                btn.Style = (Style)FindResource("DangerBtn");
                btn.IsEnabled = true;
                _statsTimer.Start();
                if (!_previewOn) BtnPreview_Click(btn, new RoutedEventArgs()); // 自动开预览

                // 引擎启动后注册全局热键
                if (_cfg.Hotkey.Enabled)
                {
                    try
                    {
                        if (_hotkey.Register(_cfg.Hotkey))
                            AppendLog($"全局热键已注册：{_cfg.Hotkey.DisplayString()}", LogLevel.Info);
                        else
                            AppendLog("全局热键注册失败（可能被其他程序占用）。", LogLevel.Warn);
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"全局热键注册异常：{ex.Message}", LogLevel.Warn);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"启动失败：{ex.Message}", LogLevel.Error);
            ResetButtons();
        }
        finally
        {
            _starting = false;
        }
    }

    private void SetStartButtonsEnabled(bool enabled)
    {
        BtnMyLeague.IsEnabled = enabled;
        BtnCnDaily.IsEnabled = enabled;
        BtnWeekly.IsEnabled = enabled;
    }

    private void ResetButtons()
    {
        BtnMyLeague.Content = "开始";
        BtnCnDaily.Content = "开始";
        BtnWeekly.Content = "开始";
        BtnMyLeague.Style = (Style)FindResource("PrimaryBtn");
        BtnCnDaily.Style = (Style)FindResource("PrimaryBtn");
        BtnWeekly.Style = (Style)FindResource("PrimaryBtn");
        SetStartButtonsEnabled(true);
        RbCn.IsEnabled = RbIntl.IsEnabled = true;
        TabLeague.IsEnabled = TabWeekly.IsEnabled = TabCnTour.IsEnabled = true;
        _statsTimer.Stop();
    }

    private void UpdateStats()
    {
        var s = _engine?.Stats;
        if (s is null) return;
        StMatches.Text = s.MatchesDone.ToString();
        StKey.Text = s.KeyMatches.ToString();
        StForfeit.Text = s.Forfeited.ToString();
        StElapsed.Text = s.StartedAt is null ? "—" : (DateTime.Now - s.StartedAt.Value).ToString(@"hh\:mm\:ss");
        StError.Text = string.IsNullOrEmpty(s.LastError) ? "" : $"最近错误：{s.LastError}";

        // 场景自动结束（正常跑完 / 异常退出）：IsRunning 变 false 后按钮需从"停止"恢复为"开始"
        if (_engine is { IsRunning: false } && _statsTimer.IsEnabled)
        {
            _hotkey.Unregister();
            AppendLog("场景已结束，自动停止。", LogLevel.Success);
            ResetButtons();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveUiToConfig();
        _previewTimer.Stop();
        _statsTimer.Stop();
        _hotkey.Dispose();
        _engine?.Dispose();
        FileLogger.Instance.Dispose();
        base.OnClosing(e);
    }
}
