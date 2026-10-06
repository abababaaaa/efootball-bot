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
    // ---------------- 配置 ----------------

    private void LoadConfigToUi()
    {
        ChkOnlyKey.IsChecked = _cfg.MyLeague.OnlyKeyMatches;
        ChkBuy4x.IsChecked = _cfg.MyLeague.Buy4xExp;
        ChkBuyCond.IsChecked = _cfg.MyLeague.BuyExcellentCondition;
        ChkBuyMgr.IsChecked = _cfg.MyLeague.BuyManagerBoost;
        TxtTarget.Text = _cfg.MyLeague.TargetMatches.ToString();
        TxtCnDailyLoop.Text = _cfg.Cn.DailyLoopLimit.ToString();
        foreach (ComboBoxItem item in CmbDifficulty.Items)
            if ((string)item.Content == _cfg.MyLeague.Difficulty) { CmbDifficulty.SelectedItem = item; break; }
        foreach (ComboBoxItem item in CmbWeeklyDiff.Items)
            if ((string)item.Content == _cfg.Weekly.Difficulty) { CmbWeeklyDiff.SelectedItem = item; break; }

        // 设置面板
        ChkAutoClose.IsChecked = _cfg.AutoCloseWhenDone;
        ChkMinimizeTray.IsChecked = false;
        ChkPeriodicGc.IsChecked = _cfg.PeriodicGc;

        // OCR 频率
        int[] ocrMsValues = { 500, 900, 1500, 2500, 5000 };
        for (int i = 0; i < ocrMsValues.Length; i++)
            if (_cfg.Timing.OcrIntervalMs == ocrMsValues[i]) { CmbOcrInterval.SelectedIndex = i; break; }

        // 预览间隔
        int[] previewMsValues = { 150, 300, 500, 1000, 2000 };
        for (int i = 0; i < previewMsValues.Length; i++)
            if (_cfg.PreviewIntervalMs == previewMsValues[i]) { CmbPreviewInterval.SelectedIndex = i; break; }

        // 挂机节奏：加载期间屏蔽 TextChanged，避免逐框赋值的中间态触发校验/保存
        _loadingConfig = true;
        TxtIdleMin.Text = FormatIdleSeconds(_cfg.Timing.IdleTapMinMs);
        TxtIdleMax.Text = FormatIdleSeconds(_cfg.Timing.IdleTapMaxMs);
        _loadingConfig = false;

        // 日志设置
        ChkFileLog.IsChecked = _cfg.FileLogEnabled;
        TxtMaxLogFiles.Text = _cfg.MaxLogFiles.ToString();
        TxtLogRetentionDays.Text = _cfg.LogRetentionDays.ToString();
        string[] levelOptions = { "Info", "Warn", "Error", "Debug" };
        for (int i = 0; i < levelOptions.Length; i++)
            if (_cfg.FileLogLevel.Equals(levelOptions[i], StringComparison.OrdinalIgnoreCase)) { CmbFileLogLevel.SelectedIndex = i; break; }

        ChkShowRuntimeLog.IsChecked = _cfg.ShowRuntimeLog;

        // 热键配置 → UI
        ChkHotkeyEnabled.IsChecked = _cfg.Hotkey.Enabled;
        ChkHotkeyCtrl.IsChecked = (_cfg.Hotkey.Modifiers & 2) != 0;
        ChkHotkeyShift.IsChecked = (_cfg.Hotkey.Modifiers & 4) != 0;
        ChkHotkeyAlt.IsChecked = (_cfg.Hotkey.Modifiers & 1) != 0;
        ChkHotkeyWin.IsChecked = (_cfg.Hotkey.Modifiers & 8) != 0;
        TxtHotkey.Text = _cfg.Hotkey.DisplayString().Split('+').LastOrDefault() ?? "?";

        // 自动更新
        TxtCurrentVersion.Text = "v" + Updater.CurrentVersion;
        TxtUpdateRepo.Text = Updater.DefaultRepository;
        ChkCheckOnStartup.IsChecked = _cfg.Update.CheckOnStartup;
        ChkIncludePrerelease.IsChecked = _cfg.Update.IncludePrerelease;
        ChkAutoDownloadInstall.IsChecked = _cfg.Update.AutoDownloadInstall;

        // 手柄隔离（赋值会触发 Changed：加载期间必须屏蔽，否则每次启动误弹一次提权配置）
        _loadingConfig = true;
        ChkHidHideEnabled.IsChecked = _cfg.HidHide.Enabled;
        ChkHidePhysical.IsChecked = _cfg.HidHide.HidePhysicalGamepad;
        _loadingConfig = false;
        _ = RefreshHidHidePanelAsync();
    }

    private void SaveUiToConfig()
    {
        _cfg.MyLeague.OnlyKeyMatches = ChkOnlyKey.IsChecked == true;
        _cfg.MyLeague.Buy4xExp = ChkBuy4x.IsChecked == true;
        _cfg.MyLeague.BuyExcellentCondition = ChkBuyCond.IsChecked == true;
        _cfg.MyLeague.BuyManagerBoost = ChkBuyMgr.IsChecked == true;
        if (int.TryParse(TxtTarget.Text, out int t) && t >= 0)
            _cfg.MyLeague.TargetMatches = t;
        if (int.TryParse(TxtCnDailyLoop.Text, out int cdl) && cdl >= 0)
            _cfg.Cn.DailyLoopLimit = cdl;
        if (CmbDifficulty.SelectedItem is ComboBoxItem d)
            _cfg.MyLeague.Difficulty = (string)d.Content;
        if (CmbWeeklyDiff.SelectedItem is ComboBoxItem wd)
            _cfg.Weekly.Difficulty = (string)wd.Content;

        // 设置面板
        _cfg.AutoCloseWhenDone = ChkAutoClose.IsChecked == true;
        _cfg.PeriodicGc = ChkPeriodicGc.IsChecked == true;

        if (CmbOcrInterval.SelectedIndex >= 0)
        {
            int[] ocrMs = { 500, 900, 1500, 2500, 5000 };
            _cfg.Timing.OcrIntervalMs = ocrMs[CmbOcrInterval.SelectedIndex];
        }
        if (CmbPreviewInterval.SelectedIndex >= 0)
        {
            int[] pvMs = { 150, 300, 500, 1000, 2000 };
            _cfg.PreviewIntervalMs = pvMs[CmbPreviewInterval.SelectedIndex];
            _previewTimer.Interval = TimeSpan.FromMilliseconds(_cfg.PreviewIntervalMs);
        }

        // 挂机节奏兜底（导航离开/点开始时）：成功随末尾统一保存，失败保留旧值
        ApplyIdleTap(persist: false);

        _cfg.FileLogEnabled = ChkFileLog.IsChecked == true;
        if (int.TryParse(TxtMaxLogFiles.Text, out int mf) && mf >= 1 && mf <= 50)
            _cfg.MaxLogFiles = mf;
        if (int.TryParse(TxtLogRetentionDays.Text, out int rd) && rd >= 1 && rd <= 365)
            _cfg.LogRetentionDays = rd;
        if (CmbFileLogLevel.SelectedItem is ComboBoxItem li)
            _cfg.FileLogLevel = (string)li.Content;

        _cfg.ShowRuntimeLog = ChkShowRuntimeLog.IsChecked == true;
        // 实时生效：如果当前在游戏模块，直接切换 LogCard 可见性
        if (_currentNav == NavMode.Game)
            LogCard.Visibility = _cfg.ShowRuntimeLog ? Visibility.Visible : Visibility.Collapsed;

        // 热键 UI → 配置
        _cfg.Hotkey.Enabled = ChkHotkeyEnabled.IsChecked == true;
        int mods = 0;
        if (ChkHotkeyAlt.IsChecked == true) mods |= 1;
        if (ChkHotkeyCtrl.IsChecked == true) mods |= 2;
        if (ChkHotkeyShift.IsChecked == true) mods |= 4;
        if (ChkHotkeyWin.IsChecked == true) mods |= 8;
        _cfg.Hotkey.Modifiers = mods;
        // VirtualKey 在按键捕获时已经直接更新到 _cfg.Hotkey.VirtualKey，这里不需额外处理

        // 如果引擎正在运行，尝试重新注册热键（新配置）
        if (_workerHost is { IsRunning: true } && _cfg.Hotkey.Enabled)
        {
            try
            {
                if (!_hotkey.Register(_cfg.Hotkey))
                    AppendLog("热键注册失败（可能被其他程序占用），已跳过。", LogLevel.Warn);
            }
            catch (Exception ex)
            {
                AppendLog($"热键注册异常：{ex.Message}", LogLevel.Warn);
            }
        }
        else if (_workerHost?.IsRunning != true)
        {
            // 未运行时先注销（等启动时再注册）
            _hotkey.Unregister();
        }
        RefreshHotkeyUi();

        // 文件日志配置变更 → 重新初始化
        FileLogger.Instance.Initialize(_cfg.FileLogEnabled, _cfg.FileLogLevel, _cfg.MaxLogFiles, _cfg.LogRetentionDays);

        // 自动更新（仓库地址已硬编码，不持久化）
        _cfg.Update.CheckOnStartup = ChkCheckOnStartup.IsChecked == true;
        _cfg.Update.IncludePrerelease = ChkIncludePrerelease.IsChecked == true;
        _cfg.Update.AutoDownloadInstall = ChkAutoDownloadInstall.IsChecked == true;

        // 手柄隔离（Enabled/GameImagePath/AppliedBlacklist 由开关提权动作维护，此处只同步复选框）
        _cfg.HidHide.HidePhysicalGamepad = ChkHidePhysical.IsChecked == true;

        _cfg.Save();
    }

    // ---------------- 挂机节奏 ----------------

    private bool _loadingConfig;

    /// <summary>毫秒转秒文本并去掉多余尾零：1000→"1"，500→"0.5"。</summary>
    private static string FormatIdleSeconds(int ms) =>
        (ms / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private void IdleTap_TextChanged(object sender, TextChangedEventArgs e)
    {
        // InitializeComponent 按 XAML 顺序建控件，TxtIdleError 在两个输入框之后；
        // 框架给 Text 赋默认值触发本事件时它可能尚未创建。加载赋值阶段由 _loadingConfig 屏蔽。
        if (TxtIdleError is null || _loadingConfig) return;
        ApplyIdleTap(persist: true);
    }

    /// <summary>
    /// 校验并应用挂机节奏。成功：写入配置、清除红框/错误；失败：红框+错误提示、保留旧值。
    /// persist=true（TextChanged）时立即保存；false（SaveUiToConfig）时交给调用方统一保存。
    /// </summary>
    private bool ApplyIdleTap(bool persist)
    {
        if (TimingConfig.TryParseSeconds(TxtIdleMin.Text, TxtIdleMax.Text,
                out int minMs, out int maxMs, out string error))
        {
            _cfg.Timing.IdleTapMinMs = minMs;
            _cfg.Timing.IdleTapMaxMs = maxMs;
            TxtIdleError.Visibility = Visibility.Collapsed;
            TxtIdleMin.ClearValue(Control.BorderBrushProperty);
            TxtIdleMax.ClearValue(Control.BorderBrushProperty);
            if (persist) _cfg.Save();
            return true;
        }

        TxtIdleError.Text = error;
        TxtIdleError.Visibility = Visibility.Visible;
        TxtIdleMin.BorderBrush = (System.Windows.Media.Brush)FindResource("Danger");
        TxtIdleMax.BorderBrush = (System.Windows.Media.Brush)FindResource("Danger");
        return false;
    }

    // ================= 自动更新事件 =================

    private UpdateInfo? _pendingUpdate;

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        BtnCheckUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = "正在检查…";
        ChangelogBox.Visibility = Visibility.Collapsed;
        try
        {
            var result = await Task.Run(() => Updater.CheckAsync(includePrerelease: _cfg.Update.IncludePrerelease, force: true));
            TxtUpdateStatus.Text = result.Message;
            BtnApplyUpdate.IsEnabled = result.IsAvailable;
            _pendingUpdate = result.Info;
            if (result.IsAvailable && result.Info is not null)
            {
                ShowUpdateBanner(result.Info);
                ShowChangelog(result.Info.Body);
            }
            else
            {
                HideUpdateBanner();
            }
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = $"检查失败：{ex.Message}";
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private void ShowChangelog(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        TxtChangelog.Text = body.Trim();
        ChangelogBox.Visibility = Visibility.Visible;
    }

    private void TxtUpdateRepo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"https://github.com/{Updater.DefaultRepository}",
                UseShellExecute = true
            });
        }
        catch { /* 忽略 */ }
    }

    private void BtnBannerUpdate_Click(object sender, RoutedEventArgs e)
    {
        // 先跳转到更新面板，再执行更新
        NavigateTo(NavMode.Update);
        BtnApplyUpdate_Click(sender, e);
    }

    private async void BtnApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        BtnCheckUpdate.IsEnabled = false;
        BtnApplyUpdate.IsEnabled = false;
        PbUpdate.Visibility = Visibility.Visible;
        PbUpdate.Value = 0;
        TxtUpdateStatus.Text = $"下载 v{_pendingUpdate.Version}…";

        var progress = new Progress<(long Bytes, long Total)>(p =>
        {
            if (p.Total > 0) PbUpdate.Maximum = p.Total;
            PbUpdate.Value = p.Bytes;
            TxtUpdateStatus.Text = $"下载中 {p.Bytes / 1024} KB / {p.Total / 1024} KB";
        });

        try
        {
            var result = await Task.Run(() => Updater.DownloadAndApplyAsync(_pendingUpdate, progress));
            TxtUpdateStatus.Text = result.Message;
            if (result.Applied)
            {
                AppendLog("更新已下载，启动时自动替换后重启。", LogLevel.Success);
                // 给用户 2 秒看到状态，然后退出当前进程让 PowerShell 脚本接管
                await Task.Delay(2000);
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = $"更新失败：{ex.Message}";
        }
        finally
        {
            PbUpdate.Visibility = Visibility.Collapsed;
            BtnCheckUpdate.IsEnabled = true;
            BtnApplyUpdate.IsEnabled = _pendingUpdate is not null;
        }
    }

    private async void BtnOptimizeGitHub_Click(object sender, RoutedEventArgs e)
    {
        BtnOptimizeGitHub.IsEnabled = false;
        PbGitHub.Visibility = Visibility.Visible;
        PbGitHub.IsIndeterminate = true;
        TxtGitHubStatus.Text = "正在优化 GitHub 直连（测速约 10~30 秒）…";

        try
        {
            if (!GitHostsOptimizer.IsAdministrator())
            {
                // 非管理员：UAC 提权重新运行自身离线执行，结果写临时文件后读回
                TxtGitHubStatus.Text = "需要管理员权限，请在弹出的授权窗口确认…";
                string report = Path.Combine(Path.GetTempPath(), $"ghopt_{Guid.NewGuid():N}.txt");
                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = $"--optimize-github --report \"{report}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                });

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 120 && !File.Exists(report))
                    await Task.Delay(500);

                if (File.Exists(report))
                {
                    TxtGitHubStatus.Text = File.ReadAllText(report);
                    AppendLog("GitHub 直连优化完成（管理员进程）。", LogLevel.Success);
                }
                else
                {
                    TxtGitHubStatus.Text = "优化未完成：可能已取消授权或超时。";
                }
                try { File.Delete(report); } catch { }
            }
            else
            {
                string result = await Task.Run(GitHostsOptimizer.Optimize);
                TxtGitHubStatus.Text = result;
                AppendLog("GitHub 直连优化完成。", LogLevel.Success);
            }
        }
        catch (Exception ex)
        {
            TxtGitHubStatus.Text = $"优化失败：{ex.Message}";
        }
        finally
        {
            PbGitHub.Visibility = Visibility.Collapsed;
            PbGitHub.IsIndeterminate = false;
            BtnOptimizeGitHub.IsEnabled = true;
        }
    }

    /// <summary>启动时自动检查（异步，不阻塞）。</summary>
    public async Task AutoCheckUpdateOnStartupAsync()
    {
        if (!_cfg.Update.CheckOnStartup) return;
        try
        {
            var result = await Task.Run(() => Updater.CheckAsync(includePrerelease: _cfg.Update.IncludePrerelease));
            if (result.IsAvailable && result.Info is not null)
            {
                _pendingUpdate = result.Info;
                Dispatcher.Invoke(() =>
                {
                    TxtUpdateStatus.Text = $"发现新版本 v{result.Info.Version}";
                    BtnApplyUpdate.IsEnabled = true;
                    ShowChangelog(result.Info.Body);
                    ShowUpdateBanner(result.Info);
                    AppendLog($"自动更新：发现 v{result.Info.Version}。", LogLevel.Info);

                    // 如果勾选了「自动下载安装」，直接开始下载替换
                    if (_cfg.Update.AutoDownloadInstall)
                    {
                        AppendLog("已开启自动下载安装，正在下载…", LogLevel.Info);
                        BtnApplyUpdate_Click(this, new RoutedEventArgs());
                    }
                });
            }
        }
        catch { /* 启动时自动检查失败不打扰用户 */ }
    }

    /// <summary>显示顶部更新提醒横幅。</summary>
    private void ShowUpdateBanner(UpdateInfo info)
    {
        TxtUpdateBannerTitle.Text = $"发现新版本 v{info.Version}";
        TxtUpdateBannerDesc.Text = $"当前 v{Updater.CurrentVersion} → v{info.Version}，点击右侧立即更新";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    /// <summary>隐藏更新横幅。</summary>
    private void HideUpdateBanner()
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    // ================= 手柄隔离（HidHide） =================

    private bool _hidHideBusy;

    private void ChkHidePhysical_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingConfig) return;
        _cfg.HidHide.HidePhysicalGamepad = ChkHidePhysical.IsChecked == true;
        _cfg.Save();
        // 不立即弹 UAC：下次启动挂机时由计划任务 sync 静默生效；需要立即生效可关再开主开关
        if (_cfg.HidHide.Enabled && TxtHidHideStatus is not null)
            TxtHidHideStatus.Text = ChkHidePhysical.IsChecked == true
                ? "真实手柄将在下次启动挂机时一并隐藏"
                : "真实手柄隐藏将在下次启动挂机时关闭";
    }

    /// <summary>查询 HidHide 状态并刷新开关下方状态行；默认开启且未就绪时自动触发一次提权配置。</summary>
    private async Task RefreshHidHidePanelAsync(bool autoSetup = true)
    {
        var status = await Task.Run(HidHideManager.QueryStatus);
        bool needSetup = false;
        await Dispatcher.InvokeAsync(() =>
        {
            if (!status.DriverInstalled)
            {
                TxtHidHideStatus.Text = _cfg.HidHide.Enabled
                    ? "待配置：驱动未安装，开启开关时自动下载安装"
                    : "驱动未安装";
                needSetup = _cfg.HidHide.Enabled;
            }
            else if (status.Error is not null)
            {
                TxtHidHideStatus.Text = $"服务未就绪：{status.Error}";
                needSetup = _cfg.HidHide.Enabled; // 服务停止/未激活，setup 会拉起
            }
            else if (_cfg.HidHide.Enabled)
            {
                // 设备离线（未挂机）时不打扰：Active 且已有快照即视为已配置，变化由计划任务 sync 兜底
                bool onlineUnhidden = status.VigemInstanceCount > 0 && !status.VigemHidden;
                TxtHidHideStatus.Text = (!status.Active || onlineUnhidden)
                    ? "待配置：尚未对当前虚拟手柄生效"
                    : "已开启：虚拟手柄仅游戏内可见";
                needSetup = !status.Active || onlineUnhidden;
            }
            else
            {
                TxtHidHideStatus.Text = "已关闭：虚拟手柄对所有应用可见";
            }
        });

        if (autoSetup && needSetup && !_hidHideBusy)
            _ = SetHidHideEnabledAsync(true, automatic: true);
    }

    /// <summary>
    /// 提权执行 job：启动 runas 子进程 → 日志记录 PID → 并行等待「子进程退出」与「结果文件」。
    /// 三类失败均返回带明确 Error 的结果：
    /// ① UAC 被否(1223)/被策略拦截(5)；② 子进程退出但未写回结果（疑崩溃，指引查日志）；
    /// ③ 300s 超时（疑卡在下载/安装）。
    /// </summary>
    private async Task<HidHideConfigJob.HidHideResultDto> RunHidHideJobAsync(HidHideConfigJob.HidHideJobDto job)
    {
        string jobPath = Path.Combine(Path.GetTempPath(), $"hidhide_{Guid.NewGuid():N}.json");
        File.WriteAllText(jobPath, System.Text.Json.JsonSerializer.Serialize(job));

        Process child;
        try
        {
            child = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = $"--hidhide-config \"{jobPath}\"",
                Verb = "runas",
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("Process.Start 返回 null");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1223 or 5)
        {
            AppendLog($"提权子进程未启动（Win32 {ex.NativeErrorCode}：" +
                (ex.NativeErrorCode == 1223 ? "UAC 被拒绝" : "访问被策略或安全软件拦截") + "）。", LogLevel.Warn);
            TryDelete(jobPath);
            return new HidHideConfigJob.HidHideResultDto
            {
                Success = false,
                Error = $"提权子进程未启动（Win32 {ex.NativeErrorCode}）。" +
                    "若本程序本身是「以管理员身份运行」，则不会弹窗、属正常现象，请直接观察是否静默配置成功",
            };
        }

        AppendLog($"提权配置子进程已启动（PID={child.Id}），等待其退出…", LogLevel.Info);

        HidHideConfigJob.HidHideResultDto? result = null;
        bool exited, resultWritten;
        exited = resultWritten = false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 300)
        {
            if (child.HasExited)
            {
                AppendLog($"提权配置子进程已退出（ExitCode={child.ExitCode}）。", LogLevel.Info);
                await Task.Delay(150); // 等结果文件刷盘
                exited = true;
                break;
            }
            try
            {
                string text = await File.ReadAllTextAsync(jobPath);
                if (text.Contains("\"Success\"")) { resultWritten = true; break; }
            }
            catch { /* 文件被占用：子进程正在写 */ }
            await Task.Delay(500);
        }

        bool timeout = !exited && !resultWritten;
        if (timeout && !child.HasExited)
        {
            AppendLog("提权配置子进程 300 秒未退出，本次配置超时。", LogLevel.Warn);
            try { child.Kill(false); } catch { /* 提权进程可能无权结束 */ }
        }

        try
        {
            result = System.Text.Json.JsonSerializer.Deserialize<HidHideConfigJob.HidHideResultDto>(
                await File.ReadAllTextAsync(jobPath));
        }
        catch (Exception ex)
        {
            string hint = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EfootballBot", "hidhide-config-error.log");
            AppendLog(exited
                ? $"子进程未写回有效结果，可能启动即崩溃，详见 {hint}（{ex.Message}）"
                : $"结果文件读取失败：{ex.Message}", LogLevel.Warn);
        }

        TryDelete(jobPath);
        child.Dispose();

        result ??= timeout
            ? new HidHideConfigJob.HidHideResultDto
            {
                Success = false,
                Error = "提权配置子进程超时（300 秒未退出），可能卡在下载 HidHide 或静默安装环节",
            }
            : new HidHideConfigJob.HidHideResultDto
            {
                Success = false,
                Error = "子进程已退出但未写回结果，可能启动即崩溃；" +
                    "请查看 %AppData%\\EfootballBot\\hidhide-config-error.log",
            };
        return result;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>主开关切换：on=自动装驱动+配置（setup），off=解除（clear）。</summary>
    private async Task SetHidHideEnabledAsync(bool on, bool automatic)
    {
        if (_hidHideBusy) return;
        _hidHideBusy = true;
        ChkHidHideEnabled.IsEnabled = false;
        try
        {
            TxtHidHideStatus.Text = GitHostsOptimizer.IsAdministrator()
                ? "已具备管理员权限，正在静默配置（不会弹出授权窗口）…"
                : on
                    ? (automatic ? "首次启用正在自动配置，请在授权窗口点「是」…" : "正在开启手柄隔离，请在授权窗口点「是」…")
                    : "正在关闭手柄隔离，请在授权窗口点「是」…";

            var job = new HidHideConfigJob.HidHideJobDto
            {
                Mode = on ? "setup" : "clear",
                HideVigem = true,
                HidePhysical = _cfg.HidHide.HidePhysicalGamepad,
                GameImagePath = _cfg.HidHide.GameImagePath,
            };

            // 拒权(1223)/拦截(5)/崩溃/超时均由 RunHidHideJobAsync 转成带 Error 的结果
            var result = await RunHidHideJobAsync(job);

            if (!result.Success)
            {
                _loadingConfig = true;
                ChkHidHideEnabled.IsChecked = !on;
                _loadingConfig = false;
                TxtHidHideStatus.Text = $"配置失败：{result.Error}";
                return;
            }

            _cfg.HidHide.Enabled = on;
            if (on)
            {
                if (!string.IsNullOrWhiteSpace(result.ResolvedGameImagePath))
                    _cfg.HidHide.GameImagePath = result.ResolvedGameImagePath;
                // job 只返回本次新增条目，快照须合并（幂等重跑新增为 0 时不能清空，否则 clear 无法回滚）
                _cfg.HidHide.AppliedBlacklist =
                    HidHideManager.MergeInto(_cfg.HidHide.AppliedBlacklist, result.AppliedBlacklist);
            }
            _cfg.Save();
            AppendLog(on
                ? (result.AppliedBlacklist.Count > 0
                    ? $"手柄隔离已开启（黑名单托管 {result.AppliedBlacklist.Count} 项）。"
                    : "手柄隔离已开启，配置已生效。")
                : "手柄隔离已关闭，虚拟手柄恢复全局可见。", LogLevel.Info);
            TxtHidHideStatus.Text = result.Message;
        }
        finally
        {
            ChkHidHideEnabled.IsEnabled = true;
            _hidHideBusy = false;
        }
    }

    private void ChkHidHideEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingConfig) return;
        bool on = ChkHidHideEnabled.IsChecked == true;
        _ = SetHidHideEnabledAsync(on, automatic: false);
    }
}
