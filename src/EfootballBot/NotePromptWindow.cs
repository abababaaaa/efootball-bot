using System.Windows;
using System.Windows.Controls;

namespace EfootballBot;

/// <summary>
/// 「发现问题手动停止」的可选补充说明弹窗。
/// 用户点「生成反馈包」= 带说明生成；点「取消」或关闭窗口 = 不生成。
/// <see cref="Ask"/> 返回说明文本；返回 null 表示用户选择不生成反馈包。
/// </summary>
public sealed class NotePromptWindow : Window
{
    private readonly TextBox _box;

    public string? Note { get; private set; }

    private NotePromptWindow(string title, string hint)
    {
        Title = title;
        Width = 480;
        Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = hint,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19,
        });
        _box = new TextBox
        {
            Height = 120,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = 800,
        };
        root.Children.Add(_box);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "生成反馈包", MinWidth = 110, Padding = new Thickness(14, 6, 14, 6) };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 80,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        ok.Click += (_, _) => { Note = _box.Text; DialogResult = true; Close(); };
        cancel.Click += (_, _) => { Note = null; DialogResult = false; Close(); };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        root.Children.Add(btns);

        Content = root;
    }

    /// <summary>弹出补充说明窗口。返回说明文本（可能为空串）；null = 用户取消生成反馈包。</summary>
    public static string? Ask(Window owner, string hint)
    {
        var w = new NotePromptWindow("问题反馈（可选补充说明）", hint) { Owner = owner };
        return w.ShowDialog() == true ? (w.Note ?? "") : null;
    }
}
