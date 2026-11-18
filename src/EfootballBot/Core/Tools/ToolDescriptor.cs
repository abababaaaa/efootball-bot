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
/// 一个工具在注册表里的身份证：id + 名字 + 说明 + 实例。
/// 界面按 id 找工具、按动作派发；界面代码里没有 switch，也没有 intl 布尔分支。
/// </summary>
public sealed class ToolDescriptor
{
    /// <summary>工具 id（= <see cref="ToolIds"/> 常量）。</summary>
    public required string Id { get; init; }

    /// <summary>工具实例（实现类唯一实例，无状态）。</summary>
    public required ITool Tool { get; init; }

    public string DisplayName => Tool.DisplayName;
    public string Summary => Tool.Summary;
    public IReadOnlyList<ToolAction> Actions => Tool.Actions;
    public bool ServerScoped => Tool.ServerScoped;

    public override string ToString() => $"{Id}({DisplayName})";
}

