using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EfootballBot.Core.Bot;
using EfootballBot.Core.System;

namespace EfootballBot;

// 网络页：GitPort GitHub 加速内核管理 + 服务器延迟测试 + 实时监控（每秒 tcping，丢包率与曲线）
public partial class MainWindow
{
    private NetworkProbe? _probe;
    private ObservableCollection<ServerRowVm> _serverRows = new();
    private CancellationTokenSource? _measureCts;
    private bool _gitPortBusy;

    // 监控启动时选中的行（曲线跟随它；换行立即换曲线）
    private ServerRowVm? SelectedRow => LvServers.SelectedItem as ServerRowVm;

    // ---------------- 表格行模型 ----------------

    public sealed class ServerRowVm : INotifyPropertyChanged
    {
        public int Id { get; init; }
        public string Label { get; init; } = "";
        public string Kind { get; init; } = "";

        private string _ip = "";
        public string Ip { get => _ip; set { _ip = value; Raise(nameof(Ip)); } }

        private bool _monitor;
        public bool Monitor { get => _monitor; set { _monitor = value; Raise(nameof(Monitor)); } }

        private string _delayText = "—";
        public string DelayText { get => _delayText; set { _delayText = value; Raise(nameof(DelayText)); } }

        private Brush _dotBrush = (Brush)Application.Current.Resources["EnvDotIdle"];
        public Brush DotBrush { get => _dotBrush; set { _dotBrush = value; Raise(nameof(DotBrush)); } }

        private int _ms = int.MinValue; // int.MinValue=未测，-1=失败
        public int Ms
        {
            get => _ms;
            set
            {
                _ms = value;
                if (value == int.MinValue) { DelayText = "—"; DotBrush = Idle; }
                else if (value < 0) { DelayText = "×"; DotBrush = Idle; }
                else
                {
                    DelayText = $"{value} ms";
                    DotBrush = value < 60 ? Ok : value < 150 ? Warn : Bad;
                }
            }
        }

        private static Brush Ok => (Brush)Application.Current.Resources["AccentGlow"];
        private static Brush Warn => (Brush)Application.Current.Resources["Warning"];
        private static Brush Bad => (Brush)Application.Current.Resources["Danger"];
        private static Brush Idle => (Brush)Application.Current.Resources["EnvDotIdle"];

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
    }

    private NetworkProbe Probe => _probe ??= new NetworkProbe();

    /// <summary>目标类型显示名（种子表的 game/update/candidate + 域名/手填）。</summary>
    private static string KindText(string kind) => kind switch
    {
        "game" => "游戏服",
        "update" => "更新服",
        "candidate" => "候选服",
        "domain" => "域名",
        _ => "手填",
    };

    // ---------------- 进入页面 ----------------

    private void InitNetworkPage()
    {
        if (LvServers.ItemsSource is null)
        {
            foreach (var t in Probe.Targets)
                _serverRows.Add(new ServerRowVm
                { Id = t.Id, Label = t.Label, Ip = t.Ip, Kind = KindText(t.Kind), Monitor = t.Monitor });
            LvServers.ItemsSource = _serverRows;
            var view = (System.ComponentModel.ICollectionView)LvServers.Items;
            view.Filter = o =>
            {
                if (o is not ServerRowVm r) return false;
                // 延迟阈值筛选：只在已有测量值时生效（未测的恒显示）
                if (int.TryParse(TxtFilterMs.Text, out int limit) && r.Ms >= 0 && r.Ms > limit) return false;
                string f = TxtServerFilter.Text.Trim();
                if (f.Length > 0 && !r.Label.Contains(f, StringComparison.OrdinalIgnoreCase)
                    && !r.Ip.Contains(f, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            };
        }
        RefreshGitPortState();
        UpdateServerSummary();
    }

    private void RefreshGitPortState()
    {
        if (_gitPortBusy) return;
        bool running = GitPortManager.IsRunning;
        DotGitPort.Fill = GitPortManager.IsWorkbenchUp ? BrushOk : running ? (Brush)Application.Current.Resources["Warning"] : BrushIdle;
        TxtGitPortState.Text = !GitPortManager.IsInstalled ? "内核未下载"
            : GitPortManager.IsWorkbenchUp ? "运行中 · 工作台就绪"
            : running ? "启动中…" : "未运行";
        BtnGitPortAction.Content = !GitPortManager.IsInstalled ? "下载内核"
            : running ? "停止加速" : "启动加速";
    }

    // ---------------- GitPort ----------------

    private async void BtnGitPortAction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_gitPortBusy) return;
        try
        {
            if (!GitPortManager.IsInstalled)
            {
                _gitPortBusy = true;
                BtnGitPortAction.IsEnabled = false;
                TxtGitPortStatus.Text = "正在下载 GitPort 内核（走镜像链，约 8 MB）…";
                AppendLog("开始下载 GitPort GitHub 加速内核…", LogLevel.Info);
                await GitPortManager.DownloadAsync(
                    s => Dispatcher.BeginInvoke(() => TxtGitPortStatus.Text = s),
                    System.Threading.CancellationToken.None);
                TxtGitPortStatus.Text = "内核下载完成。";
                AppendLog($"GitPort 内核下载完成（{GitPortManager.Version}）", LogLevel.Success);
            }
            else if (GitPortManager.IsRunning)
            {
                GitPortManager.Stop((m, lv) => { AppendLog(m, lv); });
                TxtGitPortStatus.Text = "";
            }
            else
            {
                _gitPortBusy = true;
                BtnGitPortAction.IsEnabled = false;
                TxtGitPortStatus.Text = "正在启动内核…";
                bool ok = await Task.Run(() => GitPortManager.Start((m, lv) =>
                    Dispatcher.BeginInvoke(() => AppendLog(m, lv))));
                TxtGitPortStatus.Text = ok ? "" : "启动超时：进程已拉起但工作台未就绪，可稍后点「打开工作台」重试。";
            }
        }
        catch (OperationCanceledException) { TxtGitPortStatus.Text = "下载已取消。"; }
        catch (Exception ex)
        {
            TxtGitPortStatus.Text = $"失败：{ex.Message}";
            AppendLog($"GitPort 操作失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            _gitPortBusy = false;
            BtnGitPortAction.IsEnabled = true;
            RefreshGitPortState();
        }
    }

    private void BtnGitPortWorkbench_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!GitPortManager.IsInstalled)
        {
            TxtGitPortStatus.Text = "先下载并启动内核，再打开工作台。";
            return;
        }
        if (!GitPortManager.IsRunning)
        {
            TxtGitPortStatus.Text = "内核未运行，正在启动…";
            _ = Task.Run(() => GitPortManager.Start((m, lv) => Dispatcher.BeginInvoke(() => AppendLog(m, lv))))
                .ContinueWith(_ => Dispatcher.BeginInvoke(RefreshGitPortState));
        }
        GitPortManager.OpenWorkbench();
    }

    // ---------------- 服务器测量 ----------------

    private async void BtnMeasureAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_measureCts is not null) return; // 测量进行中
        _measureCts = new CancellationTokenSource();
        BtnMeasureAll.IsEnabled = false;
        BtnMeasureCancel.IsEnabled = true;
        AppendLog("开始测量全部服务器延迟…", LogLevel.Info);
        int n = 0;
        var ct = _measureCts.Token;
        bool completed = await Probe.MeasureAllAsync(ct, (t, ms) => Dispatcher.BeginInvoke(() =>
        {
            var row = _serverRows.FirstOrDefault(r => r.Id == t.Id);
            if (row is null) return;
            row.Ip = t.Ip;
            row.Ms = ms;
            n++;
            UpdateServerSummary();
        }));
        _measureCts.Dispose();
        _measureCts = null;
        BtnMeasureAll.IsEnabled = true;
        BtnMeasureCancel.IsEnabled = false;
        AppendLog(completed
            ? $"延迟测量完成（{n} 个目标）。"
            : "延迟测量已取消。", completed ? LogLevel.Success : LogLevel.Info);
        ((System.ComponentModel.ICollectionView)LvServers.Items).Refresh();
    }

    private void BtnMeasureCancel_Click(object sender, System.Windows.RoutedEventArgs e)
        => _measureCts?.Cancel();

    private void UpdateServerSummary()
    {
        int measured = _serverRows.Count(r => r.Ms != int.MinValue);
        int ok = _serverRows.Count(r => r.Ms >= 0);
        int monitor = _serverRows.Count(r => r.Monitor);
        TxtServerSummary.Text = $"已测 {measured}/{_serverRows.Count} · 可达 {ok} · 监控 {monitor} 项";
    }

    private void TxtFilterMs_TextChanged(object sender, TextChangedEventArgs e)
        => ((System.ComponentModel.ICollectionView?)LvServers?.Items)?.Refresh();

    private void TxtServerFilter_TextChanged(object sender, TextChangedEventArgs e)
        => ((System.ComponentModel.ICollectionView?)LvServers?.Items)?.Refresh();

    private void TxtAddTarget_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTargetFromBox();
    }

    private void BtnAddTarget_Click(object sender, System.Windows.RoutedEventArgs e) => AddTargetFromBox();

    private void AddTargetFromBox()
    {
        string s = TxtAddTarget.Text.Trim();
        if (s.Length == 0) return;
        bool domain = s.Contains('.') && !s.All(char.IsDigit) && Uri.CheckHostName(s) != UriHostNameType.Unknown;
        if (!domain && System.Net.IPAddress.TryParse(s, out _) == false)
        {
            TxtServerSummary.Text = "无效输入：请填 IPv4 地址或域名。";
            return;
        }
        var t = Probe.Add(s);
        _serverRows.Add(new ServerRowVm
        { Id = t.Id, Label = t.Label, Ip = t.Ip, Kind = KindText(t.Kind), Monitor = t.Monitor });
        TxtAddTarget.Text = "";
        UpdateServerSummary();
        AppendLog($"已添加服务器目标：{s}（域名将每次测量前解析）", LogLevel.Info);
    }

    private void BtnRemoveTarget_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) { TxtServerSummary.Text = "先在表格中选中一行。"; return; }
        Probe.Remove(row.Id);
        _serverRows.Remove(row);
        UpdateServerSummary();
    }

    // ---------------- 实时监控 ----------------

    private async void BtnMonitorRun_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (Probe.Monitoring) { await StopNetworkMonitorAsync(); return; }
        int n = _serverRows.Count(r => r.Monitor);
        if (n == 0) { TxtMonDetail.Text = "先在表格里勾选要监控的目标（「监控」列）。"; return; }
        Probe.Sample += OnProbeSample;
        Probe.StartMonitoring(ChkMonitorCsv.IsChecked == true);
        DotMonitor.Fill = BrushOk;
        TxtMonitorState.Text = $"监控中 · {n} 个目标 · 每 1 秒";
        BtnMonitorRun.Content = "停止监控";
        AppendLog($"网络实时监控已启动（{n} 个目标）。", LogLevel.Info);
    }

    private async Task StopNetworkMonitorAsync()
    {
        if (_probe is not null) _probe.Sample -= OnProbeSample;
        if (_probe is { Monitoring: true })
        {
            await _probe.StopMonitoringAsync();
            AppendLog("网络实时监控已停止。", LogLevel.Info);
        }
        DotMonitor.Fill = BrushIdle;
        TxtMonitorState.Text = "未监控";
        BtnMonitorRun.Content = "开始监控";
    }

    private void OnProbeSample(int id, int ms)
        => Dispatcher.BeginInvoke(() =>
        {
            var row = _serverRows.FirstOrDefault(r => r.Id == id);
            if (row is null) return;
            row.Ms = ms;
            if (ReferenceEquals(row, SelectedRow)) { UpdateMonDetail(row); DrawSpark(row); }
        });

    private void LvServers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRow is { } r) { UpdateMonDetail(r); DrawSpark(r); }
    }

    private void ChkMonitorTarget_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { DataContext: ServerRowVm r })
        {
            Probe.SetMonitor(r.Id, true);
            UpdateServerSummary();
        }
    }

    private void ChkMonitorTarget_Unchecked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { DataContext: ServerRowVm r })
        {
            Probe.SetMonitor(r.Id, false);
            UpdateServerSummary();
        }
    }

    private void UpdateMonDetail(ServerRowVm? row)
    {
        if (row is null) return;
        var s = Probe.StatsFor(row.Id);
        string cur = s.Current < 0 ? "×" : $"{s.Current} ms";
        string minmax = s.Min < 0 ? "—" : $"{s.Min} / {s.Avg:0} / {s.Max} ms";
        TxtMonDetail.Text = $"{row.Label}  当前 {cur} · 丢包率 {s.LossPct:0.0}% · 最小/平均/最大 {minmax} · 样本 {s.Samples.Count}";
    }

    // 最近样本折线：横轴时间，纵轴延迟（0 到 max(200ms, 实测峰)），失败点画短竖线
    private void DrawSpark(ServerRowVm row)
    {
        CvSpark.Children.Clear();
        var samples = Probe.StatsFor(row.Id).Samples.ToArray();
        if (samples.Length < 2) return;
        double w = CvSpark.ActualWidth;
        if (w < 10) w = 520;
        double h = CvSpark.ActualHeight;
        int peak = Math.Max(200, samples.Where(v => v > 0).DefaultIfEmpty(200).Max());
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            for (int i = 0; i < samples.Length; i++)
            {
                double x = w * i / (samples.Length - 1);
                int v = samples[i];
                if (v < 0) { started = false; continue; }
                double y = h - 2 - (h - 6) * Math.Min(1.0, v / (double)peak);
                if (!started) { ctx.BeginFigure(new System.Windows.Point(x, y), false, false); started = true; }
                else ctx.LineTo(new System.Windows.Point(x, y), true, false);
            }
        }
        geo.Freeze();
        CvSpark.Children.Add(new Path
        {
            Data = geo,
            Stroke = (Brush)Application.Current.Resources["AccentGlow"],
            StrokeThickness = 1.5,
        });
    }
}
