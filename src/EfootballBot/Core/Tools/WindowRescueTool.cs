using EfootballBot.Core.System;

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
/// 窗口防卡死（工具模块）：游戏窗口被留在屏幕外/停靠后回不来/找不到窗口时，
/// 按停靠记录还原原位置，否则居中到主屏。
///
/// 实现本来就在 <see cref="BackgroundParker.RescueGameWindow"/>（挂机停靠的同一套窗口操作），
/// 但入口散在 MainWindow 的按钮处理里、与挂机模块混在一起。这里把它收成工具层的一个动作：
/// 界面只调 <c>ToolCatalog.ExecuteAsync(ToolIds.WindowRescue, ToolAction.RescueWindow, ctx)</c>。
/// </summary>
public sealed class WindowRescueTool : ITool
{
    public string Id => ToolIds.WindowRescue;
    public string DisplayName => "窗口防卡死";
    public string Summary => "把跑出屏幕/停靠后回不来的游戏窗口拉回主屏（两服通用）";
    public bool ServerScoped => false;

    public IReadOnlyList<ToolAction> Actions { get; } = new[] { ToolAction.RescueWindow };

    public Task<ToolResult> ExecuteAsync(ToolAction action, ToolContext ctx, CancellationToken ct = default)
    {
        if (action != ToolAction.RescueWindow) return Task.FromResult(ToolResult.Fail($"窗口防卡死不支持动作 {action}"));
        try
        {
            BackgroundParker.RescueGameWindow(ctx.Log);
            return Task.FromResult(ToolResult.Success("已处理（详见日志：还原原位置 / 居中主屏 / 未发现窗口）"));
        }
        catch (Exception ex)
        {
            ctx.Log($"窗口防卡死失败：{ex.Message}", LogLevel.Error);
            return Task.FromResult(ToolResult.Fail($"处理失败：{ex.Message}"));
        }
    }
}

