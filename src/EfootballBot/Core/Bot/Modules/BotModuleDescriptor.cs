// ===========================================================================
// 架构锁 · 注册表层 Core/Bot/Modules（v7.2）
// 本层职责：模块清单 —— id / 服务器分区 / 场景或工具 / 工厂 / 是否可接力。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 模块 id 字面量只许出现在本层；别处一律用 ModuleIds 常量
//   · 具体场景只许在本层 new（BotModuleCatalog 是唯一工厂入口）
//   · 本层不写业务逻辑，不引用任何场景的内部状态
// 改写法前先读 docs/MODULES.md §7 与 AGENTS.md；除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Modules;

/// <summary>
/// 一个挂机模块的全部元信息。注册表（<see cref="BotModuleCatalog"/>）里的每条记录就是
/// 一个模块的「身份证」：属于哪个服、叫什么、是不是场景、怎么造、能否被接力。
/// 新增模块 = 在注册表里加一条，别处不改。
/// </summary>
public sealed class BotModuleDescriptor
{
    /// <summary>模块 id（= 管道 mode 字符串，见 <see cref="ModuleIds"/>）。</summary>
    public required string Id { get; init; }
    /// <summary>所属服务器分区。国服模块只认国服窗口，国际服只认国际服窗口。</summary>
    public required ServerRegion Server { get; init; }
    /// <summary>场景 / 工具页。</summary>
    public required ModuleKind Kind { get; init; }
    /// <summary>界面与日志里的名字（也用作场景日志前缀）。</summary>
    public required string DisplayName { get; init; }
    /// <summary>一句话说明（GUI 提示、文档、日志可用）。</summary>
    public string Summary { get; init; } = "";
    /// <summary>
    /// 场景工厂：引擎建会话后按 id 造场景，参数是（引擎, 日志回调）。
    /// 工具页为 null。
    /// </summary>
    public Func<BotEngine, Action<string, LogLevel>, IScenario>? Factory { get; init; }
    /// <summary>
    /// 是否允许作为「全部完成后接力」的目标。默认 false：只有把全部内容刷完才有意义的
    /// 模块（我的联赛/AI 天梯/天梯巅峰赛）才配 true；周活动本身、种马、换人测试都不配。
    /// </summary>
    public bool HandoffEligible { get; init; }
    /// <summary>是否场景模块（= 引擎要接虚拟手柄、要跑决策循环）。</summary>
    public bool IsScenario => Kind == ModuleKind.Scenario && Factory is not null;

    public override string ToString() => $"{Id}({Server.Display()}·{DisplayName})";
}





