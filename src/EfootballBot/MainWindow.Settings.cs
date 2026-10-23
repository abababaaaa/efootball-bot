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
    /// <summary>比赛难度选项（与游戏内「选择赛事等级」一一对应，顺序=游戏列表行序）。</summary>
    private static readonly string[] MyLeagueDifficulties =
        { "新手", "业余", "标准", "职业", "顶尖球员", "超级球星", "传奇" };

    /// <summary>场次计数停止固定档位（索引=下拉序，0=无限跑）；索引 6 = 自定义（读输入框）。</summary>
    private static readonly int[] StopAfterPresets = { 0, 1, 3, 5, 10, 20 };

    /// <summary>把计数值映射到下拉/自定义输入框（非预设值进自定义并回填数字）。</summary>
    private static void LoadStopAfterUi(ComboBox cmb, TextBox txt, int value)
    {
        int idx = Array.IndexOf(StopAfterPresets, value);
        if (idx >= 0)
        {
            cmb.SelectedIndex = idx;
            txt.Visibility = Visibility.Collapsed;
        }
        else
        {
            cmb.SelectedIndex = StopAfterPresets.Length; // 自定义
            txt.Text = value.ToString();
            txt.Visibility = Visibility.Visible;
        }
    }

    /// <summary>下拉+自定义输入框 → 计数值。自定义输入非法/非正数回退 0（无限）。</summary>
    private static int SaveStopAfterUi(ComboBox cmb, TextBox txt)
    {
        if (cmb.SelectedIndex >= 0 && cmb.SelectedIndex < StopAfterPresets.Length)
            return StopAfterPresets[cmb.SelectedIndex];
        return int.TryParse(txt.Text.Trim(), out int v) && v > 0 ? v : 0;
    }

    /// <summary>选「自定义」时显示手动输入框（两个下拉共用，按容器内位置配对）。
    /// 注意：XAML 解析给 SelectedIndex 赋初值时本事件即触发，此时同级控件可能尚未创建——
    /// 必须从 sender 自己的容器就地找配对输入框，引用 x:Name 字段会 NullReferenceException
    /// （实机 2026-10-11 v1.1.8：启动即崩，gui-error.log 可查）。</summary>
    private void CmbStopAfter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox cmb || cmb.Parent is not StackPanel panel) return;
        int i = panel.Children.IndexOf(cmb);
        for (int j = i + 1; j < panel.Children.Count; j++)
        {
            if (panel.Children[j] is TextBox txt)
            {
                txt.Visibility = cmb.SelectedIndex == StopAfterPresets.Length
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                return;
            }
        }
    }

    // ---------------- 配置 ----------------

    /// <summary>OCR 引擎下拉切换：Unlimited-OCR 才显示外部服务地址（其余引擎用不上）。</summary>
    private void CmbOcrEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PnlUnlimitedOcr is null) return; // XAML 初始化早期可能尚未建好
        PnlUnlimitedOcr.Visibility = CmbOcrEngine.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadConfigToUi()
    {
        // 设置面板
        ChkAutoClose.IsChecked = _cfg.AutoCloseWhenDone;
        ChkMinimizeTray.IsChecked = false;
        ChkFrameDump.IsChecked = _cfg.FrameDump.Enabled;
        int[] dumpMsValues = { 2000, 5000, 10000, 30000, 60000 };
        int dumpIdx = Array.IndexOf(dumpMsValues, _cfg.FrameDump.IntervalMs);
        CmbFrameDumpInterval.SelectedIndex = dumpIdx >= 0 ? dumpIdx : 2; // 缺省 10 秒

        // OCR 引擎（下拉项：0=SimdPaddle 默认 / 1=RapidV6 / 2=Unlimited-OCR 外部服务；
        // 旧配置 Windows 值已删引擎，一并落 SimdPaddle）
        CmbOcrEngine.SelectedIndex = _cfg.OcrEngine switch
        {
            OcrEngineKind.RapidV6 => 1,
            OcrEngineKind.UnlimitedOcr => 2,
            _ => 0,
        };
        TxtUnlimitedOcrEndpoint.Text = _cfg.UnlimitedOcr.Endpoint;
        PnlUnlimitedOcr.Visibility = CmbOcrEngine.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        // 全权接管（挂机禁用真实手柄）：开关见「手柄接管」卡
        ChkPadTakeover.IsChecked = _cfg.HidHide.HidePhysicalGamepad;

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

        // 防检测保护（★重要设定）
        ChkBackgroundParking.IsChecked = _cfg.BackgroundParking.Enabled;
        ChkHumanPacing.IsChecked = _cfg.Timing.HumanPacingEnabled;
        // 赛后活动任务（★重要设定，仅国际服）：默认关闭
        ChkPmAutoAdvance.IsChecked = _cfg.PostMatchActivity.AutoAdvanceWeeklyTasks;
        _loadingConfig = true;
        ChkScheduleEnabled.IsChecked = _cfg.Schedule.Enabled;
        TxtScheduleStart.Text = _cfg.Schedule.StartTime;
        TxtScheduleEnd.Text = _cfg.Schedule.EndTime;
        TxtScheduleCap.Text = _cfg.Schedule.DailyCapHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        TxtScheduleDrift.Text = _cfg.Schedule.DriftMinutes.ToString();
        _loadingConfig = false;
        TxtScheduleError.Visibility = Visibility.Collapsed;

        // 我的联赛道具
        ChkBuyExpBoost.IsChecked = _cfg.MyLeague.BuyExpBoost;
        string[] expLevels = { "2x", "3x", "4x" };
        int expIdx = Array.IndexOf(expLevels, _cfg.MyLeague.ExpBoostLevel);
        CmbExpBoostLevel.SelectedIndex = expIdx >= 0 ? expIdx : 0;
        ChkBuyCoachBoost.IsChecked = _cfg.MyLeague.BuyCoachBoost;
        ChkBuyConditionBoost.IsChecked = _cfg.MyLeague.BuyConditionBoost;
        string[] condLevels = { "普通+", "好+", "杰出" };
        int condIdx = Array.IndexOf(condLevels, _cfg.MyLeague.ConditionLevel);
        CmbConditionLevel.SelectedIndex = condIdx >= 0 ? condIdx : 0;
        int diffIdx = Array.IndexOf(MyLeagueDifficulties, _cfg.MyLeague.Difficulty);
        CmbDifficulty.SelectedIndex = diffIdx >= 0 ? diffIdx : 0;
        ChkMaxLevelNotify.IsChecked = _cfg.MyLeague.NotifyPlayerMaxLevel;

        // 场次计数停止（0=无限；预设 1/3/5/10/20，其他值进自定义输入框）
        LoadStopAfterUi(CmbStopAfterMatches, TxtStopAfterMatches, _cfg.MyLeague.StopAfterMatches);
        LoadStopAfterUi(CmbStopAfterKeyMatches, TxtStopAfterKeyMatches, _cfg.MyLeague.StopAfterKeyMatches);

        // 周活动（国际服）：赛事等级默认传奇（索引 6）；场次停止同款档位
        int weeklyLevelIdx = Array.IndexOf(MyLeagueDifficulties, _cfg.Weekly.EventLevel);
        CmbWeeklyLevel.SelectedIndex = weeklyLevelIdx >= 0 ? weeklyLevelIdx : 6;
        LoadStopAfterUi(CmbWeeklyStopAfter, TxtWeeklyStopAfter, _cfg.Weekly.StopAfterMatches);

        // 周活动（国服）：难度（默认标准，索引 2）+ 场次停止
        int cnWeeklyDiffIdx = Array.IndexOf(MyLeagueDifficulties, _cfg.CnWeekly.Difficulty);
        CmbCnWeeklyDifficulty.SelectedIndex = cnWeeklyDiffIdx >= 0 ? cnWeeklyDiffIdx : 2;
        LoadStopAfterUi(CmbCnWeeklyStopAfter, TxtCnWeeklyStopAfter, _cfg.CnWeekly.StopAfterMatches);

        // 我的联赛（国服）：难度/道具/满级提示/场次停止（独立配置段，与国际服互不影响）
        ChkCnMlBuyExpBoost.IsChecked = _cfg.CnMyLeague.BuyExpBoost;
        int cnMlExpIdx = Array.IndexOf(new[] { "2x", "3x", "4x" }, _cfg.CnMyLeague.ExpBoostLevel);
        CmbCnMlExpBoostLevel.SelectedIndex = cnMlExpIdx >= 0 ? cnMlExpIdx : 0;
        ChkCnMlBuyCoachBoost.IsChecked = _cfg.CnMyLeague.BuyCoachBoost;
        ChkCnMlBuyConditionBoost.IsChecked = _cfg.CnMyLeague.BuyConditionBoost;
        int cnMlCondIdx = Array.IndexOf(new[] { "普通+", "好+", "杰出" }, _cfg.CnMyLeague.ConditionLevel);
        CmbCnMlConditionLevel.SelectedIndex = cnMlCondIdx >= 0 ? cnMlCondIdx : 0;
        int cnMlDiffIdx = Array.IndexOf(MyLeagueDifficulties, _cfg.CnMyLeague.Difficulty);
        CmbCnMlDifficulty.SelectedIndex = cnMlDiffIdx >= 0 ? cnMlDiffIdx : 0;
        ChkCnMlMaxLevelNotify.IsChecked = _cfg.CnMyLeague.NotifyPlayerMaxLevel;
        LoadStopAfterUi(CmbCnMlStopAfterMatches, TxtCnMlStopAfterMatches, _cfg.CnMyLeague.StopAfterMatches);
        LoadStopAfterUi(CmbCnMlStopAfterKeyMatches, TxtCnMlStopAfterKeyMatches, _cfg.CnMyLeague.StopAfterKeyMatches);

        // AI天梯联赛（国际服）：达到组别停止（索引 0=不启用，1~10 对应 组别10~组别1）+ 场次停止
        int aiDiv = _cfg.AiLadder.StopAtDivision;
        CmbAiStopAtDivision.SelectedIndex = aiDiv is >= 1 and <= 10 ? 11 - aiDiv : 0;
        LoadStopAfterUi(CmbAiStopAfterMatches, TxtAiStopAfterMatches, _cfg.AiLadder.StopAfterMatches);

        // 日志设置
        ChkFileLog.IsChecked = _cfg.FileLogEnabled;
        TxtMaxLogFiles.Text = _cfg.MaxLogFiles.ToString();
        TxtLogRetentionDays.Text = _cfg.LogRetentionDays.ToString();
        string[] levelOptions = { "Info", "Warn", "Error", "Debug" };
        for (int i = 0; i < levelOptions.Length; i++)
            if (_cfg.FileLogLevel.Equals(levelOptions[i], StringComparison.OrdinalIgnoreCase)) { CmbFileLogLevel.SelectedIndex = i; break; }

        ChkShowRuntimeLog.IsChecked = _cfg.ShowRuntimeLog;

        // 通知 / 监控 / 影子模式
        TxtWebhookUrl.Text = _cfg.Notify.WebhookUrl;
        TxtNotifyName.Text = _cfg.Notify.InstanceName;
        ChkNotifyStop.IsChecked = _cfg.Notify.OnStop;
        ChkNotifyError.IsChecked = _cfg.Notify.OnError;
        ChkMonitor.IsChecked = _cfg.Monitor.Enabled;
        TxtMonitorPort.Text = _cfg.Monitor.Port.ToString();
        TxtMonitorToken.Text = _cfg.Monitor.Token;
        ChkDryRun.IsChecked = _cfg.DryRun;

        // 运行日志显示过滤档（仅 UI 显示，文件日志不受影响）
        _runtimeLogFilter = _cfg.RuntimeLogFilter is "Key" or "Warn" ? _cfg.RuntimeLogFilter : "All";
        _loadingConfig = true;
        CmbRuntimeLogFilter.SelectedIndex = _runtimeLogFilter switch { "Key" => 1, "Warn" => 2, _ => 0 };
        // 日志历史过滤档（与运行日志同款三档，持久化）
        _historyLogFilter = _cfg.HistoryLogFilter is "Key" or "Warn" ? _cfg.HistoryLogFilter : "All";
        CmbHistoryLogFilter.SelectedIndex = _historyLogFilter switch { "Key" => 1, "Warn" => 2, _ => 0 };
        _loadingConfig = false;

        // 热键（记录式配置，加载时只同步开关，键位显示由 RefreshHotkeyUi 负责）
        _loadingConfig = true;
        ChkHotkeyEnabled.IsChecked = _cfg.Hotkey.Enabled;
        _loadingConfig = false;

        // 自动更新
        TxtCurrentVersion.Text = "v" + Updater.FormatVersion(Updater.CurrentVersion);
        TxtUpdateRepo.Text = Updater.DefaultRepository;
        ChkCheckOnStartup.IsChecked = _cfg.Update.CheckOnStartup;
        ChkIncludePrerelease.IsChecked = _cfg.Update.IncludePrerelease;
        ChkAutoDownloadInstall.IsChecked = _cfg.Update.AutoDownloadInstall;

        // 模块回退（更新页卡片，下次开始挂机生效）
        ChkFbSharpDxCapture.IsChecked = _cfg.ModuleFallback.UseSharpDxCapture;
        ChkFbFp32Ocr.IsChecked = _cfg.ModuleFallback.UseFp32Ocr;
        ChkFbNoOcrDownscale.IsChecked = _cfg.ModuleFallback.DisableOcrDownscale;
        ChkFbNoMouseTrajectory.IsChecked = _cfg.ModuleFallback.DisableMouseTrajectory;
        ChkFbNoFailureDumps.IsChecked = _cfg.ModuleFallback.DisableFailureDumps;

        // 手柄接管（赋值会触发 Changed：加载期间必须屏蔽）
        _loadingConfig = true;
        ChkPadTakeover.IsChecked = _cfg.HidHide.HidePhysicalGamepad;
        _loadingConfig = false;
    }

    private void SaveUiToConfig()
    {
        // 设置面板
        _cfg.AutoCloseWhenDone = ChkAutoClose.IsChecked == true;

        // 通知 / 监控 / 影子模式
        _cfg.Notify.WebhookUrl = TxtWebhookUrl.Text.Trim();
        _cfg.Notify.InstanceName = TxtNotifyName.Text.Trim();
        _cfg.Notify.OnStop = ChkNotifyStop.IsChecked == true;
        _cfg.Notify.OnError = ChkNotifyError.IsChecked == true;
        bool monitorWasEnabled = _cfg.Monitor.Enabled;
        _cfg.Monitor.Enabled = ChkMonitor.IsChecked == true;
        if (int.TryParse(TxtMonitorPort.Text.Trim(), out int monPort) && monPort is > 0 and < 65536)
            _cfg.Monitor.Port = monPort;
        _cfg.Monitor.Token = TxtMonitorToken.Text.Trim();
        _cfg.DryRun = ChkDryRun.IsChecked == true;
        // 监控页开关变更：即时生效（重启监听）；其余字段重启程序后生效
        if (_cfg.Monitor.Enabled != monitorWasEnabled)
        {
            try { _monitor?.Dispose(); } catch { }
            _monitor = null;
            if (_cfg.Monitor.Enabled) StartMonitorIfEnabled();
            else AppendLog("局域网监控页已关闭。", LogLevel.Info);
        }

        _cfg.FrameDump.Enabled = ChkFrameDump.IsChecked == true;
        if (CmbFrameDumpInterval.SelectedIndex >= 0)
        {
            int[] dumpMs = { 2000, 5000, 10000, 30000, 60000 };
            _cfg.FrameDump.IntervalMs = dumpMs[CmbFrameDumpInterval.SelectedIndex];
        }
        // 下拉项 → 引擎（0=SimdPaddle 默认 / 1=RapidV6-small / 2=Unlimited-OCR 外部服务）
        _cfg.OcrEngine = CmbOcrEngine.SelectedIndex switch
        {
            1 => OcrEngineKind.RapidV6,
            2 => OcrEngineKind.UnlimitedOcr,
            _ => OcrEngineKind.SimdPaddle,
        };
        string endpoint = TxtUnlimitedOcrEndpoint.Text.Trim();
        if (endpoint.Length > 0) _cfg.UnlimitedOcr.Endpoint = endpoint;

        // 全权接管（下次开始挂机生效；停止挂机自动恢复真实手柄）
        _cfg.HidHide.HidePhysicalGamepad = ChkPadTakeover.IsChecked == true;

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

        // 防检测保护（★重要设定）
        _cfg.BackgroundParking.Enabled = ChkBackgroundParking.IsChecked == true;
        _cfg.Timing.HumanPacingEnabled = ChkHumanPacing.IsChecked == true;
        _cfg.Schedule.Enabled = ChkScheduleEnabled.IsChecked == true;
        ApplySchedule(persist: false);
        _cfg.PostMatchActivity.AutoAdvanceWeeklyTasks = ChkPmAutoAdvance.IsChecked == true;

        // 我的联赛道具
        _cfg.MyLeague.BuyExpBoost = ChkBuyExpBoost.IsChecked == true;
        if (CmbExpBoostLevel.SelectedIndex >= 0)
            _cfg.MyLeague.ExpBoostLevel = new[] { "2x", "3x", "4x" }[CmbExpBoostLevel.SelectedIndex];
        _cfg.MyLeague.BuyCoachBoost = ChkBuyCoachBoost.IsChecked == true;
        _cfg.MyLeague.BuyConditionBoost = ChkBuyConditionBoost.IsChecked == true;
        if (CmbConditionLevel.SelectedIndex >= 0)
            _cfg.MyLeague.ConditionLevel = new[] { "普通+", "好+", "杰出" }[CmbConditionLevel.SelectedIndex];
        if (CmbDifficulty.SelectedIndex >= 0)
            _cfg.MyLeague.Difficulty = MyLeagueDifficulties[CmbDifficulty.SelectedIndex];
        _cfg.MyLeague.NotifyPlayerMaxLevel = ChkMaxLevelNotify.IsChecked == true;
        _cfg.MyLeague.StopAfterMatches = SaveStopAfterUi(CmbStopAfterMatches, TxtStopAfterMatches);
        _cfg.MyLeague.StopAfterKeyMatches = SaveStopAfterUi(CmbStopAfterKeyMatches, TxtStopAfterKeyMatches);

        // 周活动（国际服）
        if (CmbWeeklyLevel.SelectedIndex >= 0)
            _cfg.Weekly.EventLevel = MyLeagueDifficulties[CmbWeeklyLevel.SelectedIndex];
        _cfg.Weekly.StopAfterMatches = SaveStopAfterUi(CmbWeeklyStopAfter, TxtWeeklyStopAfter);

        // 周活动（国服）
        if (CmbCnWeeklyDifficulty.SelectedIndex >= 0)
            _cfg.CnWeekly.Difficulty = MyLeagueDifficulties[CmbCnWeeklyDifficulty.SelectedIndex];
        _cfg.CnWeekly.StopAfterMatches = SaveStopAfterUi(CmbCnWeeklyStopAfter, TxtCnWeeklyStopAfter);

        // 我的联赛（国服）
        _cfg.CnMyLeague.BuyExpBoost = ChkCnMlBuyExpBoost.IsChecked == true;
        if (CmbCnMlExpBoostLevel.SelectedIndex >= 0)
            _cfg.CnMyLeague.ExpBoostLevel = new[] { "2x", "3x", "4x" }[CmbCnMlExpBoostLevel.SelectedIndex];
        _cfg.CnMyLeague.BuyCoachBoost = ChkCnMlBuyCoachBoost.IsChecked == true;
        _cfg.CnMyLeague.BuyConditionBoost = ChkCnMlBuyConditionBoost.IsChecked == true;
        if (CmbCnMlConditionLevel.SelectedIndex >= 0)
            _cfg.CnMyLeague.ConditionLevel = new[] { "普通+", "好+", "杰出" }[CmbCnMlConditionLevel.SelectedIndex];
        if (CmbCnMlDifficulty.SelectedIndex >= 0)
            _cfg.CnMyLeague.Difficulty = MyLeagueDifficulties[CmbCnMlDifficulty.SelectedIndex];
        _cfg.CnMyLeague.NotifyPlayerMaxLevel = ChkCnMlMaxLevelNotify.IsChecked == true;
        _cfg.CnMyLeague.StopAfterMatches = SaveStopAfterUi(CmbCnMlStopAfterMatches, TxtCnMlStopAfterMatches);
        _cfg.CnMyLeague.StopAfterKeyMatches = SaveStopAfterUi(CmbCnMlStopAfterKeyMatches, TxtCnMlStopAfterKeyMatches);

        // AI天梯联赛（国际服）：索引 1~10 对应 组别10~组别1（10 最低、1 最高）
        _cfg.AiLadder.StopAtDivision = CmbAiStopAtDivision.SelectedIndex is >= 1 and <= 10
            ? 11 - CmbAiStopAtDivision.SelectedIndex
            : 0;
        _cfg.AiLadder.StopAfterMatches = SaveStopAfterUi(CmbAiStopAfterMatches, TxtAiStopAfterMatches);

        // 文件日志配置：只有真变了才重新初始化。SaveUiToConfig 在每次点开始时都会执行，
        // 无条件 Initialize 会新建会话文件，把"应用启动/主界面就绪"留在上一个文件里
        // （用户回传的"日志只有一行"半数由此而来，v1.1.19 修复）
        bool newFileLogEnabled = ChkFileLog.IsChecked == true;
        int newMaxLogFiles = int.TryParse(TxtMaxLogFiles.Text, out int mf) && mf >= 1 && mf <= 50 ? mf : _cfg.MaxLogFiles;
        int newRetention = int.TryParse(TxtLogRetentionDays.Text, out int rd) && rd >= 1 && rd <= 365 ? rd : _cfg.LogRetentionDays;
        string newFileLogLevel = CmbFileLogLevel.SelectedItem is ComboBoxItem li ? (string)li.Content : _cfg.FileLogLevel;
        bool logCfgChanged = newFileLogEnabled != _cfg.FileLogEnabled
            || newMaxLogFiles != _cfg.MaxLogFiles
            || newRetention != _cfg.LogRetentionDays
            || newFileLogLevel != _cfg.FileLogLevel;
        _cfg.FileLogEnabled = newFileLogEnabled;
        _cfg.MaxLogFiles = newMaxLogFiles;
        _cfg.LogRetentionDays = newRetention;
        _cfg.FileLogLevel = newFileLogLevel;

        _cfg.ShowRuntimeLog = ChkShowRuntimeLog.IsChecked == true;
        // 实时生效：如果当前在游戏模块，直接切换 LogCard 可见性
        if (_currentNav == NavMode.Game)
            LogCard.Visibility = _cfg.ShowRuntimeLog ? Visibility.Visible : Visibility.Collapsed;

        // 热键：键位由记录弹窗直接写配置并保存，这里只同步启用开关
        _cfg.Hotkey.Enabled = ChkHotkeyEnabled.IsChecked == true;

        // 文件日志配置变更 → 重新初始化（仅当上面检测到真变更）
        if (logCfgChanged)
            FileLogger.Instance.Initialize(_cfg.FileLogEnabled, _cfg.FileLogLevel, _cfg.MaxLogFiles, _cfg.LogRetentionDays);

        // 自动更新（仓库地址已硬编码，不持久化）
        _cfg.Update.CheckOnStartup = ChkCheckOnStartup.IsChecked == true;
        _cfg.Update.IncludePrerelease = ChkIncludePrerelease.IsChecked == true;
        _cfg.Update.AutoDownloadInstall = ChkAutoDownloadInstall.IsChecked == true;

        // 模块回退
        _cfg.ModuleFallback.UseSharpDxCapture = ChkFbSharpDxCapture.IsChecked == true;
        _cfg.ModuleFallback.UseFp32Ocr = ChkFbFp32Ocr.IsChecked == true;
        _cfg.ModuleFallback.DisableOcrDownscale = ChkFbNoOcrDownscale.IsChecked == true;
        _cfg.ModuleFallback.DisableMouseTrajectory = ChkFbNoMouseTrajectory.IsChecked == true;
        _cfg.ModuleFallback.DisableFailureDumps = ChkFbNoFailureDumps.IsChecked == true;

        // 手柄接管：DisabledPadDevs 恢复清单保存前从磁盘回读，避免 GUI 陈旧快照把
        // 尚未消费的遗留记录清掉（LegacyPadRestore 靠它恢复旧版本卡禁用的手柄）
        try { _cfg.HidHide.DisabledPadDevs = AppConfig.Load().HidHide.DisabledPadDevs; } catch { }

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

    // ---------------- 防检测保护（★重要设定） ----------------

    /// <summary>作息调度字段变更（TextChanged）：校验并立即保存，失败保留旧值并红框提示。</summary>
    private void ScheduleField_TextChanged(object sender, TextChangedEventArgs e)
    {
        // InitializeComponent 按 XAML 顺序建控件，错误提示框在输入框之后，加载期可能尚未创建
        if (TxtScheduleError is null || _loadingConfig) return;
        ApplySchedule(persist: true);
    }

    /// <summary>
    /// 校验并应用作息调度字段：时段 HH:mm（支持跨午夜）、每日上限 0~24 小时（0=不限）、
    /// 启停漂移 0~120 分钟。成功写入配置；失败红框+错误提示、保留旧值。
    /// </summary>
    private bool ApplySchedule(bool persist)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        // 拆开解析再合并：&& 短路会让编译器无法确定 out 变量已赋值。
        // 时段兼容 9:30 / 09:30 / 9:05 等写法，保存时统一归一化为 HH:mm。
        string[] timeFormats = { @"hh\:mm", @"h\:mm", @"h\:m" };
        bool okStart = TimeSpan.TryParseExact(TxtScheduleStart.Text.Trim(), timeFormats, null, out var start);
        bool okEnd = TimeSpan.TryParseExact(TxtScheduleEnd.Text.Trim(), timeFormats, null, out var end);
        bool okCap = double.TryParse(TxtScheduleCap.Text.Trim(), System.Globalization.NumberStyles.Float, ci, out double cap)
                     && cap is >= 0 and <= 24;
        bool okDrift = int.TryParse(TxtScheduleDrift.Text.Trim(), out int drift) && drift is >= 0 and <= 120;
        bool ok = okStart && okEnd && okCap && okDrift;

        if (!ok)
        {
            TxtScheduleError.Text = "格式无效：时段需为 HH:mm（如 09:30 / 22:00），上限 0~24 小时，漂移 0~120 分钟";
            TxtScheduleError.Visibility = Visibility.Visible;
            return false;
        }

        TxtScheduleError.Visibility = Visibility.Collapsed;
        _cfg.Schedule.StartTime = start.ToString(@"hh\:mm");
        _cfg.Schedule.EndTime = end.ToString(@"hh\:mm");
        _cfg.Schedule.DailyCapHours = cap;
        _cfg.Schedule.DriftMinutes = drift;
        if (persist) _cfg.Save();
        return true;
    }

    // ---------------- 设置页一级分类切换 ----------------

    /// <summary>五个分类模块（仿游戏内活动入口）切换二级页可见性。</summary>
    private void SettingsTab_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 按 XAML 顺序建控件：首个 RadioButton 的 Checked 触发时其余元素可能尚未创建
        if (SetTabImportant is null || SetTabDisplay is null || SetTabInput is null
            || SetTabDaily is null || SetTabDev is null) return;

        SetTabImportant.Visibility = RbSetImportant.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetTabDisplay.Visibility = RbSetDisplay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetTabInput.Visibility = RbSetInput.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetTabDaily.Visibility = RbSetDaily.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SetTabDev.Visibility = RbSetDev.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- 训练数据采集（存帧） ----------------

    /// <summary>打开 exe 同级 captures\ 目录（不存在则先创建，便于空目录也能打开）。</summary>
    private void BtnOpenCaptures_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(Core.System.AppPaths.ExeDir, "captures");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开存帧目录失败：{ex.Message}", LogLevel.Warn);
        }
    }

    // ================= 一键诊断（原 DiagReport.bat 内置版） =================

    /// <summary>诊断报告输出根目录：data\DiagReport\（数据全部收在程序目录内）。</summary>
    private static string DiagRoot => Path.Combine(Core.System.AppPaths.DataDir, "DiagReport");

    private bool _diagBusy;

    private async void BtnDiagReport_Click(object sender, RoutedEventArgs e)
    {
        if (_diagBusy) return;
        _diagBusy = true;
        BtnDiagReport.IsEnabled = false;
        TxtDiagStatus.Text = "正在收集系统信息…（约 1~2 分钟，请勿关闭程序）";
        var progress = new Progress<string>(s => TxtDiagStatus.Text = s);
        try
        {
            string note = TxtDiagNote.Text;
            string result = await Task.Run(() => Core.System.DiagReporter.CollectAsync(
                s => ((IProgress<string>)progress).Report(s), userNote: note));
            TxtDiagStatus.Text = $"完成：{result}";
            AppendLog($"诊断报告已生成：{result}", LogLevel.Info);
            OpenDiagResult(result);
        }
        catch (Exception ex)
        {
            TxtDiagStatus.Text = $"生成失败：{ex.Message}";
            AppendLog($"生成诊断报告失败：{ex}", LogLevel.Error);
        }
        finally
        {
            _diagBusy = false;
            BtnDiagReport.IsEnabled = true;
        }
    }

    private void BtnOpenDiagFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagRoot);
            Process.Start(new ProcessStartInfo { FileName = DiagRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开诊断目录失败：{ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>在资源管理器中定位生成的 zip（打包失败返回目录时直接打开该目录）。</summary>
    private static void OpenDiagResult(string result)
    {
        try
        {
            if (File.Exists(result))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo { FileName = result, UseShellExecute = true });
        }
        catch { /* 打不开不提示，路径已在状态栏显示 */ }
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
            AppendLog($"检查更新：{result.Message}", result.IsError ? LogLevel.Warn : LogLevel.Info);
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
            // 记录下载来源域名（Gitee/GitHub）与体积——国内下载慢/失败时反馈包里可据此定位
            try
            {
                string host = new Uri(_pendingUpdate.DownloadUrls[0]).Host;
                AppendLog($"开始下载 v{_pendingUpdate.Version}（来源 {host}，约 {_pendingUpdate.TotalBytes / 1024 / 1024} MB）", LogLevel.Info);
            }
            catch { }
            var result = await Task.Run(() => UpdateApplier.ApplyAsync(_pendingUpdate, progress));
            TxtUpdateStatus.Text = result.Message;
            AppendLog($"应用更新：{result.Message}", result.IsError ? LogLevel.Error : LogLevel.Success);
            if (result.Applied)
            {
                AppendLog("新版本已启动，本窗口即将退出（旧版本已自动备份，可回退）。", LogLevel.Success);
                // 给用户 2 秒看到状态，然后优雅退出（存配置→停 Worker→释放单实例锁），
                // 已启动的新实例等锁接管
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
                string report = Path.Combine(AppPaths.TempDir, $"ghopt_{Guid.NewGuid():N}.txt");
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
        TxtUpdateBannerDesc.Text = $"当前 v{Updater.FormatVersion(Updater.CurrentVersion)} → v{info.Version}，点击右侧立即更新";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    /// <summary>隐藏更新横幅。</summary>
    private void HideUpdateBanner()
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    // ================= 手柄接管 =================

    private void ChkPadTakeover_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingConfig) return;
        _cfg.HidHide.HidePhysicalGamepad = ChkPadTakeover.IsChecked == true;
        _cfg.Save();
    }

    // ================= 手柄驱动自检 =================

    private EfootballBot.Core.System.PadCheckResult? _lastPadCheck;

    private void BtnPadSelfCheck_Click(object sender, RoutedEventArgs e) => RunPadSelfCheck();

    private void RunPadSelfCheck()
    {
        var r = EfootballBot.Core.System.PadSelfCheck.Run();
        ShowPadCheck(r);
        AppendLog($"[手柄自检] {r.Title}：{r.Detail}",
            r.Kind == EfootballBot.Core.System.PadCheckKind.Ready ? LogLevel.Success : LogLevel.Warn);
    }

    private async void BtnPadSelfFix_Click(object sender, RoutedEventArgs e)
    {
        BtnPadSelfFix.IsEnabled = false;
        try
        {
            var r = await EfootballBot.Core.System.PadSelfCheck.TryFixAsync(
                m => AppendLog($"[手柄自检] {m}", LogLevel.Info));
            ShowPadCheck(r);
            AppendLog(r.Kind == EfootballBot.Core.System.PadCheckKind.Ready
                    ? $"[手柄自检] 修复完成：{r.Title}"
                    : $"[手柄自检] 修复后仍未就绪：{r.Title}——{r.SuggestedFix}",
                r.Kind == EfootballBot.Core.System.PadCheckKind.Ready ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            AppendLog($"[手柄自检] 修复过程出错：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            BtnPadSelfFix.IsEnabled = true;
        }
    }

    private void BtnPadSelfCheckReport_Click(object sender, RoutedEventArgs e)
    {
        var r = _lastPadCheck ?? EfootballBot.Core.System.PadSelfCheck.Run();
        _lastPadCheck = r;
        string path = EfootballBot.Core.System.PadSelfCheck.SaveReport(r);
        TxtPadSelfCheckResult.Text = $"{r.Title}\n\n报告已保存：{path}\n（发反馈时直接带上这个文件即可）";
        AppendLog($"[手柄自检] 报告已保存：{path}", LogLevel.Info);
    }

    /// <summary>把自检结论 + 建议动作显示在设置页（只陈述探测事实，不给猜测性结论）。</summary>
    private void ShowPadCheck(EfootballBot.Core.System.PadCheckResult r)
    {
        _lastPadCheck = r;
        TxtPadSelfCheckResult.Text = $"{r.Title}\n{r.Detail}\n\n下一步：{r.SuggestedFix}";
    }
}
