using System.Windows;
using System.Windows.Controls;

namespace EfootballBot;

/// <summary>
/// 「发现问题手动停止」的可选补充说明弹窗。
/// 用户点「生成反馈包」= 带说明生成；点「取消」或关闭窗口 = 不生成。
/// <see cref="Ask"/> 返回说明文本；返回 null 表示用户选择不生成反馈包。
///
/// 【2026-11-21 修「弹出来整张白」】旧版直接 <c>new Window()</c>：WPF 默认 Window 底色是白色，
/// 而 App.xaml 的深色世界只落在显式写 Background 的窗口（MainWindow / ConnectionOverlay）上；
/// 这里既没设 Background 也没设 Foreground，深色主题下就是白底 + 12.5px 白字 = 只剩输入光标可见，
/// 两个按钮同样白底白字（控件模板不设默认态颜色）。现在按 DESIGN.md 走：
/// 底色绑 WindowBg、文字绑 TextPrimary、按钮用 App 级 PrimaryBtn / GhostBtn、
/// 标题栏挂 FluentWindow（暗色，无标题栏快捷键 Alt+Space）。
/// </summary>
public sealed class NotePromptWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly TextBox _box;

    public string? Note { get; private set; }

    private NotePromptWindow(string title, string hint)
    {
        Title = title;
        Width = 500;
        Height = 300;
        MinWidth = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        // 主题：App.xaml 合并了 WPF-UI 的 Dark 基底，自定义 token 由 ThemeManager.ApplyTheme 写进
        // Application.Resources，所以这里的 DynamicResource 一定解析得到，且跟随主题切换。
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        // 中文正文回落雅黑 UI：本仓库的字体快照里 UiFont = "Segoe UI Variable Text, Microsoft YaHei UI"
        SetResourceReference(FontFamilyProperty, "UiFont");
        FontSize = 13;

        var root = new Grid { Margin = new Thickness(18, 16, 18, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["PageTitle"],
            FontSize = 15,   // 页面级 20 在 500px 弹窗里过大，压一档
        };
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(new TextBlock
        {
            Text = hint,
            Style = (Style)Application.Current.Resources["BodyText"],
        });
        _box = new TextBox
        {
            Height = 112,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(8, 6, 8, 6),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 13,
            MaxLength = 800,
        };
        body.Children.Add(_box);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button
        {
            Content = "生成反馈包",
            MinWidth = 112,
            Style = (Style)Application.Current.Resources["PrimaryBtn"],
        };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 82,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.Resources["GhostBtn"],
        };
        ok.Click += (_, _) => { Note = _box.Text; DialogResult = true; Close(); };
        cancel.Click += (_, _) => { Note = null; DialogResult = false; Close(); };
        ok.IsDefault = true;   // 回车 = 生成反馈包
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Note = null;
                DialogResult = false;
                Close();
            }
        };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;

        // 打开即聚焦输入框（用户真正要做的事）。回车见 ok.IsDefault，Esc 见 PreviewKeyDown。
        Loaded += (_, _) => _box.Focus();
    }

    /// <summary>弹出补充说明窗口。返回说明文本（可能为空串）；null = 用户取消生成反馈包。</summary>
    public static string? Ask(Window owner, string hint)
    {
        var w = new NotePromptWindow("问题反馈（可选补充说明）", hint) { Owner = owner };
        return w.ShowDialog() == true ? (w.Note ?? "") : null;
    }
}
