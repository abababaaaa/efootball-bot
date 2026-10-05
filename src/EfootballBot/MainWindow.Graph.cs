using System.Windows;
using EfootballBot.Core.Vision;

namespace EfootballBot;

/// <summary>
/// 图谱窗口的打开与事件转发。图谱本体在 GraphWindow（随时可通过左栏「图谱」按钮打开）。
/// 窗口按模块显示线性流程链，开始挂机时自动切换到对应模块。
/// </summary>
public partial class MainWindow
{
    private GraphWindow? _graphWindow;
    private string _graphMode = "myleague";

    private void BtnGraph_Click(object sender, RoutedEventArgs e)
    {
        if (_graphWindow is null)
        {
            _graphWindow = new GraphWindow { Owner = this };
            _graphWindow.Closed += (_, _) => _graphWindow = null;
            _graphWindow.SetMode(_graphMode);
            _graphWindow.Show();
        }
        else
        {
            if (_graphWindow.WindowState == WindowState.Minimized)
                _graphWindow.WindowState = WindowState.Normal;
            _graphWindow.Activate();
        }
    }

    /// <summary>画面事件转发到图谱窗口（窗口开着才更新）。</summary>
    private void GraphOnScreen(GameScreen screen, double score, string? evidence)
        => _graphWindow?.OnScreen(screen, score, evidence);

    /// <summary>开始新场景时：图谱切到对应模块并重置。</summary>
    private void GraphReset(string mode)
    {
        _graphMode = mode;
        if (_graphWindow is null) return;
        _graphWindow.SetMode(mode);
        _graphWindow.ResetGraph();
    }
}
