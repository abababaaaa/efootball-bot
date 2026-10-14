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
        if (!PassesRuntimeLogFilter(lv)) return;
        AppendLogItem(msg, lv);
    }

    /// <summary>应用内显示过滤档：All=全部 / Key=仅关键流程（Success）/ Warn=仅报错与提醒（Warn+Error）。仅影响 UI，文件日志全量。</summary>
    private string _runtimeLogFilter = "All";

    private readonly Queue<(string Msg, LogLevel Lv)> _logBuffer = new();

    private bool PassesRuntimeLogFilter(LogLevel lv) => _runtimeLogFilter switch
    {
        "Key" => lv == LogLevel.Success,
        "Warn" => lv is LogLevel.Warn or LogLevel.Error,
        _ => true,
    };

    private void AppendLogItem(string msg, LogLevel lv)
    {
        Brush levelBrush = lv switch
        {
            LogLevel.Success => BrushOk,
            LogLevel.Warn => BrushWarn,
            LogLevel.Error => BrushBad,
            _ => BrushLogInfo,
        };

        // 三段分色：[时间] 灰 / [模块前缀] 主题色 / 正文按级别
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var m = LogPrefixRegex.Match(msg);
        if (m.Success)
        {
            tb.Inlines.Add(new Run($"[{m.Groups["t"].Value}] ") { Foreground = BrushLogTime });
            if (!string.IsNullOrEmpty(m.Groups["tag"].Value))
                tb.Inlines.Add(new Run($"{m.Groups["tag"].Value} ") { Foreground = BrushLogTag });
            tb.Inlines.Add(new Run(m.Groups["rest"].Value) { Foreground = levelBrush });
        }
        else
        {
            tb.Inlines.Add(new Run(msg) { Foreground = levelBrush });
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
            if (PassesRuntimeLogFilter(lv))
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
            var brushInfo = (Brush)FindResource("LogInfo");
            var brushWarn = (Brush)FindResource("Warning");
            var brushError = (Brush)FindResource("Danger");
            var brushOk = (Brush)FindResource("AccentGlow");

            // YAML 日志行：- {time: "...", level: "INFO", msg: "..."}
            var yamlLine = new System.Text.RegularExpressions.Regex(
                @"^- \{time: ""(?<t>.*?)"", level: ""(?<l>.*?)"", msg: ""(?<m>.*)""\}\s*$");
            int shown = 0;
            foreach (var raw in lines)
            {
                string line = raw;
                string level = "";
                var m = yamlLine.Match(raw);
                if (m.Success)
                {
                    level = m.Groups["l"].Value;
                    string msg = m.Groups["m"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
                    line = $"[{m.Groups["t"].Value}] [{level}] {msg}";
                }
                Brush b = brushInfo;
                if (line.Contains("[ERROR]") || level == "ERROR") b = brushError;
                else if (line.Contains("[WARN]") || level == "WARN") b = brushWarn;
                else if (line.Contains("[OK]") || level == "OK") b = brushOk;
                LogFileContent.Items.Add(new TextBlock
                {
                    Text = line,
                    Foreground = b,
                    TextWrapping = TextWrapping.NoWrap,
                });
                shown++;
            }

            LogFileStatus.Text = $"{path} | {shown} 条日志";
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
