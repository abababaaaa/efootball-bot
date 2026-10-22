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
        if (_workerHost is not { ScenarioRunning: true }) return;
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

    /// <summary>统一暂停/恢复切换（GUI 按钮与监控页共用），同步按钮文字与热键可视化。</summary>
    private async Task TogglePauseAsync()
    {
        var host = _workerHost;
        if (host is not { ScenarioRunning: true }) return;
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
        // 全权接管（v3.3.0）：暂停=纯场景暂停并清空手柄输出。真实手柄挂机期间本就处于
        // 禁用让位态，拿去玩其他游戏请先停止挂机（停止即自动恢复）。
        if (next)
            AppendLog("已暂停：已清空手柄输出，暂停观察与操作（真实手柄仍处让位态，拿去玩游戏请先停止挂机）。", LogLevel.Success);
        else
            AppendLog("已恢复挂机。", LogLevel.Info);
    }

    /// <summary>统一停止（GUI 按钮与热键共用）。allowAutoClose=false 用于手动停止后仍需弹窗交互的场景。</summary>
    private async Task StopScenarioAsync(bool allowAutoClose = true)
    {
        var host = _workerHost;
        if (host is null) return;
        _hotkey.Unregister();
        AppendLog("正在停止…", LogLevel.Info);
        ResetButtons();
        await host.StopAsync();
        AppendLog("已停止。", LogLevel.Info);
        if (allowAutoClose && _cfg.AutoCloseWhenDone)
        {
            AppendLog("全部流程完毕，2 秒后自动关闭…", LogLevel.Success);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { timer.Stop(); Close(); };
            timer.Start();
        }
    }

    /// <summary>把当前热键配置同步到设置页标签与运行按钮。</summary>
    private void RefreshHotkeyUi()
    {
        if (LblHotkeyMain is null) return; // XAML 初始化顺序保护
        string disp = _cfg.Hotkey.Enabled ? _cfg.Hotkey.DisplayString() : "已禁用";
        LblHotkeyMain.Text = disp;
        if (TxtCnHotkey is not null) TxtCnHotkey.Text = disp;
        RefreshRunButtons();
    }

    // ---------------- 记录键 ----------------

    /// <summary>记录主热键（暂停/终止）：弹小窗捕获键盘任意键或鼠标键。</summary>
    private void BtnRecordHotkeyMain_Click(object sender, RoutedEventArgs e)
    {
        var w = new HotkeyRecordWindow("记录 暂停/终止 热键") { Owner = this };
        w.ShowDialog();
        if (!w.Recorded) return;
        _cfg.Hotkey.IsMouse = w.IsMouse;
        _cfg.Hotkey.VirtualKey = w.Code;
        _cfg.Hotkey.Modifiers = w.Modifiers;
        _cfg.Save();
        ReregisterIfRunning("暂停/终止");
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
        if (_workerHost is { ScenarioRunning: true })
        {
            if (_cfg.Hotkey.Enabled) ReregisterIfRunning("热键");
            else _hotkey.Unregister();
        }
        RefreshHotkeyUi();
    }

    private void ReregisterIfRunning(string what)
    {
        if (_workerHost is not { ScenarioRunning: true } || !_cfg.Hotkey.Enabled) return;
        try
        {
            if (!_hotkey.Register(_cfg.Hotkey))
                AppendLog($"{what}热键注册失败。", LogLevel.Warn);
            else
                AppendLog($"{what}热键已更新：{_cfg.Hotkey.DisplayString()}（第一次暂停，第二次终止）", LogLevel.Info);
        }
        catch (System.Exception ex)
        {
            AppendLog($"{what}热键注册异常：{ex.Message}", LogLevel.Warn);
        }
    }
}
