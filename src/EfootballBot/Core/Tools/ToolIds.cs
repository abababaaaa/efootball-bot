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
/// 全部工具 id 的唯一定义处。**任何地方都不许再写这些字面量**：
/// 加/改一个工具只动本文件 + <see cref="ToolCatalog"/>，界面接线自动跟着变
/// （构建期 arch-guard 的 ARCH-TOOL-ID-LITERAL 会挡住字面量）。
/// </summary>
public static class ToolIds
{
    /// <summary>赛前快捷设置：启动游戏 / 打开 Settings.exe / 打开游戏目录（两服各自路径记忆）。</summary>
    public const string PreMatch = "prematch";

    /// <summary>本地 Shell 动作：打开路径、打开 URL、在资源管理器中定位文件。</summary>
    public const string Shell = "shell";

    /// <summary>窗口防卡死：把跑出屏幕/停靠后回不来的游戏窗口拉回主屏。</summary>
    public const string WindowRescue = "windowrescue";
}

