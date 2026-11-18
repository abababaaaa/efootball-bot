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
/// 工具执行结果：界面只需要把 <see cref="Message"/> 贴到状态行、把 <see cref="Ok"/>
/// 决定日志级别。<see cref="Path"/> 是「这次处理到的实际路径」（定位到的 exe/目录），
/// 供界面回显或后续动作复用。
/// </summary>
/// <param name="Ok">是否成功（false 只影响日志级别与状态行配色，不抛异常）。</param>
/// <param name="Message">给人看的一句话（界面状态行直接用）。</param>
/// <param name="Path">处理到的路径（无则为 null）。</param>
public sealed record ToolResult(bool Ok, string Message, string? Path = null)
{
    public static ToolResult Success(string message, string? path = null) => new(true, message, path);
    public static ToolResult Fail(string message, string? path = null) => new(false, message, path);
}

