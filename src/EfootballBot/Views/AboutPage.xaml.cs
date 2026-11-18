using System.Windows.Controls;

namespace EfootballBot.Views;

/// <summary>
/// 关于页（声明）。当前是纯静态内容，因此没有代码；
/// 以后要给这一页加交互（点链接、复制作者信息…），处理函数写在这里，
/// 不要回到 MainWindow.*.cs —— 那正是「改一个小东西牵一发而动全身」的起点。
/// </summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }
}
