using System.Windows;
using System.Windows.Controls;
using EfootballBot.Core;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;

namespace EfootballBot;

public partial class MainWindow
{
    // ---------------- 全局热键（暂停/停止 + 恢复） ----------------

    private System.Windows.Threading.DispatcherTimer? _autoCloseTimer;

    /// <summary>主热键：运行中第一次按=暂停；暂停中再按=彻底停止。</summary>
    private void Hotkey_PauseStop_Triggered()
    {
        if (_workerHost is not { ScenarioRunning: true }) return;
        if (_paused)
        {
            AppendLog($"[热键] 已通过「{_cfg.Hotkey.DisplayString()}」请求停止。", LogLevel.Success);
            _ = StopScenarioAsync();
        }
        else
        {
            AppendLog($"[热键] 已通过「{_cfg.Hotkey.DisplayString()}」请求暂停。", LogLevel.Success);
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
            AppendLog($"[暂停] 命令发送失败：{ex.Message}", LogLevel.Error);
            return;
        }
        _paused = next;
        RefreshRunButtons();
        // 全权接管（v3.3.0）：暂停=纯场景暂停并清空手柄输出。真实手柄挂机期间本就处于
        // 禁用让位态，拿去玩其他游戏请先停止挂机（停止即自动恢复）。
        if (next)
            AppendLog("[暂停] 已暂停观察与操作并清空手柄输出（真实手柄仍处让位态，需先停止挂机才能自用）。", LogLevel.Success);
        else
            AppendLog("[继续] 已恢复挂机执行。", LogLevel.Info);
    }

    /// <summary>统一停止（GUI 按钮与热键共用）。手动停止默认不自动关闭程序；自然结束由 WorkerHost.Exited 触发。</summary>
    private async Task StopScenarioAsync(bool allowAutoClose = false)
    {
        if (!allowAutoClose) CancelAutoClose();
        if (_stopping) return;
        var host = _workerHost;
        if (host is null) return;
        _stopping = true;
        _hotkey.Unregister();
        AppendLog("[停止] 正在停止挂机并释放手柄…", LogLevel.Info);
        RefreshRunButtons(); // _stopping=true 期间禁用所有启动/切换/暂停按钮，防止在 StopAsync 等待期间重入
        try
        {
            await host.StopAsync();
            MuteGameOnScenarioStopped(); // 停挂机即恢复游戏声音（必须在按钮恢复可点之前，防止误取消新会话静音）
            AppendLog("[停止] 挂机已停止，手柄与窗口状态已恢复。", LogLevel.Info);
        }
        finally
        {
            _stopping = false;
            ResetButtons();
        }
        if (allowAutoClose && _cfg.AutoCloseWhenDone)
            ScheduleAutoClose();
    }

    private bool _autoCloseScheduled;

    private void ScheduleAutoClose()
    {
        if (_autoCloseScheduled) return;
        _autoCloseScheduled = true;
        AppendLog("[自动关闭] 全部流程已完成，将在 2 秒后自动关闭程序…", LogLevel.Success);
        _autoCloseTimer?.Stop();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoCloseTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_autoCloseScheduled) return;
            Close();
        };
        timer.Start();
    }

    private void CancelAutoClose()
    {
        if (!_autoCloseScheduled && _autoCloseTimer is null) return;
        _autoCloseTimer?.Stop();
        _autoCloseTimer = null;
        if (_autoCloseScheduled)
        {
            _autoCloseScheduled = false;
            AppendLog("[自动关闭] 已取消自动关闭。", LogLevel.Info);
        }
    }

    /// <summary>各模块页头部的热键胶囊（全部模块覆盖，懒初始化）。</summary>
    private readonly List<TextBlock> _hotkeyChips = new();

    private List<TextBlock> HotkeyChips()
    {
        if (_hotkeyChips.Count == 0)
        {
            foreach (var t in new[]
                     {
                         TxtHkIntlLeague, TxtHkIntlWeekly, TxtHkIntlLadder, TxtHkStud,
                         TxtHkCnLeague, TxtHkCnWeekly, TxtHkCnMyLeague,
                     })
                if (t is not null) _hotkeyChips.Add(t);
        }
        return _hotkeyChips;
    }

    /// <summary>热键短标签（如 F10）；热键未启用时返回 null，按钮不加后缀。</summary>
    private string? HotkeyLabel => _cfg is { Hotkey.Enabled: true } c ? c.Hotkey.DisplayString() : null;

    /// <summary>把当前热键配置同步到设置页标签、各模块页胶囊与运行按钮。</summary>
    private void RefreshHotkeyUi()
    {
        if (LblHotkeyMain is null) return; // XAML 初始化顺序保护
        string disp = _cfg.Hotkey.Enabled ? _cfg.Hotkey.DisplayString() : "已禁用";
        var fg = (System.Windows.Media.Brush)FindResource(_cfg.Hotkey.Enabled ? "TextPrimary" : "TextMuted");
        LblHotkeyMain.Text = disp;
        foreach (var t in HotkeyChips())
        {
            t.Text = disp;
            t.Foreground = fg;
        }
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
        PersistConfigSafe("保存热键设置");
        AppendLog($"[设置] 全局热键已设为：{_cfg.Hotkey.DisplayString()}", LogLevel.Info);
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
        PersistConfigSafe("保存热键开关");
        AppendLog($"[设置] 全局热键：{(_cfg.Hotkey.Enabled ? "已启用" : "已禁用")}", LogLevel.Info);
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
                AppendLog($"[热键] {what}热键注册失败。", LogLevel.Warn);
            else
                AppendLog($"[热键] {what}热键已更新：{_cfg.Hotkey.DisplayString()}（第一次暂停，第二次终止）", LogLevel.Info);
        }
        catch (System.Exception ex)
        {
            AppendLog($"[热键] {what}热键注册异常：{ex.Message}", LogLevel.Warn);
        }
    }
}

