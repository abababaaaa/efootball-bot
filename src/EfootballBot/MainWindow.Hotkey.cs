using System.Windows;
using System.Windows.Controls;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;

namespace EfootballBot;

public partial class MainWindow
{
    // ---------------- 全局热键（暂停/停止 + 恢复） ----------------

    /// <summary>主热键：运行中第一次按=暂停；暂停中再按=彻底停止。</summary>
    private void Hotkey_PauseStop_Triggered()
    {
        if (_workerHost is not { IsRunning: true }) return;
        if (_paused)
        {
            AppendLog($"已通过快捷键「{_cfg.Hotkey.DisplayString()}」停止。", LogLevel.Success);
            _ = StopScenarioAsync();
        }
        else
        {
            AppendLog($"已通过快捷键「{_cfg.Hotkey.DisplayString()}」暂停。", LogLevel.Success);
            _ = TogglePauseAsync();
        }
    }

    /// <summary>恢复热键：从暂停恢复。</summary>
    private void Hotkey_Resume_Triggered()
    {
        if (_workerHost is not { IsRunning: true } || !_paused) return;
        AppendLog($"已通过快捷键「{_cfg.Hotkey.ResumeDisplayString()}」恢复。", LogLevel.Success);
        _ = TogglePauseAsync();
    }

    /// <summary>统一暂停/恢复切换（GUI 按钮与热键共用），同步按钮文字与热键可视化。</summary>
    private async Task TogglePauseAsync()
    {
        var host = _workerHost;
        if (host is not { IsRunning: true }) return;
        bool next = !_paused;
        try
        {
            await host.SendPauseAsync(next);
        }
        catch (System.Exception ex)
        {
            AppendLog($"暂停命令发送失败：{ex.Message}", LogLevel.Error);
            return;
        }
        _paused = next;
        RefreshRunButtons();
    }

    /// <summary>统一停止（GUI 按钮与热键共用）。</summary>
    private async Task StopScenarioAsync()
    {
        var host = _workerHost;
        if (host is null) return;
        _hotkey.Unregister();
        AppendLog("正在停止…", LogLevel.Info);
        ResetButtons();
        await host.StopAsync();
        AppendLog("已停止。", LogLevel.Info);
        if (_cfg.AutoCloseWhenDone)
        {
            AppendLog("全部流程完毕，2 秒后自动关闭…", LogLevel.Success);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }

    /// <summary>刷新运行控制按钮文字（含热键可视化）：停止/暂停/继续。多模块：仅当前运行的模块按钮显示"停止"。</summary>
    private void RefreshRunButtons()
    {
        bool running = _workerHost is { IsRunning: true };
        string ps = _cfg.Hotkey.Enabled ? _cfg.Hotkey.DisplayString() : "";
        string rs = _cfg.Hotkey.Enabled ? _cfg.Hotkey.ResumeDisplayString() : "";
        string stopText = string.IsNullOrEmpty(ps) ? "停止" : $"停止 ({ps})";

        BtnStartMyLeague.Content = running && _activeStartBtn == BtnStartMyLeague ? stopText : "开始";
        BtnStartWeekly.Content = running && _activeStartBtn == BtnStartWeekly ? stopText : "开始";
        BtnStartAiLadder.Content = running && _activeStartBtn == BtnStartAiLadder ? stopText : "开始";
        BtnStartCnWeekly.Content = running && _activeStartBtn == BtnStartCnWeekly ? stopText : "开始";

        if (!running)
            BtnPause.Content = "未开始";
        else if (_paused)
            BtnPause.Content = string.IsNullOrEmpty(rs) ? "继续" : $"继续 ({rs})";
        else
            BtnPause.Content = string.IsNullOrEmpty(ps) ? "暂停" : $"暂停 ({ps})";
    }

    /// <summary>把当前热键配置同步到设置页标签与运行按钮。</summary>
    private void RefreshHotkeyUi()
    {
        if (LblHotkeyMain is null) return; // XAML 初始化顺序保护
        LblHotkeyMain.Text = _cfg.Hotkey.Enabled ? _cfg.Hotkey.DisplayString() : "已禁用";
        LblHotkeyResume.Text = _cfg.Hotkey.Enabled ? _cfg.Hotkey.ResumeDisplayString() : "已禁用";
        RefreshRunButtons();
    }

    // ---------------- 记录键 ----------------

    /// <summary>记录主热键（暂停/停止）：弹小窗捕获键盘任意键或鼠标键。</summary>
    private void BtnRecordHotkeyMain_Click(object sender, RoutedEventArgs e)
    {
        var w = new HotkeyRecordWindow("记录 暂停/停止 热键") { Owner = this };
        w.ShowDialog();
        if (!w.Recorded) return;
        _cfg.Hotkey.IsMouse = w.IsMouse;
        _cfg.Hotkey.VirtualKey = w.Code;
        _cfg.Hotkey.Modifiers = w.Modifiers;
        _cfg.Save();
        ReregisterIfRunning("暂停/停止");
        RefreshHotkeyUi();
    }

    /// <summary>记录恢复热键。</summary>
    private void BtnRecordHotkeyResume_Click(object sender, RoutedEventArgs e)
    {
        var w = new HotkeyRecordWindow("记录 恢复 热键") { Owner = this };
        w.ShowDialog();
        if (!w.Recorded) return;
        _cfg.Hotkey.ResumeIsMouse = w.IsMouse;
        _cfg.Hotkey.ResumeVirtualKey = w.Code;
        _cfg.Hotkey.ResumeModifiers = w.Modifiers;
        _cfg.Save();
        ReregisterIfRunning("恢复");
        RefreshHotkeyUi();
    }

    /// <summary>启用开关变化：立即保存并在运行中即时注册/注销。
    /// 注意 XAML 的 IsChecked="True" 会在 InitializeComponent 期间触发本事件——
    /// 此时 _cfg 尚未赋值，必须空守卫直接返回（加载赋值另有 _loadingConfig 屏蔽）。</summary>
    private void ChkHotkeyEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingConfig || _cfg is null || ChkHotkeyEnabled is null) return;
        _cfg.Hotkey.Enabled = ChkHotkeyEnabled.IsChecked == true;
        _cfg.Save();
        if (_workerHost is { IsRunning: true })
        {
            if (_cfg.Hotkey.Enabled) ReregisterIfRunning("热键");
            else _hotkey.Unregister();
        }
        RefreshHotkeyUi();
    }

    private void ReregisterIfRunning(string what)
    {
        if (_workerHost is not { IsRunning: true } || !_cfg.Hotkey.Enabled) return;
        try
        {
            if (!_hotkey.Register(_cfg.Hotkey))
                AppendLog($"{what}热键注册失败。", LogLevel.Warn);
            else
                AppendLog($"{what}热键已更新：{_cfg.Hotkey.DisplayString()} / {_cfg.Hotkey.ResumeDisplayString()}", LogLevel.Info);
        }
        catch (System.Exception ex)
        {
            AppendLog($"{what}热键注册异常：{ex.Message}", LogLevel.Warn);
        }
    }
}
