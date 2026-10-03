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
    // ---------------- 日志 / 统计 ----------------

    private void AppendLog(string msg, LogLevel lv)
    {
        if (lv == LogLevel.Debug) return; // 默认不显示调试级
        var tb = new TextBlock
        {
            Text = msg,
            TextWrapping = TextWrapping.Wrap,
            Foreground = lv switch
            {
                LogLevel.Success => BrushOk,
                LogLevel.Warn => BrushWarn,
                LogLevel.Error => BrushBad,
                _ => BrushLogInfo,
            },
        };
        LogList.Items.Add(tb);
        while (LogList.Items.Count > 500) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(tb);
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();

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
            var files = Directory.GetFiles(FileLogger.LogDir, "*.log")
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

            foreach (var line in lines)
            {
                Brush b = brushInfo;
                if (line.Contains("[ERROR]")) b = brushError;
                else if (line.Contains("[WARN]")) b = brushWarn;
                else if (line.Contains("[OK]")) b = brushOk;
                LogFileContent.Items.Add(new TextBlock
                {
                    Text = line,
                    Foreground = b,
                    TextWrapping = TextWrapping.NoWrap,
                });
            }

            LogFileStatus.Text = $"{path} | {lines.Length} 行";
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
