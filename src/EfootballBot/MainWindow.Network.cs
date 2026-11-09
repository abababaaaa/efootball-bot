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

// 网络页：FastGithub 访问加速内核 + 镜像下载加速 + 服务器延迟测试 + 实时监控（每秒 tcping，丢包率与曲线）
public partial class MainWindow
{
    private NetworkProbe? _probe;
    private ObservableCollection<ServerRowVm> _serverRows = new();   // 国际服网络监测（游戏/更新/候选/手填 IP）
    private ObservableCollection<ServerRowVm> _githubRows = new();   // GitHub 链接优化（域名目标）
    private CancellationTokenSource? _measureCts;
    private bool _githubBusy;        // 下载内核 / 一键加速 / 停止还原 / 内核测速进行中
    private bool _githubMirrorBusy;  // 镜像测速进行中
    private bool _githubHooked;      // 内核自愈提示只订阅一次

    // 监控启动时选中的行（曲线跟随它；换行立即换曲线）
    private ServerRowVm? SelectedRow => LvServers.SelectedItem as ServerRowVm;

    // ---------------- 表格行模型 ----------------

    public sealed class ServerRowVm : INotifyPropertyChanged
    {
        public int Id { get; init; }
        public string Label { get; init; } = "";
        public string Kind { get; init; } = "";

        // 排序键：IP 补零成定长字符串（按数值从大到小才对）、延迟把「未测/无响应」沉到最后
        public string SortLabel => Label;
        public string SortIp => string.Join(".", Ip.Split('.').Select(p => p.PadLeft(3, '0')));
        public int SortMs => Ms == int.MinValue ? int.MaxValue : Ms < 0 ? int.MaxValue - 1 : Ms;

        private string _ip = "";
        public string Ip { get => _ip; set { _ip = value; Raise(nameof(Ip)); } }

        private bool _monitor;
        public bool Monitor { get => _monitor; set { _monitor = value; Raise(nameof(Monitor)); } }

        private string _delayText = "—";
        public string DelayText { get => _delayText; set { _delayText = value; Raise(nameof(DelayText)); } }

        private string _stateText = "";
        /// <summary>内核测速给出的可用性（可用 / 受限 / 不可用 / 失败）；空 = 未测。</summary>
        public string StateText { get => _stateText; set { _stateText = value; Raise(nameof(StateText)); } }

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
            {
                var row = new ServerRowVm
                { Id = t.Id, Label = t.Label, Ip = t.Ip, Kind = KindText(t.Kind), Monitor = t.Monitor };
                (t.Domain is not null ? _githubRows : _serverRows).Add(row);
            }
            LvGithub.ItemsSource = _githubRows;
            LvServers.ItemsSource = _serverRows;
            // 点表头排序（名称/IP/类型/延迟，再点一次反向）
            LvServers.AddHandler(System.Windows.Controls.Primitives.DataGridColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
            LvGithub.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
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
        InitFastGithubPage();
        UpdateServerSummary();
        RefreshLineState();
    }

    // ---------------- 对战线路（强制 P2P / 更新服加速） ----------------

    private bool _lineBusy;
    private string _lineNotice = "";   // 上一次操作的结果（与状态摘要一起显示，避免被刷新覆盖）

    /// <summary>实读状态（netsh 查规则 ~100ms，放后台）：规则是否存在、hosts 是否已改。</summary>
    private void RefreshLineState()
    {
        if (_lineBusy) return;
        _ = Task.Run(() => (P2P: ForcedP2P.IsEnabled, Tcp: ForcedP2P.TcpEnabled,
                Hosts: UpdateHostsOptimizer.IsApplied, Ip: UpdateHostsOptimizer.CurrentIp))
            .ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                var r = t.Result;
                BtnP2PToggle.Content = r.P2P ? "关闭强制 P2P" : "启用强制 P2P";
                DotP2P.Fill = r.P2P ? BrushOk : BrushIdle;
                if (r.P2P) ChkP2PTcp.IsChecked = r.Tcp;   // 规则已启用时回填实际状态
                TxtP2PState.Text = r.P2P
                    ? (r.Tcp ? "已启用（UDP + TCP）" : "已启用（仅 UDP）")
                    : "未启用";
                string status = r.Hosts
                    ? $"更新服加速：hosts 已指向 {r.Ip ?? "—"}。"
                    : "更新服加速：未应用。";
                TxtLineDetail.Text = _lineNotice.Length == 0 ? status : _lineNotice + "\n" + status;
            }));
    }

    private async void BtnP2PToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_lineBusy) return;
        _lineBusy = true;
        BtnP2PToggle.IsEnabled = false;
        try
        {
            bool on = await Task.Run(() => ForcedP2P.IsEnabled);
            string? hint = global::System.IO.Path.GetDirectoryName(_cfg.GamePaths.IntlSettingsTool ?? "");
            var (ok, msg) = await Task.Run(() =>
            {
                if (on) return ForcedP2P.Disable();
                string? exe = ForcedP2P.ResolveGameExe(hint);
                if (exe is null)
                    return (false, "未定位到国际服游戏本体（eFootball.exe）：先启动一次游戏，或先点「打开设置工具」形成路径记忆。");
                return ForcedP2P.Enable(exe, ChkP2PTcp.IsChecked == true);
            });
            _lineNotice = msg;
            AppendLog(ok ? $"强制 P2P：{msg}" : $"强制 P2P 失败：{msg}",
                ok ? LogLevel.Success : LogLevel.Warn);
        }
        finally
        {
            _lineBusy = false;
            BtnP2PToggle.IsEnabled = true;
            RefreshLineState();
        }
    }

    private async void BtnUpdateHosts_Click(object sender, RoutedEventArgs e)
    {
        if (_lineBusy) return;
        _lineBusy = true;
        BtnUpdateHosts.IsEnabled = false;
        try
        {
            TxtLineDetail.Text = "正在测速更新 CDN…";
            string result = await Task.Run(UpdateHostsOptimizer.Apply);
            _lineNotice = result.Replace('\n', ' ').Trim();
            TxtLineDetail.Text = _lineNotice;
            AppendLog("更新服加速：" + (result.Split('\n').LastOrDefault(l => l.Length > 0) ?? ""),
                result.Contains("失败") || result.Contains("未修改") ? LogLevel.Warn : LogLevel.Success);
        }
        finally
        {
            _lineBusy = false;
            BtnUpdateHosts.IsEnabled = true;
            RefreshLineState();
        }
    }

    private async void BtnUpdateHostsRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_lineBusy) return;
        _lineBusy = true;
        BtnUpdateHostsRestore.IsEnabled = false;
        try
        {
            var (ok, msg) = await Task.Run(UpdateHostsOptimizer.Restore);
            _lineNotice = msg;
            AppendLog(msg, ok ? LogLevel.Info : LogLevel.Warn);
        }
        finally
        {
            _lineBusy = false;
            BtnUpdateHostsRestore.IsEnabled = true;
            RefreshLineState();
        }
    }

    /// <summary>进入页面：订阅内核自愈提示 + 初始化镜像下拉 + 刷新卡片状态（幂等）。</summary>
    private void InitFastGithubPage()
    {
        if (!_githubHooked)
        {
            _githubHooked = true;
            FastGithubKernel.Notice += message => Dispatcher.BeginInvoke(() =>
            {
                AppendLog(message, LogLevel.Warn);
                RefreshFastGithubState();
            });
        }
        if (CbFastGithubMirror.ItemsSource is null)
        {
            CbFastGithubMirror.ItemsSource = FastGithubKernel.MirrorPrefixes;
            CbFastGithubMirror.Text = FastGithubKernel.DefaultMirror;
        }
        RefreshFastGithubState();
    }

    /// <summary>刷新加速卡状态：以 hosts 实读为准（内核进程可有可无，只改 hosts 就生效）。</summary>
    private void RefreshFastGithubState()
    {
        if (BtnFastGithubAction is null || _githubBusy) return;
        bool installed = FastGithubKernel.IsInstalled;
        int applied = FastGithubKernel.AppliedDomains.Count;

        BtnFastGithubAction.Content = !installed ? "下载内核" : applied > 0 ? "停止还原" : "一键加速";
        BtnFastGithubPanel.IsEnabled = installed;
        DotFastGithub.Fill = applied > 0 ? BrushOk : BrushIdle;
        TxtFastGithubState.Text = !installed
            ? "内核未下载"
            : applied > 0 ? $"已加速 · 接管 {applied} 个域名" : "未加速";
        TxtFastGithubStatus.Text = applied > 0
            ? $"hosts 已指向实测最快节点（内核 {FastGithubKernel.InstalledVersion}）；下载走镜像 {FastGithubKernel.DefaultMirror}"
            : "";
    }

    // 表头排序：两列映射到 VM 属性；延迟列把未测(-int.MinValue)排最后
    private string _sortKey = "";
    private bool _sortDesc;

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.DataGridColumnHeader { Column: { } dcol }) return;
        // 表头带过 ▲/▼ 标记，映射前先剥掉
        string header = (dcol.Header as string ?? "").TrimEnd('▲', '▼', ' ');
        string? key = header switch
        {
            "名称" or "域名" => "SortLabel",
            "IP" => "SortIp",
            "类型" => "Kind",
            "延迟" => "SortMs",
            _ => null,
        };
        if (key is null) return;
        var list = (System.Windows.Controls.ItemsControl)LvServers;
        if (_sortKey == key) _sortDesc = !_sortDesc; else { _sortKey = key; _sortDesc = false; }
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(list.ItemsSource);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(key,
            _sortDesc ? System.ComponentModel.ListSortDirection.Descending
                      : System.ComponentModel.ListSortDirection.Ascending));
        view.Refresh();

        // 表头小箭头：当前排序列显示 ▲/▼
        foreach (var c in LvServers.Columns)
        {
            string baseText = (c.Header as string ?? "").TrimEnd('▲', '▼', ' ');
            c.Header = ReferenceEquals(c, dcol) ? baseText + (_sortDesc ? " ▼" : " ▲") : baseText;
        }
    }

    // ---------------- FastGithub 访问加速 ----------------

    /// <summary>主按钮三态：未装 → 下载内核；已装未加速 → 一键加速；已加速 → 停止还原。</summary>
    private async void BtnFastGithubAction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_githubBusy) return;
        _githubBusy = true;
        BtnFastGithubAction.IsEnabled = false;
        try
        {
            if (!FastGithubKernel.IsInstalled)
            {
                TxtFastGithubStatus.Text = "正在下载 FastGithub 内核（镜像链，约 38 MB）…";
                AppendLog("开始下载 FastGithub 内核…", LogLevel.Info);
                var progress = new Progress<string>(text => TxtFastGithubStatus.Text = text);
                await FastGithubKernel.DownloadAsync(progress);
                AppendLog($"FastGithub 内核已就绪（{FastGithubKernel.InstalledVersion}）", LogLevel.Success);
                TxtFastGithubStatus.Text = "内核就绪，点「一键加速」开始测速并写 hosts。";
            }
            else if (FastGithubKernel.IsApplied)
            {
                TxtFastGithubStatus.Text = "正在还原 hosts…";
                var (ok, message) = await FastGithubKernel.RestoreAsync();
                TxtFastGithubStatus.Text = message;
                AppendLog(message, ok ? LogLevel.Success : LogLevel.Error);
            }
            else
            {
                TxtFastGithubStatus.Text = "正在测速 21 个 GitHub 域名并写 hosts（约 20–60 秒）…";
                AppendLog("FastGithub 开始测速并择优写入 hosts…", LogLevel.Info);
                var result = await FastGithubKernel.ApplyAsync();
                TxtFastGithubStatus.Text = result.Message;
                AppendLog(result.Message, result.Ok ? LogLevel.Success : LogLevel.Error);
                ApplyFastGithubNodes(result.Nodes);
            }
        }
        catch (OperationCanceledException) { TxtFastGithubStatus.Text = "操作已取消。"; }
        catch (Exception ex)
        {
            TxtFastGithubStatus.Text = $"失败：{ex.Message}";
            AppendLog($"FastGithub 操作失败：{ex.Message}", LogLevel.Error);
        }
        finally
        {
            _githubBusy = false;
            BtnFastGithubAction.IsEnabled = true;
            RefreshFastGithubState();
        }
    }

    /// <summary>打开内核自带面板（可视化测速 / 镜像开关），与本页共用同一份 hosts 与配置。</summary>
    private void BtnFastGithubPanel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!FastGithubKernel.IsInstalled)
        {
            TxtFastGithubStatus.Text = "先下载内核，再打开面板。";
            return;
        }
        FastGithubKernel.OpenPanel();
        TxtFastGithubStatus.Text = "已打开 FastGithub 面板：面板里的加速/还原与本页操作的是同一份 hosts。";
    }

    /// <summary>内核自检（--selftest，只测速不改 hosts）：TCP 初筛 + HTTPS 复验，结果回填延迟表。</summary>
    private async void BtnFastGithubProbe_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_githubBusy) return;
        if (!FastGithubKernel.IsInstalled)
        {
            TxtGithubSummary.Text = "先下载内核再测速";
            return;
        }
        _githubBusy = true;
        BtnFastGithubProbe.IsEnabled = false;
        try
        {
            TxtGithubSummary.Text = "内核测速中（TCP 初筛 + HTTPS 复验，约 20–60 秒）…";
            var outcome = await FastGithubKernel.ProbeAsync();
            ApplyFastGithubNodes(outcome.Nodes);
            TxtGithubSummary.Text = outcome.Message;
            AppendLog(outcome.Message, outcome.Nodes.Count > 0 ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            TxtGithubSummary.Text = "测速失败：" + ex.Message;
            AppendLog("FastGithub 内核测速失败：" + ex.Message, LogLevel.Warn);
        }
        finally
        {
            _githubBusy = false;
            BtnFastGithubProbe.IsEnabled = true;
        }
    }

    /// <summary>把内核探测/加速结果回填到域名表（按域名匹配，表里没有的域名忽略）。</summary>
    private void ApplyFastGithubNodes(IReadOnlyList<FastGithubKernel.FastGithubNode> nodes)
    {
        if (nodes.Count == 0) return;
        foreach (var node in nodes)
        {
            var row = _githubRows.FirstOrDefault(r =>
                string.Equals(r.Label, node.Host, StringComparison.OrdinalIgnoreCase));
            if (row is null) continue;
            row.Ip = node.Ip;
            row.Ms = (int)Math.Min(node.Ms, int.MaxValue);
            row.StateText = node.StateText;
        }
        UpdateGithubSummary();
    }

    // ---------------- 下载加速（镜像链） ----------------

    /// <summary>并行实测每个预设镜像（拉 Release 小样本的首块），最快者回填下拉，等用户确认再落配置。</summary>
    private async void BtnFastGithubMirrorProbe_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_githubMirrorBusy) return;
        _githubMirrorBusy = true;
        BtnFastGithubMirrorProbe.IsEnabled = false;
        try
        {
            TxtFastGithubMirrorState.Text = "镜像测速中…";
            var probes = await Task.WhenAll(
                FastGithubKernel.MirrorPrefixes.Select(prefix => FastGithubKernel.ProbeMirrorAsync(prefix)));
            TxtFastGithubMirrorState.Text = string.Join(" · ", probes.Select(p =>
                $"{MirrorHost(p.Prefix)} {(p.Ok ? p.Ms + " ms" : "不可用")}"));
            var best = probes.Where(p => p.Ok).OrderBy(p => p.Ms).FirstOrDefault();
            if (best is null)
            {
                AppendLog("镜像测速：全部不可用，下载将回退 GitHub 直连", LogLevel.Warn);
                return;
            }
            CbFastGithubMirror.Text = best.Prefix;
            AppendLog($"镜像测速：最快 {best.Prefix}（{best.Ms} ms），点「设为默认」生效", LogLevel.Success);
        }
        catch (Exception ex)
        {
            TxtFastGithubMirrorState.Text = "镜像测速失败：" + ex.Message;
        }
        finally
        {
            _githubMirrorBusy = false;
            BtnFastGithubMirrorProbe.IsEnabled = true;
        }
    }

    /// <summary>把下拉里的前缀落成默认镜像（写 %APPDATA%\FastGithub\mirror.txt，与内核面板共用）。</summary>
    private void BtnFastGithubMirrorApply_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        FastGithubKernel.DefaultMirror = CbFastGithubMirror.Text;
        CbFastGithubMirror.Text = FastGithubKernel.DefaultMirror;
        TxtFastGithubMirrorState.Text =
            $"下载加速已切到 {FastGithubKernel.DefaultMirror}（更新下载与内核下载同样生效）";
        AppendLog(TxtFastGithubMirrorState.Text, LogLevel.Success);
        RefreshFastGithubState();
    }

    private static string MirrorHost(string prefix)
    {
        try { return new Uri(prefix).Host; } catch { return prefix; }
    }

    // ---------------- 服务器测量 ----------------

    private async void BtnMeasureAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_measureCts is not null) return; // 测量进行中
        _measureCts = new CancellationTokenSource();
        BtnMeasureAll.IsEnabled = false;
        BtnMeasureCancel.IsEnabled = true;
        AppendLog("开始测量国际服服务器延迟…", LogLevel.Info);
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
        }), t => t.Domain is null);
        _measureCts.Dispose();
        _measureCts = null;
        BtnMeasureAll.IsEnabled = true;
        BtnMeasureCancel.IsEnabled = false;
        AppendLog(completed
            ? $"延迟测量完成（{n} 个目标）。"
            : "延迟测量已取消。", completed ? LogLevel.Success : LogLevel.Info);
        ((System.ComponentModel.ICollectionView)LvServers.Items).Refresh();
    }

    private async void BtnGithubMeasure_Click(object sender, RoutedEventArgs e)
    {
        if (_measureCts is not null) return;
        _measureCts = new CancellationTokenSource();
        BtnGithubMeasure.IsEnabled = false;
        int n = 0;
        var ct = _measureCts.Token;
        bool completed = await Probe.MeasureAllAsync(ct, (t, ms) => Dispatcher.BeginInvoke(() =>
        {
            var row = _githubRows.FirstOrDefault(r => r.Id == t.Id);
            if (row is null) return;
            row.Ip = t.Ip;
            row.Ms = ms;
            n++;
            UpdateGithubSummary();
        }), t => t.Domain is not null);
        _measureCts.Dispose();
        _measureCts = null;
        BtnGithubMeasure.IsEnabled = true;
        AppendLog(completed ? $"GitHub 域名测量完成（{n} 个）。" : "GitHub 域名测量已取消。",
            completed ? LogLevel.Success : LogLevel.Info);
    }

    private void UpdateGithubSummary()
    {
        int ok = _githubRows.Count(r => r.Ms >= 0);
        int slow = _githubRows.Count(r => r.Ms >= 200);
        TxtGithubSummary.Text = _githubRows.All(r => r.Ms == int.MinValue)
            ? ""
            : $"可达 {ok}/{_githubRows.Count} · 200ms 以上 {slow} 个";
    }

    private void BtnMeasureCancel_Click(object sender, System.Windows.RoutedEventArgs e)
        => _measureCts?.Cancel();

    private void UpdateServerSummary()
    {
        int measured = _serverRows.Count(r => r.Ms != int.MinValue);
        int ok = _serverRows.Count(r => r.Ms >= 0);
        TxtServerSummary.Text = $"已测 {measured}/{_serverRows.Count} · 可达 {ok}";
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
        var row = new ServerRowVm
        { Id = t.Id, Label = t.Label, Ip = t.Ip, Kind = KindText(t.Kind), Monitor = t.Monitor };
        (t.Domain is not null ? _githubRows : _serverRows).Add(row);
        TxtAddTarget.Text = "";
        UpdateServerSummary();
        AppendLog($"已添加目标：{s}（域名将每次测量前解析）", LogLevel.Info);
    }

    private void BtnRemoveTarget_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) { TxtServerSummary.Text = "先在表格中选中一行。"; return; }
        Probe.Remove(row.Id);
        _serverRows.Remove(row);
        UpdateServerSummary();
    }

    // ---------------- 游戏内连接监控（Npcap 抓包 + 每秒 ping） ----------------

    private InGameConnectionMonitor? _inGame;
    private ConnectionOverlay? _overlay;

    private void BtnInGameMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_inGame is { Running: true })
        {
            _inGame.Updated -= OnInGameUpdated;
            _inGame.Stop();
            DotInGame.Fill = BrushIdle;
            TxtInGameState.Text = "未启动";
            BtnInGameMonitor.Content = "启动监控";
            TxtIgDetail.Text = "已停止。";
            if (_overlay is { IsVisible: true }) _overlay.Hide();
            return;
        }

        _inGame ??= new InGameConnectionMonitor { RegionLookup = ip => Probe.RegionFor(ip) };
        if (!_inGame.Start(out var err))
        {
            DotInGame.Fill = (Brush)Application.Current.Resources["Danger"];
            TxtInGameState.Text = "启动失败";
            TxtIgDetail.Text = err;
            AppendLog(err, LogLevel.Warn);
            return;
        }
        _inGame.Updated += OnInGameUpdated;
        DotInGame.Fill = BrushOk;
        TxtInGameState.Text = "监控中（等比赛开始）";
        BtnInGameMonitor.Content = "停止监控";
        TxtIgDetail.Text = "正在抓包：进比赛后会自动认出服务器连接。";
        AppendLog("游戏内连接监控已启动（Npcap 抓包）。", LogLevel.Info);
    }

    private void BtnInGameOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is null)
        {
            _overlay = new ConnectionOverlay();
            _overlay.Closed += (_, _) => _overlay = null;
        }
        _overlay.Show();
        _overlay.Topmost = true;
    }

    private void OnInGameUpdated(ConnSnapshot s) => Dispatcher.BeginInvoke(() => ApplyInGame(s));

    private void ApplyInGame(ConnSnapshot s)
    {
        TxtInGameState.Text = s.State;
        DotInGame.Fill = s.DelayMs < 0 ? BrushIdle : s.DelayMs < 60 ? BrushOk
            : s.DelayMs < 150 ? (Brush)Application.Current.Resources["Warning"]
            : (Brush)Application.Current.Resources["Danger"];

        TxtIgDelay.Text = s.DelayMs < 0 ? "—" : s.DelayMs + " ms";
        TxtIgDelay.Foreground = DotInGame.Fill;
        TxtIgLoss.Text = s.LossPct <= 0.05 ? "0%" : $"{s.LossPct:0.#}%";
        TxtIgLoss.Foreground = s.LossPct < 1 ? BrushOk
            : s.LossPct < 5 ? (Brush)Application.Current.Resources["Warning"]
            : (Brush)Application.Current.Resources["Danger"];
        TxtIgRemote.Text = s.Remote;
        TxtIgRegion.Text = s.Region;
        TxtIgDetail.Text = $"最小/平均/最大 {(s.MinMs < 0 ? "—" : s.MinMs + "ms")} / "
            + $"{(s.AvgMs < 0 ? "—" : s.AvgMs + "ms")} / {(s.MaxMs <= 0 ? "—" : s.MaxMs + "ms")}"
            + $" · 样本 {s.Curve.Count}";

        DrawIgSpark(s.Curve);
        if (_overlay is { IsVisible: true }) _overlay.Update(s);
    }

    private void DrawIgSpark(IReadOnlyList<int> samples)
    {
        CvIgSpark.Children.Clear();
        if (samples.Count < 2) return;
        double w = CvIgSpark.ActualWidth;
        double h = CvIgSpark.ActualHeight;
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
        CvIgSpark.Children.Add(new Path
        {
            Data = geo,
            Stroke = BrushOk,
            StrokeThickness = 1.4,
        });
    }
}
