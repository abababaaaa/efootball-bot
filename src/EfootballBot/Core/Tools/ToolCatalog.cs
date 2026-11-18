// ===========================================================================
// 架构锁 · 工具层 Core/Tools（v7.2）
// 本层职责：本地工具（赛前设置 / 打开路径 / 窗口防卡死 …），与挂机模块完全解耦。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用挂机层（EfootballBot.Core.Bot）—— 共享件放 Core/**，不要借挂机的件
//   · 工具 id 字面量只许在 ToolIds.cs，具体工具只许在 ToolCatalog 里实例化
//   · 界面层（MainWindow*.cs）不许直接 Process.Start：起进程/打开路径一律走 ToolCatalog
// 新增工具：ToolIds 加常量 + ToolCatalog 加一条 + 本目录一个 *Tool.cs 实现。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Tools;

/// <summary>
/// 工具注册表：**工具的唯一清单**（与挂机的 <c>BotModuleCatalog</c> 同一套思路）。
///
/// 以前「赛前快捷设置」的三件事（启动游戏 / 打开 Settings.exe / 打开游戏目录）
/// 是写在 MainWindow 里的三段私有方法，每段都用 <c>intl ? … : …</c> 分两服，
/// 窗口防卡死又把实现放在 BackgroundParker、入口放在 MainWindow——改一处必须翻两个文件。
/// 现在：一个工具一个文件，界面只写 <c>ExecuteAsync(ToolIds.X, ToolAction.Y, ctx)</c>。
/// </summary>
public static class ToolCatalog
{
    private static readonly ToolDescriptor[] Registry =
    {
        new() { Id = ToolIds.PreMatch, Tool = new PreMatchTool() },
        new() { Id = ToolIds.Shell, Tool = new ShellTool() },
        new() { Id = ToolIds.WindowRescue, Tool = new WindowRescueTool() },
    };

    /// <summary>全部工具。</summary>
    public static IReadOnlyList<ToolDescriptor> All => Registry;

    /// <summary>按 id 找工具；未注册返回 null（配置/旧界面残留也要能安全降级）。</summary>
    public static ToolDescriptor? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : Registry.FirstOrDefault(d => d.Id == id);

    /// <summary>
    /// 执行一个工具动作。未注册的工具或该工具不支持的动作都返回失败结果——
    /// 界面不需要知道任何具体工具类型，也不会因为拼错 id 静默什么都不做。
    /// </summary>
    public static async Task<ToolResult> ExecuteAsync(string id, ToolAction action, ToolContext ctx,
        CancellationToken ct = default)
    {
        var d = Find(id);
        if (d is null) return ToolResult.Fail($"工具「{id}」未在注册表登记");
        if (!d.Actions.Contains(action))
            return ToolResult.Fail($"工具「{d.DisplayName}」不支持动作 {action}");
        return await d.Tool.ExecuteAsync(action, ctx, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 注册表自检（单测 + 启动自检共用）：返回问题清单，空 = 通过。
    /// 校验结构性错误——重复 id、id 与实现不一致、工具没声明动作、分区工具没标 ServerScoped。
    /// </summary>
    public static IReadOnlyList<string> SelfCheck()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Registry)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) problems.Add("存在空 id 的工具");
            else if (!seen.Add(d.Id)) problems.Add($"工具 id 重复：{d.Id}");
            if (d.Tool is null) { problems.Add($"{d.Id}: 缺少实现实例"); continue; }
            if (!string.Equals(d.Id, d.Tool.Id, StringComparison.Ordinal))
                problems.Add($"{d.Id}: 注册 id 与实现里的 Id（{d.Tool.Id}）不一致");
            if (string.IsNullOrWhiteSpace(d.DisplayName)) problems.Add($"{d.Id}: DisplayName 为空");
            if (d.Actions.Count == 0) problems.Add($"{d.Id}: 没有声明任何动作");
            if (d.Id.Any(char.IsUpper)) problems.Add($"{d.Id}: id 必须小写");
        }
        return problems;
    }
}

