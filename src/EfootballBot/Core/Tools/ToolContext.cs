using EfootballBot.Core.Config;

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
/// 一次工具调用的上下文：调用方（界面）提供、工具只读。
/// 全部分区相关的东西都走 <see cref="Server"/>——工具内部不再出现 intl 布尔参数分支。
/// </summary>
/// <param name="Server">目标分区（国服/国际服）；与分区无关的工具忽略它。</param>
/// <param name="Config">应用配置（路径记忆写在 GamePaths 里，工具自己负责落盘）。</param>
/// <param name="Log">日志回调（统一由引擎/界面提供，工具不直接写文件）。</param>
/// <param name="TargetPath">Shell 类工具要操作的路径/URL。</param>
public sealed record ToolContext(
    ServerRegion Server,
    AppConfig Config,
    Action<string, LogLevel> Log,
    string? TargetPath = null)
{
    public ToolContext WithPath(string? path) => this with { TargetPath = path };
}

