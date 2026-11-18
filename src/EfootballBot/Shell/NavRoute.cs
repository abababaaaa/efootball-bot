using System.Windows;

namespace EfootballBot.Shell;

/// <summary>
/// 上栏一级分区（首页 / 挂机 / 工具 / 关于）。
/// 新增一个分区 = 这里加一个值 + MainWindow.Shell.cs 的 _sectionMembers 加一行 + XAML 加一个上栏页签。
/// </summary>
public enum TopSection { Home, Game, Tools, About }

/// <summary>
/// 左栏导航模式：一个值 = 一个「页面」。这是壳里唯一需要登记的 id，
/// 页面名叫什么、面板是哪个、左栏按钮亮不亮，全部由 <see cref="NavRoute"/> 那一行数据决定。
/// </summary>
public enum NavMode { Home, Game, Settings, Update, GitHub, Network, LogViewer, Feedback, About }

/// <summary>
/// 面板插槽：一条路由下可以有多个互斥面板（例如游戏模块的国际服 / 国服）。
/// <see cref="IsActive"/> 为 null = 恒激活；否则每次导航时求值，true 的那个显示、其余隐藏。
/// </summary>
public sealed class PanelSlot
{
    public required FrameworkElement Panel { get; init; }
    public Func<bool>? IsActive { get; init; }
}

/// <summary>
/// 一条导航路由：把一个页面在壳里需要的全部接线收成一行数据 ——
/// 中栏面板、左栏高亮按钮、三级页签容器、一级分区归属、进入时刷新动作。
/// 以前这些散落在 NavigateTo 的 120 行手写 Visibility 里，加一页要改六处。
/// </summary>
public sealed class NavRoute
{
    public required NavMode Mode { get; init; }
    public required TopSection Section { get; init; }

    /// <summary>中栏面板（互斥，按 IsActive 选一个显示）。</summary>
    public required PanelSlot[] Panels { get; init; }

    /// <summary>左栏三级页签容器（可选）。</summary>
    public PanelSlot[] SubMenus { get; init; } = [];

    /// <summary>左栏工具按钮：进入本页时打 Tag=Active（NavToolBtn 模板据此高亮）。</summary>
    public FrameworkElement? RailButton { get; init; }

    /// <summary>进入本页要跑的一次性刷新（重扫日志文件、刷新网络页状态…）。</summary>
    public Action? OnEnter { get; init; }
}

/// <summary>
/// --page 深链别名：把命令行/文档里的短名映射到 <see cref="NavMode"/>，
/// 让「打开应用直接停在某一页」可以脚本化（截图回归、QA 复现、开发调试）。
/// </summary>
public static class NavKey
{
    public static readonly IReadOnlyDictionary<string, NavMode> Aliases =
        new Dictionary<string, NavMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = NavMode.Home,
            ["game"] = NavMode.Game,
            ["play"] = NavMode.Game,
            ["settings"] = NavMode.Settings,
            ["setting"] = NavMode.Settings,
            ["update"] = NavMode.Update,
            ["github"] = NavMode.GitHub,
            ["network"] = NavMode.Network,
            ["net"] = NavMode.Network,
            ["logs"] = NavMode.LogViewer,
            ["log"] = NavMode.LogViewer,
            ["feedback"] = NavMode.Feedback,
            ["about"] = NavMode.About,
        };

    /// <summary>解析 --page 的值（支持 "settings" 与带二级段的 "settings:display"）。</summary>
    public static bool TryParse(string? raw, out NavMode mode, out string? sub)
    {
        mode = NavMode.Home;
        sub = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        string key = raw.Trim();
        int colon = key.IndexOf(':');
        if (colon > 0)
        {
            sub = key[(colon + 1)..].Trim().ToLowerInvariant();
            key = key[..colon];
        }
        return Aliases.TryGetValue(key, out mode);
    }
}
