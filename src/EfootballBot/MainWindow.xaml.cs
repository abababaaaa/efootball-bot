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
using EfootballBot.Ipc;

namespace EfootballBot;

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg;
    private WorkerHost? _workerHost;
    private readonly GlobalHotkey _hotkey = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statsTimer;
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
        FileLogger.Instance.Initialize(_cfg.FileLogEnabled, _cfg.FileLogLevel, _cfg.MaxLogFiles, _cfg.LogRetentionDays);

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
        RbCn.IsChecked == true ? BotEngine.CnProcessName : GameWindow.GlobalProcessName;

    private bool IsCnSelected => RbCn.IsChecked == true;

    // ---------------- WorkerHost ----------------

    private WorkerHost GetOrCreateWorkerHost()
    {
        if (_workerHost is not null) return _workerHost;
        _workerHost = new WorkerHost();
        _workerHost.Log += (lv, msg) =>
        {
            // 文件日志：Worker 转发的全部日志落盘（M1 桥接补回，等同旧 engine.Log 双订阅）
            FileLogger.Instance.Write(msg, Enum.TryParse<LogLevel>(lv, out var fl) ? fl : LogLevel.Info);
            Dispatcher.BeginInvoke(() =>
                AppendLog(msg, Enum.TryParse<LogLevel>(lv, out var l) ? l : LogLevel.Info));
        };
        _workerHost.StatsChanged += s => Dispatcher.BeginInvoke(UpdateStats);
        // Worker 发感知摘要文本（"OCR N 词 / 检测 N 框"）+ 证据列表
        _workerHost.ScreenObserved += (screen, score, evidence) => Dispatcher.BeginInvoke(() =>
        {
            TxtScreen.Text = $"感知：{screen}（{score:F0}）";
            if (!string.IsNullOrEmpty(evidence))
                TxtScreen.Text += $"  {evidence}";
        });
        _workerHost.PreviewReceived += pngB64 => Dispatcher.BeginInvoke(() => ShowPreviewPng(pngB64));
        _workerHost.Exited += reason => Dispatcher.BeginInvoke(() =>
        {
            AppendLog($"场景已结束：{reason}", LogLevel.Success);
            ResetButtons();
            _statsTimer.Stop();
            _hotkey.Unregister();
        });
        _workerHost.Disconnected += () => Dispatcher.BeginInvoke(() =>
        {
            AppendLog("Worker 进程已断开，场景已停止。", LogLevel.Warn);
            ResetButtons();
            _statsTimer.Stop();
            _hotkey.Unregister();
        });
        return _workerHost;
    }

    // ---------------- 开始 / 停止 ----------------

    private async void BtnStartIntl_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartIntl, "observe");

    private async void BtnStartCn_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartCn, "cndaily");

    private async Task ToggleScenarioAsync(Button btn, string? mode)
    {
        if (_starting) return;
        var host = GetOrCreateWorkerHost();

        if (host.IsRunning)
        {
            // UI 反馈必须前置：StopAsync 要发 stop 并等 Worker 退出（最长 5s+Kill），
            // 放在后面会让"停止"按钮看起来卡死几秒
            _hotkey.Unregister();
            AppendLog("正在停止…", LogLevel.Info);
            ResetButtons();
            await host.StopAsync();
            AppendLog("已停止。", LogLevel.Info);
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

        // 开始前必须手动确认的两点：
        // ① 真实手柄占用 XInput 玩家1 时虚拟手柄输入会被游戏忽略，挂机前需拔下/关闭；
        // ② 全屏（尤其超宽）下定位与捕获均不稳定，720p 窗口模式是实测可靠配置。
        var confirm = System.Windows.MessageBox.Show(
            this,
            "开始前请确认两件事：\n\n" +
            "① 关闭/拔下自己的真实手柄\n真实手柄会占用玩家 1，挂机用的虚拟手柄输入将被游戏忽略。\n\n" +
            "② 游戏最好以 720p 窗口模式运行\n无边框窗口 1280×720 最稳定；独占全屏下挂机无法窗口化且定位易失效。",
            "开始挂机前确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        _starting = true;
        try
        {
            SaveUiToConfig();
            SetStartButtonsEnabled(false);
            // 立即反馈：Worker 冷启动握手最长 30s，期间按钮必须显示"启动中…"而非无响应
            btn.Content = "启动中…";
            AppendLog("正在启动 Worker（首次冷启动需解压，可能 10 秒以上）…", LogLevel.Info);
            await host.StartAsync(mode!);
            if (host.IsRunning)
            {
                btn.Content = "停止";
                btn.Style = (Style)FindResource("DangerBtn");
                btn.IsEnabled = true;
                BtnPause.IsEnabled = true;
                _paused = false;
                BtnPause.Content = "暂停";
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
            // 空 Message 的异常（如单文件 exe 发布写入中途启动导致的程序集加载失败）
            // 只打 Message 什么都看不到，输出类型+堆栈+内部异常+HRESULT
            string detail = ex.ToString();
            if (ex.InnerException is not null)
                detail += $"\n → 内部异常：{ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
            AppendLog($"启动失败：{detail}", LogLevel.Error);
            ResetButtons();
        }
        finally
        {
            _starting = false;
        }
    }

    private void SetStartButtonsEnabled(bool enabled)
    {
        BtnStartIntl.IsEnabled = enabled;
        BtnStartCn.IsEnabled = enabled;
    }

    // 暂停状态（仅 GUI 侧镜像，真实状态在 Worker 引擎；停止时复位）
    private bool _paused;

    private async void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        var host = _workerHost;
        if (host is not { IsRunning: true }) return;
        bool next = !_paused;
        try
        {
            await host.SendPauseAsync(next);
        }
        catch (Exception ex)
        {
            AppendLog($"暂停命令发送失败：{ex.Message}", LogLevel.Error);
            return;
        }
        _paused = next;
        BtnPause.Content = _paused ? "继续" : "暂停";
    }

    private void ResetButtons()
    {
        BtnStartIntl.Content = "开始";
        BtnStartCn.Content = "开始";
        BtnStartIntl.Style = (Style)FindResource("PrimaryBtn");
        BtnStartCn.Style = (Style)FindResource("PrimaryBtn");
        SetStartButtonsEnabled(true);
        BtnPause.IsEnabled = false;
        _paused = false;
        BtnPause.Content = "暂停";
        RbCn.IsEnabled = RbIntl.IsEnabled = true;
        _statsTimer.Stop();
    }

    private void UpdateStats()
    {
        var s = _workerHost?.LastStats;
        if (s is null) return;
        StMatches.Text = s.MatchesDone.ToString();
        StKey.Text = s.KeyMatches.ToString();
        StForfeit.Text = s.Forfeited.ToString();
        StElapsed.Text = s.StartedAt is null ? "—" : (DateTime.Now - s.StartedAt.Value).ToString(@"hh\:mm\:ss");
        StError.Text = string.IsNullOrEmpty(s.LastError) ? "" : $"最近错误：{s.LastError}";

        // 场景自动结束（正常跑完 / 异常退出）：IsRunning 变 false 后按钮需从"停止"恢复为"开始"
        if (_workerHost is { IsRunning: false } && _statsTimer.IsEnabled)
        {
            _hotkey.Unregister();
            AppendLog("场景已结束，自动停止。", LogLevel.Success);
            ResetButtons();
        }
    }

    /// <summary>将 Worker 传来的 PNG base64 解码显示到预览控件。</summary>
    private void ShowPreviewPng(string pngBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(pngBase64);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            Preview.Source = bmp;
        }
        catch { /* 解码失败跳过这一帧 */ }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveUiToConfig();
        _previewTimer.Stop();
        _statsTimer.Stop();
        _hotkey.Dispose();
        _workerHost?.Dispose();
        FileLogger.Instance.Dispose();
        base.OnClosing(e);
    }
}
