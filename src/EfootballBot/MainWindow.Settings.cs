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
        ChkAutoWindowize.IsChecked = _cfg.AutoWindowizeOnStart;

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
        _cfg.AutoWindowizeOnStart = ChkAutoWindowize.IsChecked == true;

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
        if (_engine is { IsRunning: true } && _cfg.Hotkey.Enabled)
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
        else if (_engine?.IsRunning != true)
        {
            // 未运行时先注销（等启动时再注册）
            _hotkey.Unregister();
        }
        RefreshHotkeyUi();

        // 文件日志配置变更 → 重新初始化
        FileLogger.Instance.Initialize(_cfg.FileLogEnabled, _cfg.FileLogLevel, _cfg.MaxLogFiles);

        // 自动更新（仓库地址已硬编码，不持久化）
        _cfg.Update.CheckOnStartup = ChkCheckOnStartup.IsChecked == true;
        _cfg.Update.IncludePrerelease = ChkIncludePrerelease.IsChecked == true;
        _cfg.Update.AutoDownloadInstall = ChkAutoDownloadInstall.IsChecked == true;

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
}
