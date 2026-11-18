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
/// 工具动作：工具层的「按钮语义」。GUI 只按 (工具 id, 动作) 调用，
/// 具体怎么实现留在工具自己文件里——加一个按钮不改界面逻辑，加一个动作只改一个工具。
/// </summary>
public enum ToolAction
{
    /// <summary>启动游戏本体（已在运行则不重复拉起）。</summary>
    LaunchGame,
    /// <summary>打开游戏自带的设置工具（Settings.exe）。</summary>
    OpenSettingsTool,
    /// <summary>在资源管理器中打开游戏安装目录。</summary>
    OpenGameDirectory,
    /// <summary>窗口防卡死：把跑出屏幕的游戏窗口拉回来。</summary>
    RescueWindow,
    /// <summary>在资源管理器里打开一个文件/文件夹。</summary>
    OpenPath,
    /// <summary>用默认浏览器/程序打开一个 URL。</summary>
    OpenUrl,
    /// <summary>在资源管理器里定位并选中一个文件。</summary>
    RevealPath,
}

