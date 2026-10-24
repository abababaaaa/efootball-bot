using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EfootballBot;

/// <summary>
/// 「开启满级自动换人」确认弹窗（首次勾选时弹出）：
/// 巡检路径图（游戏内首发站位页）+ 三条硬要求——用户须先手动验证路径能走完首发 11 人、
/// 替补球员不参与换人、满级判定说明。点「我知道了，开启」返回 true；取消/关闭返回 false
/// （调用方把勾选回退）。图片打包自 Assets/autoswap_guide.png。
/// </summary>
public sealed class SwapGuideWindow : Window
{
    private SwapGuideWindow()
    {
        Title = "开启满级自动换人";
        Width = 760;
        Height = 760;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF0C1015"));
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFECF1F7"));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(20, 16, 20, 12),
        };
        var root = new StackPanel();

        root.Children.Add(new TextBlock
        {
            Text = "开启前请先手动验证巡检路径",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF2B13E")),
        });
        root.Children.Add(new TextBlock
        {
            Text = "开启后，每 N 场比赛结束回到我的联赛主页时，会自动打开比赛计划逐个检查首发 11 人：\n" +
                   "· 满级（等级 x/x）球员：能力点没用完先自动分配，再从后备换入当前等级最低的球员；能力点已用完直接换。\n" +
                   "· 替补球员不参与自动换人流程（只换首发 11 人）。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 20,
            Margin = new Thickness(0, 8, 0, 0),
        });
        root.Children.Add(new TextBlock
        {
            Text = "★ 请先按下面的图手动验证一遍：在游戏「比赛计划」页，从门将(GK)开始按 ↑ ← ↑ → → → → → ↓ ← " +
                   "逐格移动光标，确认能依次选中全部 11 名首发。若你的阵型走不完这条路径，请勿开启本功能。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 20,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF2B13E")),
        });

        var img = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/autoswap_guide.png")),
            MaxWidth = 700,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        root.Children.Add(img);
        root.Children.Add(new TextBlock
        {
            Text = "路径：门将(GK) → ↑ → ← → ↑ → → → → → → → ↓ → ←（共 11 名首发，光标默认落在门将上）",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6C7686")),
            Margin = new Thickness(0, 4, 0, 0),
        });

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button
        {
            Content = "我已验证路径，开启",
            MinWidth = 150,
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF2EA46B")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 90,
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(10, 0, 0, 0),
        };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        root.Children.Add(btns);

        scroll.Content = root;
        Content = scroll;
    }

    /// <summary>弹确认窗；true = 用户确认开启。</summary>
    public static bool Confirm(Window owner)
    {
        var w = new SwapGuideWindow { Owner = owner };
        return w.ShowDialog() == true;
    }
}
