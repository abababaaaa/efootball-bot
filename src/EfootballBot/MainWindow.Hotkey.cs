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
    // ---------------- 全局热键 ----------------

    /// <summary>热键触发回调（UI 线程）。</summary>
    private void Hotkey_Triggered()
    {
        if (_engine is not { IsRunning: true }) return;
        AppendLog($"已通过快捷键「{_cfg.Hotkey.DisplayString()}」停止。", LogLevel.Success);
        _hotkey.Unregister();
        _engine.Stop();
        ResetButtons();
    }

    /// <summary>按键捕获 PreviewKeyDown：拦截修饰键+主键组合，防止文本框处理。</summary>
    private void TxtHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // 阻止 TextBox 处理
        CaptureHotkeyFromEvent(e);
    }

    private void TxtHotkey_KeyDown(object sender, KeyEventArgs e)
    {
        // PreviewKeyDown 已经捕获了，这里留空防止重复
    }

    /// <summary>从 KeyEventArgs 提取修饰键和主键，更新配置并刷新 UI。</summary>
    private void CaptureHotkeyFromEvent(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 忽略单独的修饰键（Ctrl / Alt / Shift / Win 按下时 key 就是修饰键本身）
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
            return;

        // 忽略一些不适合作热键的按键
        if (key == Key.Escape || key == Key.Tab || key == Key.Left || key == Key.Right ||
            key == Key.Up || key == Key.Down || key == Key.Back || key == Key.Delete)
            return;

        // 必须至少有一个修饰键
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) == 0)
            return;

        int mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        _cfg.Hotkey.VirtualKey = vk;
        _cfg.Hotkey.Modifiers = mods;

        // 同步到 UI 控件
        TxtHotkey.Text = key.ToString();
        ChkHotkeyCtrl.IsChecked = (mods & 2) != 0;
        ChkHotkeyShift.IsChecked = (mods & 4) != 0;
        ChkHotkeyAlt.IsChecked = (mods & 1) != 0;
        ChkHotkeyWin.IsChecked = (mods & 8) != 0;

        RefreshHotkeyUi();

        // 如果引擎正在运行，立即重新注册
        if (_engine is { IsRunning: true } && _cfg.Hotkey.Enabled)
        {
            try
            {
                if (!_hotkey.Register(_cfg.Hotkey))
                    AppendLog("新热键注册失败（可能被占用）。", LogLevel.Warn);
                else
                    AppendLog($"热键已更新：{_cfg.Hotkey.DisplayString()}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                AppendLog($"热键注册异常：{ex.Message}", LogLevel.Warn);
            }
        }
    }

    /// <summary>把当前热键配置同步到三个模块面板的提示和预览文本。</summary>
    private void RefreshHotkeyUi()
    {
        string label = _cfg.Hotkey.Enabled
            ? $"停止热键：{_cfg.Hotkey.DisplayString()}"
            : "停止热键：已禁用";
        TxtLeagueHotkey.Text = label;
        TxtWeeklyHotkey.Text = label;
        TxtCnHotkey.Text = label;
        TxtHotkeyPreview.Text = $"当前热键：{_cfg.Hotkey.DisplayString()}";
    }
}
