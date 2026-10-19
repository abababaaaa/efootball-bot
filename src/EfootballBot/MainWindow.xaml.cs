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
    private readonly Core.Input.SmartHotkey _hotkey = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statsTimer;
    private bool _previewOn;
    private bool _starting;
    private string _pendingTheme = ThemeManager.Dark;

    // 代码颜色统一在 MainWindow.Logs.cs 定义（运行时从 Application 资源读取，跟随主题）

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
        AppendLog($"应用启动：v{Updater.FormatVersion(typeof(MainWindow).Assembly.GetName().Version)}，" +
            $"{Environment.OSVersion.VersionString}", LogLevel.Info);

        // 启动后自动检查更新（不阻塞 UI）
        _ = AutoCheckUpdateOnStartupAsync();

        // 清理历史更新残留（临时下载目录/旧版 .old 备份，见 UpdateApplier.CleanupStaleArtifacts）
        _ = System.Threading.Tasks.Task.Run(UpdateApplier.CleanupStaleArtifacts);

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

            // 绑定全局热键（低级钩子：键盘任意键/鼠标键；主键=暂停→停止，恢复键=继续）
            _hotkey.Attach(this);
            _hotkey.PauseStopTriggered += () => Dispatcher.BeginInvoke(Hotkey_PauseStop_Triggered);
            _hotkey.ResumeTriggered += () => Dispatcher.BeginInvoke(Hotkey_Resume_Triggered);
            RefreshHotkeyUi();
        };

        // 环境检测在 Loaded 事件里跑：构造到此结束即界面可用（v1.1.19 远程排障心跳，
        // 区分"应用打开后卡死"与"正常打开"——后者日志必有此行）
        AppendLog("主界面就绪。", LogLevel.Info);

        // 上次运行如有崩溃转储，启动时提醒用户生成反馈包（dump 本体在 data\crash\，只留最近 3 份）
        var crashNotice = CrashReporter.RecentDumpNotice(TimeSpan.FromDays(2));
        if (crashNotice is not null)
            AppendLog(crashNotice, LogLevel.Warn);
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
            // 落盘统一走 AppendLog（v1.1.19：GUI 侧日志全部进文件），此处只进 UI
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
            // Worker 非正常退出（更新重启/崩溃/强杀）可能没机会还原停靠的窗口：兜底还原
            EfootballBot.Core.System.BackgroundParker.TryRestoreFromRecord();
            // 崩溃残留的禁用真实手柄同样兜底恢复（下次启动挂机会重新禁用）
            RestorePhysicalPadIfDisabled();
        });
        return _workerHost;
    }

    // ---------------- 开始 / 停止 ----------------

    // ---------------- 模块入口切换（中栏上层列表） ----------------

    /// <summary>国际服模块入口切换：★赛前快捷设置 / 我的联赛 / 周活动 / AI天梯联赛（内容区随之切换，互不同时显示）。</summary>
    private void RbPreMatch_Checked(object sender, RoutedEventArgs e) => SwitchIntlModule();

    private void RbMyLeague_Checked(object sender, RoutedEventArgs e) => SwitchIntlModule();

    private void RbWeekly_Checked(object sender, RoutedEventArgs e) => SwitchIntlModule();

    private void RbAiLadder_Checked(object sender, RoutedEventArgs e) => SwitchIntlModule();

    private void SwitchIntlModule()
    {
        // InitializeComponent 按 XAML 顺序建控件：Checked 触发时其余内容区可能尚未创建
        if (PreMatchContent is null || LeagueContent is null || WeeklyContent is null || AiLadderContent is null) return;
        PreMatchContent.Visibility = RbPreMatch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LeagueContent.Visibility = RbMyLeague.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        WeeklyContent.Visibility = RbWeekly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AiLadderContent.Visibility = RbAiLadder.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>国服模块入口切换（上层列表照抄国际服，分组独立：切服务器互不干扰选中态）。</summary>
    private void RbCnPreMatch_Checked(object sender, RoutedEventArgs e) => SwitchCnModule();

    private void RbCnLeague_Checked(object sender, RoutedEventArgs e) => SwitchCnModule();

    private void RbCnWeekly_Checked(object sender, RoutedEventArgs e) => SwitchCnModule();

    private void RbCnMyLeague_Checked(object sender, RoutedEventArgs e) => SwitchCnModule();

    private void SwitchCnModule()
    {
        if (CnPreMatchContent is null || CnLeagueContent is null || CnWeeklyContent is null
            || CnMyLeagueContent is null) return;
        CnPreMatchContent.Visibility = RbCnPreMatch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CnLeagueContent.Visibility = RbCnLeague.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CnWeeklyContent.Visibility = RbCnWeekly.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CnMyLeagueContent.Visibility = RbCnMyLeague.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 赛前快捷设置（一键打开游戏自带设置工具） ----------------

    private void BtnOpenGameSettings_Click(object sender, RoutedEventArgs e)
        => OpenGameSettingsTool(TxtPreMatchPath, intl: true, "国际服");

    private void BtnOpenCnGameSettings_Click(object sender, RoutedEventArgs e)
        => OpenGameSettingsTool(TxtCnPreMatchPath, intl: false, "国服");

    private void BtnOpenGameDir_Click(object sender, RoutedEventArgs e)
        => OpenGameDirectory(TxtPreMatchPath, intl: true, "国际服");

    private void BtnOpenCnGameDir_Click(object sender, RoutedEventArgs e)
        => OpenGameDirectory(TxtCnPreMatchPath, intl: false, "国服");

    private void BtnLaunchGame_Click(object sender, RoutedEventArgs e)
        => LaunchGame(TxtPreMatchPath, intl: true, "国际服");

    private void BtnLaunchCnGame_Click(object sender, RoutedEventArgs e)
        => LaunchGame(TxtCnPreMatchPath, intl: false, "国服");

    /// <summary>
    /// 快速启动游戏本体：已在运行只提示不重复拉起。国际服优先走 Steam 正规链路
    /// （steam://rungameid/1665460，与 Steam 库内快捷方式等效，Steam 未启动会自动带起，
    /// 不依赖安装位置），Steam 不在时回退定位到的 eFootball.exe 直启；国服定位
    /// eFootballOnline.exe 直启（定位链路与 Steam 严格隔离，见 GameSettingsLocator）。
    /// </summary>
    private void LaunchGame(TextBlock status, bool intl, string label)
    {
        try
        {
            string procName = intl ? GameWindow.GlobalProcessName : BotEngine.CnProcessName;
            if (Process.GetProcessesByName(procName).Length > 0)
            {
                status.Text = $"{label}游戏已在运行，无需重复启动。";
                AppendLog($"[赛前快捷设置] {label}游戏已在运行，未重复启动。", LogLevel.Info);
                return;
            }

            if (intl)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = $"steam://rungameid/{GameSettingsLocator.IntlSteamAppId}",
                        UseShellExecute = true,
                    });
                    status.Text = "已通过 Steam 启动国际服游戏（eFootball），窗口出现需等待片刻。";
                    AppendLog("[赛前快捷设置] 已通过 Steam 启动国际服游戏（eFootball）。", LogLevel.Success);
                    return;
                }
                catch
                {
                    // 无 Steam（URI 协议未注册）→ 回退定位 exe 直启
                }
            }

            // 定位游戏本体：上次定位的 Settings.exe 所在目录向上找（两服各自的路径记忆）
            var settingsPath = intl ? _cfg.GamePaths.IntlSettingsTool : _cfg.GamePaths.CnSettingsTool;
            string? exe = GameSettingsLocator.LocateGameExe(intl, Path.GetDirectoryName(settingsPath));
            if (exe is null)
            {
                status.Text = intl
                    ? "未定位到国际服游戏本体（eFootball.exe）——请确认已安装 Steam 版 eFootball。"
                    : "未找到国服游戏本体（eFootballOnline.exe）——请先点一次「打开设置工具」形成路径记忆（或启动国服游戏）后再试。";
                AppendLog($"[赛前快捷设置] 未定位到{label}游戏本体，未启动。", LogLevel.Warn);
                return;
            }

            status.Text = exe;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
            AppendLog($"[赛前快捷设置] 已启动{label}游戏：{exe}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            status.Text = $"启动失败：{ex.Message}";
            AppendLog($"[赛前快捷设置] 启动{label}游戏失败：{ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>
    /// 定位某服的 Settings.exe：运行中的游戏进程锚定 → 上次定位记忆（GamePaths）兜底，
    /// 成功后写入记忆。两服严格隔离：国服不走 Steam 回退——Steam 库里是国际服的安装，
    /// 国服游戏未启动时跌进去会打开国际服的设置工具（实机 2026-10-13 用户报告）。
    /// </summary>
    private string? LocateSettingsTool(bool intl)
    {
        var path = GameSettingsLocator.Locate(
            intl ? GameWindow.GlobalProcessName : BotEngine.CnProcessName,
            allowSteamFallback: intl);
        if (path is null)
        {
            var saved = intl ? _cfg.GamePaths.IntlSettingsTool : _cfg.GamePaths.CnSettingsTool;
            if (saved.Length > 0 && File.Exists(saved)) path = saved;
        }
        if (path is not null)
        {
            if (intl) _cfg.GamePaths.IntlSettingsTool = path;
            else _cfg.GamePaths.CnSettingsTool = path;
            _cfg.Save();
        }
        return path;
    }

    /// <summary>定位并启动游戏目录下的 Settings.exe，结果显示在模块内状态行并记日志。</summary>
    private void OpenGameSettingsTool(TextBlock status, bool intl, string label)
    {
        try
        {
            var exe = LocateSettingsTool(intl);
            if (exe is null)
            {
                status.Text = intl
                    ? "未找到 Settings.exe——请先启动国际服游戏（保持运行）再点一次，或到游戏安装目录手动打开。"
                    : "未找到国服 Settings.exe——请先启动国服游戏（保持运行）再点一次；定位成功一次后会记住路径，之后无需游戏在运行。";
                AppendLog($"[赛前快捷设置] 未定位到{label}游戏设置工具（Settings.exe）：请先启动游戏再试，或到游戏安装目录手动打开。", LogLevel.Warn);
                return;
            }
            status.Text = exe;
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            AppendLog($"[赛前快捷设置] 已打开{label}游戏设置工具：{exe}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            status.Text = $"打开失败：{ex.Message}";
            AppendLog($"[赛前快捷设置] 打开{label}设置工具失败：{ex.Message}", LogLevel.Error);
        }
    }

    /// <summary>在资源管理器中打开游戏本体安装目录（借 Settings.exe 定位）。</summary>
    private void OpenGameDirectory(TextBlock status, bool intl, string label)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LocateSettingsTool(intl));
            if (dir is null)
            {
                status.Text = $"未定位到{label}游戏安装目录——请先启动游戏再点一次。";
                AppendLog($"[赛前快捷设置] 未定位到{label}游戏安装目录：请先启动游戏再试。", LogLevel.Warn);
                return;
            }
            status.Text = dir;
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            AppendLog($"[赛前快捷设置] 已打开{label}游戏目录：{dir}", LogLevel.Success);
        }
        catch (Exception ex)
        {
            status.Text = $"打开失败：{ex.Message}";
            AppendLog($"[赛前快捷设置] 打开{label}游戏目录失败：{ex.Message}", LogLevel.Error);
        }
    }

    private async void BtnStartMyLeague_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartMyLeague, "intl");

    private async void BtnStartWeekly_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartWeekly, "intlweekly");

    private async void BtnStartAiLadder_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartAiLadder, "intlladder");

    private async void BtnStartCn_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartCn, "cndaily");

    private async void BtnStartCnWeekly_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartCnWeekly, "cnweekly");

    private async void BtnStartCnMyLeague_Click(object sender, RoutedEventArgs e)
        => await ToggleScenarioAsync(BtnStartCnMyLeague, "cnmyleague");

    private async Task ToggleScenarioAsync(Button btn, string? mode)
    {
        if (_starting) return;
        var host = GetOrCreateWorkerHost();

        if (host.IsRunning)
        {
            await StopScenarioAsync();
            return;
        }

        // 开始前必须手动确认的两点：
        // ① 真实手柄占用 XInput 玩家1 时会由挂机自动禁用释放（退出程序自动恢复）；
        // ② 全屏（尤其超宽）下定位与捕获均不稳定，720p 窗口模式是实测可靠配置。
        var confirm = System.Windows.MessageBox.Show(
            this,
            "开始前请确认两件事：\n\n" +
            "① 真实手柄可以保持连接\n若它占用玩家 1，挂机会自动禁用它释放玩家 1（首次需一次管理员授权），关闭本程序后手柄自动恢复。\n\n" +
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

            // 真实手柄占着玩家 1 而释放任务未就绪（未注册/指向旧路径）→ 现场补注册（一次 UAC）。
            // 放在点击开始时做：用户此刻最期待授权弹窗，也避免每次启动都打扰。
            await EnsurePadReleaseTasksAsync();

            // 立即反馈：Worker 冷启动握手最长 30s，期间按钮必须显示"启动中…"而非无响应
            btn.Content = "启动中…";
            AppendLog("正在启动 Worker（首次冷启动需解压，可能 10 秒以上）…", LogLevel.Info);
            await host.StartAsync(mode!);
            if (host.IsRunning)
            {
                _activeStartBtn = btn;
                btn.Style = (Style)FindResource("DangerBtn");
                btn.IsEnabled = true;
                BtnPause.IsEnabled = true;
                _paused = false;
                RefreshRunButtons(); // 按钮文字带热键可视化（停止/暂停）
                _statsTimer.Start();
                if (!_previewOn) BtnPreview_Click(btn, new RoutedEventArgs()); // 自动开预览

                // 引擎启动后注册全局热键（主键=暂停→停止，恢复键=继续）
                if (_cfg.Hotkey.Enabled)
                {
                    try
                    {
                        if (_hotkey.Register(_cfg.Hotkey))
                            AppendLog($"全局热键已注册：{_cfg.Hotkey.DisplayString()}（暂停/停止）、{_cfg.Hotkey.ResumeDisplayString()}（恢复）", LogLevel.Info);
                        else
                            AppendLog("全局热键注册失败。", LogLevel.Warn);
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
            // Worker 秒退/连不上时的高频原因提示（2026-10-13 用户案例：Worker 反复启动即死，
            // 日志只见会话头）。这类环境的日志文件里往往查不到细节，先给用户可自查的动作。
            if (detail.Contains("Worker 进程已退出") || detail.Contains("无法连接到 Worker"))
                AppendLog("排查建议：①把本程序加入杀毒软件/安全管家白名单（安全软件可能拦截 Worker 子进程）" +
                    "②右键 exe →属性→勾选「解除锁定」后确定 ③把整个程序文件夹解压到本地磁盘再运行" +
                    "（不要在微信/QQ 的下载目录里直接双击运行）④换一个磁盘目录（如 D:\\efootball-bot）重放一份再试", LogLevel.Warn);
            ResetButtons();
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// 「释放玩家 1」提权任务未就绪时的现场补注册：仅当真实手柄确实占着 slot 0 才弹 UAC
    /// （RunHidHideJobAsync 会启动 runas 子进程）。注册后由 Worker 在挂机启动时静默触发。
    /// </summary>
    private async Task EnsurePadReleaseTasksAsync()
    {
        try
        {
            bool padHoldingSlot0 = PadBackendProbe.IsSlot0Occupied()
                && HidDeviceEnumerator.FindPhysicalGamepadDevNodes().Count > 0;
            if (!padHoldingSlot0 || await Task.Run(HidHideManager.TasksReady))
                return;

            AppendLog("首次使用需注册「释放玩家 1」系统任务，请在授权窗口点「是」…", LogLevel.Info);
            var res = await RunHidHideJobAsync(new HidHideConfigJob.HidHideJobDto { Mode = "tasks" });
            AppendLog(res.Success
                ? "释放玩家 1 任务已就绪。"
                : $"释放玩家 1 任务注册失败：{res.Error}", res.Success ? LogLevel.Info : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            AppendLog($"释放任务就绪检查失败：{ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>触发恢复被挂机禁用的真实手柄（静默提权任务；无记录且无被禁节点时不做任何事）。</summary>
    private void RestorePhysicalPadIfDisabled()
    {
        try
        {
            if (PhysicalPadControl.HasPendingRestore() || PhysicalPadControl.HasDisabledGamepad())
                HidHideManager.RunTaskSilently(HidHideManager.PadRestoreTaskName);
        }
        catch { /* 恢复失败不影响退出流程，下次启动兜底 */ }
    }

    private void SetStartButtonsEnabled(bool enabled)
    {
        BtnStartMyLeague.IsEnabled = enabled;
        BtnStartWeekly.IsEnabled = enabled;
        BtnStartAiLadder.IsEnabled = enabled;
        BtnStartCn.IsEnabled = enabled;
        BtnStartCnWeekly.IsEnabled = enabled;
        BtnStartCnMyLeague.IsEnabled = enabled;
    }

    // 暂停状态（仅 GUI 侧镜像，真实状态在 Worker 引擎；停止时复位）
    private bool _paused;
    /// <summary>当前运行中的模块启动按钮（决定哪个显示"停止"）。</summary>
    private Button? _activeStartBtn;

    private async void BtnPause_Click(object sender, RoutedEventArgs e)
        => await TogglePauseAsync();

    private void ResetButtons()
    {
        BtnStartMyLeague.Style = (Style)FindResource("PrimaryBtn");
        BtnStartWeekly.Style = (Style)FindResource("PrimaryBtn");
        BtnStartWeekly.Content = "开始";
        BtnStartAiLadder.Content = "开始";
        BtnStartAiLadder.Style = (Style)FindResource("PrimaryBtn");
        BtnStartCn.Content = "开始";
        BtnStartCn.Style = (Style)FindResource("PrimaryBtn");
        BtnStartCnWeekly.Content = "开始";
        BtnStartCnWeekly.Style = (Style)FindResource("PrimaryBtn");
        BtnStartCnMyLeague.Content = "开始";
        BtnStartCnMyLeague.Style = (Style)FindResource("PrimaryBtn");
        SetStartButtonsEnabled(true);
        BtnPause.IsEnabled = false;
        _paused = false;
        _activeStartBtn = null;
        RefreshRunButtons();
        RbCn.IsEnabled = RbIntl.IsEnabled = true;
        _statsTimer.Stop();
    }

    private void UpdateStats()
    {
        var s = _workerHost?.LastStats;
        if (s is null) return;
        StMatches.Text = s.MatchesDone.ToString();
        StKey.Text = s.KeyMatches.ToString();
        StMaxed.Text = s.PlayersMaxed.ToString();
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
            _lastPreviewAt = DateTime.Now;
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
        // 先杀 Worker（虚拟手柄释放玩家 1 槽位），再恢复真实手柄——
        // 顺序反了会把真实手柄顶到玩家 2。恢复由提权任务在进程退出后自行完成。
        // ★必须先于停靠还原：Worker 里的停靠看门狗存活期间会把刚还原的窗口再移出屏幕，
        //   而还原记录已被删除，游戏就永远留在屏幕外（挂机中直接关主窗口的实测路径）
        _workerHost?.Dispose();
        // 停靠中的游戏窗口还原兜底：Worker 已死，看门狗不会再对抗还原
        EfootballBot.Core.System.BackgroundParker.TryRestoreFromRecord();
        RestorePhysicalPadIfDisabled();
        FileLogger.Instance.Dispose();
        base.OnClosing(e);
    }
}
