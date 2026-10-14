using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EfootballBot.Core.Input;

namespace EfootballBot;

/// <summary>
/// 热键记录弹窗：按下任意键（可含修饰键）或鼠标键即记录，Esc 取消。
/// 结果写入 <see cref="Code"/>/<see cref="Modifiers"/>/<see cref="IsMouse"/>。
/// </summary>
public sealed class HotkeyRecordWindow : Window
{
    /// <summary>记录到的键码（键盘 VK 或鼠标键码 1左/2右/4中/5X1/6X2）。</summary>
    public int Code { get; private set; }
    /// <summary>修饰键掩码（Alt=1 Ctrl=2 Shift=4 Win=8）。</summary>
    public int Modifiers { get; private set; }
    /// <summary>是否鼠标键。</summary>
    public bool IsMouse { get; private set; }
    /// <summary>是否成功记录（false=取消）。</summary>
    public bool Recorded { get; private set; }

    private readonly TextBlock _hint;

    public HotkeyRecordWindow(string title)
    {
        Title = title;
        Width = 380;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        _hint = new TextBlock
        {
            Text = "请按下要记录的按键组合（键盘任意键或鼠标键，可含 Ctrl/Shift/Alt/Win）\n按 Esc 取消",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16),
        };
        Content = _hint;
        PreviewKeyDown += OnKey;
        PreviewMouseDown += OnMouse;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Close(); return; }

        // 单独的修饰键不作为主键
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        // 方向/编辑键不适合
        if (key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Back or Key.Delete)
            return;

        int mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;

        Code = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        Modifiers = mods;
        IsMouse = false;
        Recorded = true;
        Close();
    }

    private void OnMouse(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Code = e.ChangedButton switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 4,
            MouseButton.XButton1 => 5,
            MouseButton.XButton2 => 6,
            _ => 0,
        };
        if (Code == 0) return;

        int mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 1;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= 8;

        Modifiers = mods;
        IsMouse = true;
        Recorded = true;
        Close();
    }
}
