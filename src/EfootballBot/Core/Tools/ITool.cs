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
/// 工具契约：与挂机模块的 <c>IScenario</c> 同一套思路——界面只认识接口 + 注册表，
/// 不认识任何具体工具类型。工具之间互不引用，公共能力下沉到 <see cref="ToolPaths"/> 等共享件。
/// </summary>
public interface ITool
{
    /// <summary>工具 id（唯一来源 <see cref="ToolIds"/>）。</summary>
    string Id { get; }

    /// <summary>界面与日志里的名字。</summary>
    string DisplayName { get; }

    /// <summary>一句话说明（模块说明/文档用）。</summary>
    string Summary { get; }

    /// <summary>本工具支持的动作（注册表自检与界面可用性判断都看它）。</summary>
    IReadOnlyList<ToolAction> Actions { get; }

    /// <summary>是否按服务器分区区分（真 = 界面要传 <see cref="ServerRegion"/>）。</summary>
    bool ServerScoped { get; }

    /// <summary>执行一个动作。实现内部自行兜异常边界以外的所有判断，返回可读结果。</summary>
    Task<ToolResult> ExecuteAsync(ToolAction action, ToolContext ctx, CancellationToken ct = default);
}

