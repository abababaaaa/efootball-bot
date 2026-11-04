using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EfootballBot.Core.System;

namespace EfootballBot;

/// <summary>
/// 游戏内连接监控悬浮窗：无边框置顶、半透明，拖到游戏画面上边打边看延迟/丢包。
/// 左键拖动、双击回右上角、右键关闭（关闭只隐藏，监控线程继续，页面可再打开）。
/// </summary>
public partial class ConnectionOverlay : Window
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x3E));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x7B, 0x86, 0x96));

    public ConnectionOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) => MoveToDefault();
    }

    private void MoveToDefault()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 24;
        Top = wa.Top + 24;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { MoveToDefault(); return; }
        try { DragMove(); } catch { }
    }

    private void OnClose(object sender, MouseButtonEventArgs e) => Hide();

    public void Update(ConnSnapshot s)
    {
        TxtState.Text = s.State;
        Dot.Fill = s.DelayMs < 0 ? Idle : s.DelayMs < 60 ? Ok : s.DelayMs < 150 ? Warn : Bad;
        TxtDelay.Text = s.DelayMs < 0 ? "—" : s.DelayMs + " ms";
        TxtDelay.Foreground = Dot.Fill;
        TxtLoss.Text = s.LossPct <= 0.05 ? "0%" : $"{s.LossPct:0.#}%";
        TxtLoss.Foreground = s.LossPct < 1 ? (Brush)Ok : s.LossPct < 5 ? Warn : Bad;
        TxtRegion.Text = s.Region;
        TxtRemote.Text = $"远端 {s.Remote}   最小/平均/最大 {Fmt(s.MinMs)}/{Fmt(s.AvgMs)}/{Fmt(s.MaxMs)}";
        DrawSpark(s.Curve);
    }

    private static string Fmt(int v) => v < 0 ? "—" : v.ToString();

    private void DrawSpark(IReadOnlyList<int> samples)
    {
        CvSpark.Children.Clear();
        if (samples.Count < 2) return;
        double w = CvSpark.ActualWidth;
        double h = CvSpark.ActualHeight;
        if (w < 10 || h < 5) return;
        int peak = Math.Max(200, samples.Where(v => v > 0).DefaultIfEmpty(200).Max());
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            for (int i = 0; i < samples.Count; i++)
            {
                int v = samples[i];
                if (v < 0) { started = false; continue; }
                double x = w * i / Math.Max(1, samples.Count - 1);
                double y = h - 1 - (h - 4) * Math.Min(1.0, v / (double)peak);
                if (!started) { ctx.BeginFigure(new Point(x, y), false, false); started = true; }
                else ctx.LineTo(new Point(x, y), true, false);
            }
        }
        geo.Freeze();
        CvSpark.Children.Add(new Path { Data = geo, Stroke = Ok, StrokeThickness = 1.4 });
    }
}
