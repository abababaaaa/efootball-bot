using System.Windows;
using EfootballBot.Shell;

namespace EfootballBot;

/// <summary>
/// 导航壳（表驱动）。
///
/// 旧写法：NavigateTo 里 120 行手写 Visibility —— 加一个页面要同时改 6 处
/// （NavMode 枚举、一级分区 Visibility 墙、三级页签 Visibility 墙、中栏面板墙、
///   左栏 Tag=Active 墙、各页 OnEnter 刷新），漏一处就是「点了没反应」或「两页叠在一起」。
///
/// 新写法：一个页面在壳里的全部接线 = <see cref="NavRoute"/> 一行数据，
/// 加页面只改这里（+ XAML 面板 + 左栏按钮）。分区的成员表也是数据，
/// 不认识的元素不会被别处顺手改掉。
/// </summary>
public partial class MainWindow
{
    /// <summary>一级分区 → 该分区才显示的左栏元素（分组标题、模块胶囊、分区专属按钮）。</summary>
    private Dictionary<TopSection, FrameworkElement[]> _sectionMembers = new();

    private Dictionary<NavMode, NavRoute>? _routes;

    private void EnsureRoutes()
    {
        if (_routes is not null) return;

        static PanelSlot Slot(FrameworkElement e, Func<bool>? on = null) => new() { Panel = e, IsActive = on };

        _routes = new NavRoute[]
        {
            // ---- 首页：右栏整块收起，中栏铺满 ----
            new()
            {
                Mode = NavMode.Home,
                Section = TopSection.Home,
                Panels = [Slot(HomePanel)],
                OnEnter = ApplyHomeLayout,
            },

            // ---- 挂机：国际服 / 国服两个面板互斥，三级页签也跟着换 ----
            new()
            {
                Mode = NavMode.Game,
                Section = TopSection.Game,
                Panels =
                [
                    Slot(PanelIntl, () => !IsCnSelected),
                    Slot(PanelCn, () => IsCnSelected),
                ],
                SubMenus =
                [
                    Slot(SubMenuIntl, () => !IsCnSelected),
                    Slot(SubMenuCn, () => IsCnSelected),
                ],
            },

            // ---- 设置：开发者页复用同一个面板，只是页签与左栏高亮不同 ----
            new()
            {
                Mode = NavMode.Settings,
                Section = TopSection.Tools,
                Panels = [Slot(SettingsPanel)],
                RailButton = BtnSettings,
                SubMenus = [Slot(SubMenuSettings, () => !_devMode)],
                OnEnter = ApplySettingsTabs,
            },

            new()
            {
                Mode = NavMode.Update,
                Section = TopSection.Tools,
                Panels = [Slot(UpdatePanel)],
                RailButton = BtnUpdate,
                SubMenus = [Slot(SubMenuUpdate)],
                OnEnter = () => { ShowSection("UpdatePanel", 0); UpTabAuto.IsChecked = true; },
            },

            new()
            {
                Mode = NavMode.GitHub,
                Section = TopSection.Tools,
                Panels = [Slot(GitHubPanel)],
                RailButton = BtnGitHub,
                SubMenus = [Slot(SubMenuGithub)],
                OnEnter = () =>
                {
                    ShowSection("GitHubPanel", 0);
                    GhTabPort.IsChecked = true;
                    InitNetworkPage();
                    RefreshFastGithubState();
                },
            },

            new()
            {
                Mode = NavMode.Network,
                Section = TopSection.Tools,
                Panels = [Slot(NetworkPanel)],
                RailButton = BtnNetwork,
                SubMenus = [Slot(SubMenuNetwork)],
                OnEnter = () =>
                {
                    ShowSection("NetworkPanel", 0);
                    NetTabPing.IsChecked = true;
                    InitNetworkPage();
                    RefreshFastGithubState();
                },
            },

            new()
            {
                Mode = NavMode.LogViewer,
                Section = TopSection.Tools,
                Panels = [Slot(LogViewerPanel)],
                RailButton = BtnLogViewer,
                OnEnter = RefreshLogFileList,
            },

            new()
            {
                Mode = NavMode.Feedback,
                Section = TopSection.Tools,
                Panels = [Slot(FeedbackPanel)],
                RailButton = BtnFeedback,
                SubMenus = [Slot(SubMenuFeedback)],
                OnEnter = () => { ShowSection("FeedbackPanel", 0); FbTabDiag.IsChecked = true; },
            },

            new()
            {
                Mode = NavMode.About,
                Section = TopSection.About,
                Panels = [Slot(PageAbout)],
                RailButton = BtnAbout,
            },
        }.ToDictionary(r => r.Mode);

        _sectionMembers = new Dictionary<TopSection, FrameworkElement[]>
        {
            [TopSection.Game] = [NavLabelGame, RbCn, RbIntl, BtnWindowRescue],
            [TopSection.Tools] =
                [NavLabelTools, BtnSettings, BtnDevTools, BtnUpdate, BtnGitHub, BtnNetwork, BtnLogViewer, BtnFeedback],
            [TopSection.About] = [NavLabelAbout, BtnAbout],
        };
    }

    /// <summary>
    /// 切页：一次调用把一级分区、中栏面板、左栏三级页签、右栏布局、左栏高亮全部落到目标状态。
    /// 数据来自 <see cref="_routes"/>，本方法不含任何「具体某个页面」的 if。
    /// </summary>
    private void NavigateTo(NavMode mode)
    {
        EnsureRoutes();
        var routes = _routes!;
        if (!routes.TryGetValue(mode, out var route)) return;

        _currentNav = mode;
        if (route.Section == TopSection.Tools) _lastToolNav = mode;
        if (mode != NavMode.Settings) _devMode = false;

        // ---------- 一级分区（上栏页签 + 左栏该分区的元素） ----------
        _suppressTopNav = true;
        TopNavHome.IsChecked = route.Section == TopSection.Home;
        TopNavGame.IsChecked = route.Section == TopSection.Game;
        TopNavTools.IsChecked = route.Section == TopSection.Tools;
        TopNavAbout.IsChecked = route.Section == TopSection.About;
        _suppressTopNav = false;

        foreach (var (section, members) in _sectionMembers)
        {
            var visibility = section == route.Section ? Visibility.Visible : Visibility.Collapsed;
            foreach (var el in members) el.Visibility = visibility;
        }

        // ---------- 中栏面板（全部先收起，再点亮当前页激活的插槽） ----------
        foreach (var r in routes.Values)
            foreach (var slot in r.Panels)
                slot.Panel.Visibility = Visibility.Collapsed;

        foreach (var slot in route.Panels)
            if (slot.IsActive?.Invoke() ?? true)
                slot.Panel.Visibility = Visibility.Visible;

        // ---------- 左栏三级页签（有任何一项可见才显示页签条） ----------
        foreach (var r in routes.Values)
            foreach (var slot in r.SubMenus)
                slot.Panel.Visibility = Visibility.Collapsed;

        bool anySubMenu = false;
        foreach (var slot in route.SubMenus)
        {
            if (!(slot.IsActive?.Invoke() ?? true)) continue;
            slot.Panel.Visibility = Visibility.Visible;
            anySubMenu = true;
        }
        SubMenuHost.Visibility = anySubMenu ? Visibility.Visible : Visibility.Collapsed;

        // ---------- 右栏布局 ----------
        if (mode == NavMode.Home) ApplyHomeLayout();
        else if (mode != NavMode.Game) SetRightPanelCollapsed(true);   // 工具页默认收起实时画面，保留展开条
        else SetRightPanelCollapsed(_rightUserCollapsed);

        // 底部实时日志卡：仅游戏模块显示（且用户开着运行日志）
        LogCard.Visibility = mode == NavMode.Game && _cfg.ShowRuntimeLog ? Visibility.Visible : Visibility.Collapsed;

        // ---------- 左栏高亮（NavToolBtn / ModuleRadio 模板识别 Tag=Active） ----------
        foreach (var r in routes.Values)
            if (r.RailButton is not null)
                r.RailButton.Tag = r.Mode == mode ? "Active" : null;

        // 设置 / 开发者共用一个面板：高亮跟着 _devMode 走
        BtnSettings.Tag = mode == NavMode.Settings && !_devMode ? "Active" : null;
        BtnDevTools.Tag = mode == NavMode.Settings && _devMode ? "Active" : null;

        // 模块胶囊：只有正在看游戏模块时才亮（模板要求 IsChecked && Tag==Active）
        RbCn.Tag = mode == NavMode.Game && IsCnSelected ? "Active" : null;
        RbIntl.Tag = mode == NavMode.Game && !IsCnSelected ? "Active" : null;

        // ---------- 进入本页的一次性刷新 ----------
        route.OnEnter?.Invoke();

        SyncSubTabs();

        // 设置页可见页签的选中态：镜像页签 SubRbSetImportant 是 Collapsed 的，
        // 若当前没有任何可见的设置页签被选中，才点亮第一项（首次进入的兜底）。
        // 不能每次进入都强制 SubRbImp1.IsChecked=true —— 那会覆盖用户上次选的页签，
        // 导致「画面与性能 / 手柄与输入 / 挂机与日志」切走再回来高亮丢失（用户反馈）。
        if (mode == NavMode.Settings && !_devMode)
        {
            bool anyVisibleChecked =
                SubRbImp1.IsChecked == true || SubRbImp2.IsChecked == true || SubRbImp3.IsChecked == true ||
                SubRbSetDisplay.IsChecked == true || SubRbSetInput.IsChecked == true || SubRbSetDaily.IsChecked == true;
            if (!anyVisibleChecked) SubRbImp1.IsChecked = true;
        }
    }

    /// <summary>设置页二级页签：恢复上一次选中的设置页签内容，开发者模式只显示开发者内容。</summary>
    private void ApplySettingsTabs()
    {
        if (_devMode)
        {
            SetTabImportant.Visibility = Visibility.Collapsed;
            SetTabDisplay.Visibility = Visibility.Collapsed;
            SetTabInput.Visibility = Visibility.Collapsed;
            SetTabDaily.Visibility = Visibility.Collapsed;
            SetTabDev.Visibility = Visibility.Visible;
            return;
        }

        string activeTag =
            SubRbImp1.IsChecked == true ? (SubRbImp1.Tag as string ?? "Imp1") :
            SubRbImp2.IsChecked == true ? (SubRbImp2.Tag as string ?? "Imp2") :
            SubRbImp3.IsChecked == true ? (SubRbImp3.Tag as string ?? "Imp3") :
            SubRbSetDisplay.IsChecked == true ? (SubRbSetDisplay.Tag as string ?? "RbSetDisplay") :
            SubRbSetInput.IsChecked == true ? (SubRbSetInput.Tag as string ?? "RbSetInput") :
            SubRbSetDaily.IsChecked == true ? (SubRbSetDaily.Tag as string ?? "RbSetDaily") :
            "Imp1";
        ApplySettingsSection(activeTag);
    }

    /// <summary>
    /// 深链：<c>EfootballBot.exe --page settings:display</c> 直接停在指定页面。
    /// 用于截图回归、QA 复现与调试；也支持 <c>game:cn</c> 指定国服 / 国际服视图。
    /// </summary>
    public void ShowPage(string page)
    {
        EnsureRoutes();
        if (!NavKey.TryParse(page, out var mode, out var sub)) return;

        if (mode == NavMode.Game && sub is "cn" or "国服")
        {
            RbCn.IsChecked = true;
            sub = null;
        }
        if (mode == NavMode.Settings && sub == "dev") _devMode = true;

        NavigateTo(mode);
        if (sub is null) return;

        // 深链切二级段后把可见页签同步过去：镜像页签 IsChecked 变了，可见那一排不会自己跟着亮
        // （SyncSubTabs 是镜像 → 可见的单向同步）。
        void SyncVisibleTab() => SyncSubTabs();

        switch (mode)
        {
            case NavMode.Settings:
                switch (sub)
                {
                    case "important": RbSetImportant.IsChecked = true; break;
                    case "display": RbSetDisplay.IsChecked = true; break;
                    case "input": RbSetInput.IsChecked = true; break;
                    case "daily": RbSetDaily.IsChecked = true; break;
                    case "dev": RbSetDaily.IsChecked = false; break;
                }
                SyncVisibleTab();
                break;

            case NavMode.Update:
                if (int.TryParse(sub, out var upIdx)) ShowSection("UpdatePanel", upIdx);
                break;

            case NavMode.GitHub:
                if (int.TryParse(sub, out var ghIdx)) ShowSection("GitHubPanel", ghIdx);
                break;

            case NavMode.Network:
                if (int.TryParse(sub, out var netIdx)) ShowSection("NetworkPanel", netIdx);
                break;

            case NavMode.Feedback:
                if (int.TryParse(sub, out var fbIdx)) ShowSection("FeedbackPanel", fbIdx);
                break;
        }
    }
}
