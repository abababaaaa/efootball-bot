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
/// 引擎的「非模块模式」：不是挂机模块，不进注册表，也不接管手柄。
/// </summary>
public static class EngineModes
{
    /// <summary>纯感知观察（--console / 排障用）：只观察不操作，任何模块都不会被创建。</summary>
    public const string Observe = "observe";
}

/// <summary>
/// 全部模块 id 的唯一定义处（= 管道协议里 start 命令的 mode 字符串，也是配置里
/// AfterCompleteModule 的取值）。**任何地方都不许再写这些字面量**：
/// 加/改/删一个模块只动 <see cref="BotModuleCatalog"/> 与本文件，
/// GUI、引擎、配置校验、接力白名单自动跟着变。
/// </summary>
public static class ModuleIds
{
    // ---- 国际服 ----
    /// <summary>国际服·我的联赛（含满级换人巡检）。</summary>
    public const string IntlMyLeague = "intl";
    /// <summary>国际服·周活动（活动地图/挑战/巡回）。</summary>
    public const string IntlWeekly = "intlweekly";
    /// <summary>国际服·AI 天梯联赛。</summary>
    public const string IntlAiLadder = "intlladder";
    /// <summary>国际服·种马购买。</summary>
    public const string IntlStudBuy = "intlstudbuy";
    /// <summary>国际服·种马培训（筛选 + 技巧训练）。</summary>
    public const string IntlStudTrain = "intlstudtrain";
    /// <summary>国际服·满级换人测试（只跑一次巡检即自动停）。</summary>
    public const string IntlSwapTest = "intlswaptest";
    /// <summary>国际服·训练帧采集（自动导航到主要页面抓训练样本后停止，开发者选项用）。</summary>
    public const string IntlTrainFrames = "intltrainframes";
    /// <summary>国际服·赛前快捷设置（工具页，不是挂机场景）。</summary>
    public const string IntlPreMatch = "intlprematch";

    // ---- 国服 ----
    /// <summary>国服·天梯赛（天梯巅峰赛）。</summary>
    public const string CnLadder = "cndaily";
    /// <summary>国服·周活动。</summary>
    public const string CnWeekly = "cnweekly";
    /// <summary>国服·我的联赛。</summary>
    public const string CnMyLeague = "cnmyleague";
    /// <summary>国服·赛前快捷设置（工具页，不是挂机场景）。</summary>
    public const string CnPreMatch = "cnprematch";
}





