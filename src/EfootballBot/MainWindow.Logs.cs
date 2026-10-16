using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
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
    // ---------------- 日志 / 统计 ----------------

    // 代码颜色改为运行时从 Application 资源读取（跟随主题）
    private static Brush BrushOk => (Brush)Application.Current.Resources["AccentGlow"];
    private static Brush BrushBad => (Brush)Application.Current.Resources["Danger"];
    private static Brush BrushIdle => (Brush)Application.Current.Resources["EnvDotIdle"];
    private static Brush BrushWarn => (Brush)Application.Current.Resources["Warning"];
    private static Brush BrushLogInfo => (Brush)Application.Current.Resources["LogInfo"];
    // 日志分段配色（固定色，不随主题）：时间=橙，模块前缀=蓝
    private static readonly Brush BrushLogTime = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C)); // 橙
    private static readonly Brush BrushLogTag = new SolidColorBrush(Color.FromRgb(0x6C, 0xB6, 0xFF));  // 蓝

    private static readonly System.Text.RegularExpressions.Regex LogPrefixRegex = new(
        @"^\[(?<t>\d{2}:\d{2}:\d{2})\]\s*(?<tag>\[[^\]]+\])?\s*(?<rest>.*)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static Brush LevelBrush(LogLevel lv) => lv switch
    {
        LogLevel.Success => BrushOk,
        LogLevel.Warn => BrushWarn,
        LogLevel.Error => BrushBad,
        _ => BrushLogInfo,
    };

    /// <summary>
    /// 运行日志与日志历史共用的三段分色行：[时间] 橙 / [模块前缀] 蓝 / 正文按级别，自动换行。
    /// </summary>
    private static TextBlock BuildLogLine(string time, string? tag, string rest, LogLevel lv)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(new Run($"[{time}] ") { Foreground = BrushLogTime });
        if (!string.IsNullOrEmpty(tag))
            tb.Inlines.Add(new Run($"{tag} ") { Foreground = BrushLogTag });
        tb.Inlines.Add(new Run(rest) { Foreground = LevelBrush(lv) });
        return tb;
    }

    private void AppendLog(string msg, LogLevel lv)
    {
        // GUI 侧日志全部落盘（v1.1.19 修复）：此前 AppendLog 只写 UI 控件，"正在启动 Worker"、
        // "启动失败：Worker 进程已退出"等关键行从未进入日志文件——用户回传 logs 后 GUI 侧故障
        // 完全不可见（2026-10-13 用户 logs.rar：7 个空白会话无法定位）。Worker 转发事件的直写
        // 已移除，统一由这里落盘，避免双写。
        try { FileLogger.Instance.Write(msg, lv); } catch { }
        if (lv == LogLevel.Debug) return; // 默认不显示调试级（文件日志仍全量）
        // 回放缓存（与显示上限同步 300 条）：切换过滤档时按当前档重渲，被隐藏的条目不丢失
        _logBuffer.Enqueue((msg, lv));
        while (_logBuffer.Count > 300) _logBuffer.Dequeue();
        if (!PassesLogFilter(lv, _runtimeLogFilter)) return;
        AppendLogItem(msg, lv);
    }

    /// <summary>应用内显示过滤档：All=全部 / Key=仅关键流程（Success）/ Warn=仅报错与提醒（Warn+Error）。仅影响 UI，文件日志全量。</summary>
    private string _runtimeLogFilter = "All";

    /// <summary>日志历史查看器的显示过滤档（与运行日志同款三档，选择持久化到配置）。</summary>
    private string _historyLogFilter = "All";

    private readonly Queue<(string Msg, LogLevel Lv)> _logBuffer = new();

    private static bool PassesLogFilter(LogLevel lv, string mode) => mode switch
    {
        "Key" => lv == LogLevel.Success,
        "Warn" => lv is LogLevel.Warn or LogLevel.Error,
        _ => true,
    };

    private void AppendLogItem(string msg, LogLevel lv)
    {
        // 三段分色：[时间] 灰 / [模块前缀] 主题色 / 正文按级别
        TextBlock tb;
        var m = LogPrefixRegex.Match(msg);
        if (m.Success)
        {
            tb = BuildLogLine(m.Groups["t"].Value, m.Groups["tag"].Value, m.Groups["rest"].Value, lv);
        }
        else
        {
            tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
            tb.Inlines.Add(new Run(msg) { Foreground = LevelBrush(lv) });
        }

        LogList.Items.Add(tb);
        while (LogList.Items.Count > 300) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(tb);
    }

    private void CmbRuntimeLogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 的 SelectedIndex=0 在 InitializeComponent 期间即触发，此时 _cfg/LogList 尚未就绪
        if (_cfg is null || LogList is null || _loadingConfig) return;
        _runtimeLogFilter = CmbRuntimeLogFilter.SelectedIndex switch
        {
            1 => "Key",
            2 => "Warn",
            _ => "All",
        };
        _cfg.RuntimeLogFilter = _runtimeLogFilter;
        _cfg.Save();
        // 切档即时生效：从回放缓存重渲（切回「全部」可看到此前被隐藏的日志）
        LogList.Items.Clear();
        foreach (var (msg, lv) in _logBuffer)
            if (PassesLogFilter(lv, _runtimeLogFilter))
                AppendLogItem(msg, lv);
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logBuffer.Clear();
        LogList.Items.Clear();
    }

    private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FileLogger.LogDir);
            Process.Start(new ProcessStartInfo(FileLogger.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"打开日志文件夹失败：{ex.Message}", LogLevel.Error);
        }
    }

    // ---------------- 历史日志查看器 ----------------

    private static readonly System.Text.RegularExpressions.Regex YamlLogLineRegex = new(
        @"^- \{time: ""(?<t>.*?)"", level: ""(?<l>.*?)"", msg: ""(?<m>.*)""\}\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private void RefreshLogFileList()
    {
        try
        {
            CmbLogFiles.Items.Clear();
            if (!Directory.Exists(FileLogger.LogDir))
            {
                LogFileStatus.Text = "日志目录不存在（尚未产生日志）";
                LogFileContent.Items.Clear();
                return;
            }
            var files = new[] { "*.yml", "*.log", "*.txt" }
                .SelectMany(p => Directory.GetFiles(FileLogger.LogDir, p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

            if (files.Count == 0)
            {
                LogFileStatus.Text = "暂无日志文件";
                LogFileContent.Items.Clear();
                return;
            }

            foreach (var f in files)
            {
                CmbLogFiles.Items.Add(new ComboBoxItem { Content = f.Name, Tag = f.FullName });
            }

            // 默认选中当前活跃日志
            string current = FileLogger.Instance.CurrentLogPath;
            if (!string.IsNullOrEmpty(current))
            {
                var match = CmbLogFiles.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(ci => (string)ci.Tag == current);
                if (match is not null) CmbLogFiles.SelectedItem = match;
                else CmbLogFiles.SelectedIndex = 0;
            }
            else
            {
                CmbLogFiles.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"刷新日志列表失败：{ex.Message}", LogLevel.Error);
        }
    }

    private void CmbLogFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbLogFiles.SelectedItem is not ComboBoxItem ci) return;
        string path = (string)ci.Tag;
        LoadLogFile(path);
    }

    private void CmbHistoryLogFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 的 SelectedIndex=0 在 InitializeComponent 期间即触发，此时控件尚未就绪
        if (LogFileContent is null || CmbLogFiles is null || _loadingConfig) return;
        _historyLogFilter = CmbHistoryLogFilter.SelectedIndex switch
        {
            1 => "Key",
            2 => "Warn",
            _ => "All",
        };
        _cfg.HistoryLogFilter = _historyLogFilter;
        _cfg.Save();
        // 当前已加载的文件按新档重渲
        if (CmbLogFiles.SelectedItem is ComboBoxItem ci && ci.Tag is string path)
            LoadLogFile(path);
    }

    /// <summary>YAML 级别标签 → 显示级别（与 FileLogger.WriteYamlLine 的写出对应）。</summary>
    private static LogLevel ParseFileLevel(string tag) => tag?.Trim().ToUpperInvariant() switch
    {
        "OK" => LogLevel.Success,
        "WARN" or "WARNING" => LogLevel.Warn,
        "ERROR" or "FATAL" => LogLevel.Error,
        "DEBUG" => LogLevel.Debug,
        _ => LogLevel.Info,
    };

    /// <summary>旧版 .txt/.log 行内级别标记猜测（无 YAML level 字段时兜底）。</summary>
    private static LogLevel GuessLegacyLevel(string line)
    {
        if (line.Contains("[ERROR]")) return LogLevel.Error;
        if (line.Contains("[WARN]")) return LogLevel.Warn;
        if (line.Contains("[OK]")) return LogLevel.Success;
        if (line.Contains("[DEBUG]")) return LogLevel.Debug;
        return LogLevel.Info;
    }

    private void LoadLogFile(string path)
    {
        LogFileContent.Items.Clear();
        try
        {
            if (!File.Exists(path))
            {
                LogFileStatus.Text = "文件不存在";
                return;
            }

            string[] lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                lines = sr.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            }

            int total = 0, shown = 0;
            foreach (var raw in lines)
            {
                var m = YamlLogLineRegex.Match(raw);
                string msg;
                LogLevel lv;
                string? yamlTime = null;
                if (m.Success)
                {
                    // YAML 日志行：- {time: "...", level: "INFO", msg: "..."}
                    // msg 已被 FileLogger 剥掉 [HH:mm:ss] 前缀，但保留 [模块tag]，与运行日志同构
                    yamlTime = m.Groups["t"].Value;
                    lv = ParseFileLevel(m.Groups["l"].Value);
                    msg = m.Groups["m"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                }
                else
                {
                    msg = raw;
                    lv = GuessLegacyLevel(raw);
                }
                total++;

                if (!PassesLogFilter(lv, _historyLogFilter)) continue;

                // 与运行日志完全同款的三段分色渲染；YAML 行显示时间取自文件时间戳（HH:mm:ss）
                TextBlock tb;
                var pm = LogPrefixRegex.Match(msg);
                if (pm.Success)
                {
                    string t = yamlTime is not null && yamlTime.Length >= 19
                        ? yamlTime.Substring(11, 8)
                        : pm.Groups["t"].Value;
                    tb = BuildLogLine(t, pm.Groups["tag"].Value, pm.Groups["rest"].Value, lv);
                }
                else if (yamlTime is not null)
                {
                    string t = yamlTime.Length >= 19 ? yamlTime.Substring(11, 8) : yamlTime;
                    tb = BuildLogLine(t, null, msg, lv);
                }
                else
                {
                    tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
                    tb.Inlines.Add(new Run(msg) { Foreground = LevelBrush(lv) });
                }

                LogFileContent.Items.Add(tb);
                shown++;
            }

            LogFileStatus.Text = $"{path} | {shown}/{total} 条日志";
        }
        catch (Exception ex)
        {
            LogFileStatus.Text = $"读取失败：{ex.Message}";
        }
    }

    private void BtnRefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        RefreshLogFileList();
    }

    private void BtnDeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (CmbLogFiles.SelectedItem is not ComboBoxItem ci) return;
        string path = (string)ci.Tag;

        if (path == FileLogger.Instance.CurrentLogPath)
        {
            System.Windows.MessageBox.Show("当前正在写入的日志文件不能删除。", "无法删除",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var result = System.Windows.MessageBox.Show($"确定要删除 {ci.Content} 吗？", "确认删除",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            File.Delete(path);
            RefreshLogFileList();
        }
        catch (Exception ex)
        {
            AppendLog($"删除日志失败：{ex.Message}", LogLevel.Error);
        }
    }
}
