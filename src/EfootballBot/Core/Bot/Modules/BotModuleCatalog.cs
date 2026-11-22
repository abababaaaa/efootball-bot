using EfootballBot.Core.Bot.Scenarios;
using EfootballBot.Core.Bot.Scenarios.AiLadder;
using EfootballBot.Core.Bot.Scenarios.CnMyLeague;
using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;
using EfootballBot.Core.Bot.Scenarios.Stud;
using EfootballBot.Core.Bot.Scenarios.Weekly;

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
/// 挂机模块注册表：**全系统唯一的模块清单**。
///
/// 它替掉了此前散落在四个地方的字符串魔法：
/// <list type="bullet">
/// <item>引擎里 <c>mode is "cndaily" or "cnweekly" ...</c> 的场景模式白名单；</item>
/// <item>引擎里 <c>cn is "cndaily" or ...</c> 的服务器判定；</item>
/// <item>引擎里 <c>CreateScenario</c> 的 switch 工厂；</item>
/// <item>引擎与设置页各一份的「接力目标白名单」（Intl/CnHandoffTargets、WeeklyAfterModules）。</item>
/// </list>
/// 现在加一个模块只有两步：<see cref="ModuleIds"/> 加一个 id，本表加一条记录。
/// 引擎、GUI 按钮接线、接力下拉、跨服校验全部自动生效——不会「改一处漏三处」。
///
/// 分层约定（谁都不许越层）：
/// <code>
/// 国服 / 国际服（ServerRegion）
///   ├─ 赛前设置（工具页：不接引擎）
///   ├─ 天梯赛 / AI 天梯联赛
///   ├─ 周活动
///   └─ 我的联赛（含满级换人）
/// 共享层 Core/Bot/Match/：界面判定、决策、赛中匹配、赛后弹窗、流程记忆
/// </code>
/// 模块之间不互相引用；需要共用的一律下沉到 <c>Core/Bot/Match</c>。
/// </summary>
public static class BotModuleCatalog
{
    /// <summary>注册表（顺序 = GUI 里的模块顺序）。新增模块在这里加一条。</summary>
    private static readonly BotModuleDescriptor[] Registry =
    {
        // ---------------- 国际服 ----------------
        new()
        {
            Id = ModuleIds.IntlMyLeague, Server = ServerRegion.Intl, Kind = ModuleKind.Scenario,
            DisplayName = "我的联赛",
            Summary = "国际服我的联赛：道具核验/难度选择/开赛/满级换人巡检",
            HandoffEligible = true,
            Factory = (engine, log) => new MyLeagueScenario(engine, log),
        },
        new()
        {
            Id = ModuleIds.IntlWeekly, Server = ServerRegion.Intl, Kind = ModuleKind.Scenario,
            DisplayName = "周活动",
            Summary = "国际服周活动：活动列表/介绍页/挑战清单/阵容挑战(Team Effort)/赛后活动地图",
            Factory = (engine, log) => new WeeklyEventScenario(engine, log),
        },
        new()
        {
            Id = ModuleIds.IntlAiLadder, Server = ServerRegion.Intl, Kind = ModuleKind.Scenario,
            DisplayName = "AI天梯联赛",
            Summary = "国际服 AI 天梯联赛：组别主页→比赛循环",
            HandoffEligible = true,
            Factory = (engine, log) => new AiLadderScenario(engine, log),
        },
        new()
        {
            Id = ModuleIds.IntlStudBuy, Server = ServerRegion.Intl, Kind = ModuleKind.Scenario,
            DisplayName = "种马购买",
            Summary = "国际服种马购买：合同页筛选/购买/到手确认",
            Factory = (engine, log) => new StudTrainingScenario(engine, log, training: false),
        },
        new()
        {
            Id = ModuleIds.IntlStudTrain, Server = ServerRegion.Intl, Kind = ModuleKind.Scenario,
            DisplayName = "种马培训",
            Summary = "国际服种马培训：筛选 + 技巧训练",
            Factory = (engine, log) => new StudTrainingScenario(engine, log, training: true),
        },
        new()
        {
            Id = ModuleIds.IntlSwapTest, Server = ServerRegion.Intl, Kind = ModuleKind.Scenario,
            DisplayName = "满级换人测试",
            Summary = "只跑一次满级换人巡检即自动停止（排障用）",
            Factory = (engine, log) => new MyLeagueScenario(engine, log, swapTestOnly: true),
        },
        new()
        {
            Id = ModuleIds.IntlPreMatch, Server = ServerRegion.Intl, Kind = ModuleKind.Tool,
            DisplayName = "赛前快捷设置",
            Summary = "启动国际服本体/打开游戏自带设置工具/打开游戏目录",
        },
        // ---------------- 国服 ----------------
        new()
        {
            Id = ModuleIds.CnLadder, Server = ServerRegion.Cn, Kind = ModuleKind.Scenario,
            DisplayName = "天梯巅峰赛",
            Summary = "国服天梯赛：主页→模式选择→参赛弹窗→单场循环（场次为 0 自动停）",
            HandoffEligible = true,
            Factory = (engine, log) => new PeakLadderScenario(engine, log),
        },
        new()
        {
            Id = ModuleIds.CnWeekly, Server = ServerRegion.Cn, Kind = ModuleKind.Scenario,
            DisplayName = "周活动",
            Summary = "国服周活动：杯赛/巡回/挑战/教练签下链",
            Factory = (engine, log) => new CnWeeklyScenario(engine, log),
        },
        new()
        {
            Id = ModuleIds.CnMyLeague, Server = ServerRegion.Cn, Kind = ModuleKind.Scenario,
            DisplayName = "我的联赛",
            Summary = "国服我的联赛：难度页/开赛/跳过比赛（对局获胜才跳）",
            HandoffEligible = true,
            Factory = (engine, log) => new CnMyLeagueScenario(engine, log),
        },
        new()
        {
            Id = ModuleIds.CnPreMatch, Server = ServerRegion.Cn, Kind = ModuleKind.Tool,
            DisplayName = "赛前快捷设置",
            Summary = "启动国服本体/打开游戏自带设置工具/打开游戏目录",
        },
    };
    /// <summary>全部模块（含工具页）。</summary>
    public static IReadOnlyList<BotModuleDescriptor> All => Registry;
    /// <summary>某个服的全部模块（GUI 分栏用）。</summary>
    public static IReadOnlyList<BotModuleDescriptor> By(ServerRegion server)
        => Registry.Where(d => d.Server == server).ToArray();
    /// <summary>某个服的全部挂机场景（不含工具页）。</summary>
    public static IReadOnlyList<BotModuleDescriptor> ScenariosOf(ServerRegion server)
        => Registry.Where(d => d.Server == server && d.IsScenario).ToArray();
    /// <summary>按 id 找模块；未注册返回 null（不抛异常——配置手改/旧版本残留也要能安全降级）。</summary>
    public static BotModuleDescriptor? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : Registry.FirstOrDefault(d => d.Id == id);
    /// <summary>id 是否是已注册的挂机场景（引擎要不要接手柄就看它）。</summary>
    public static bool IsScenarioMode(string? id) => Find(id)?.IsScenario == true;
    /// <summary>id 所属服务器；未注册返回 null（调用方按「不是本会话的模块」处理）。</summary>
    public static ServerRegion? ServerOf(string? id)
    {
        var d = Find(id);
        return d?.Server;
    }

    /// <summary>id 是否属于国服（未注册按国际服处理，与历史行为一致：默认走国际服窗口）。</summary>
    public static bool IsCn(string? id) => ServerOf(id) == ServerRegion.Cn;
    /// <summary>
    /// 按 id 造场景；未注册/工具页/工厂缺失都返回 null（调用方按「无效模式」处理，
    /// 绝不静默跑成另一个模块）。
    /// </summary>
    public static IScenario? Create(BotEngine engine, string? id, Action<string, LogLevel> log)
        => Find(id) is { IsScenario: true, Factory: { } factory } ? factory(engine, log) : null;
    /// <summary>
    /// 该服可接力的模块（全部完成后自动接力的候选）。<paramref name="excludeId"/> 用于把
    /// 「自己」排除掉（周活动不会接力到周活动）。
    /// </summary>
    public static IReadOnlyList<BotModuleDescriptor> HandoffCandidates(ServerRegion server, string? excludeId = null)
        => Registry.Where(d => d.HandoffEligible && d.Server == server && d.Id != excludeId).ToArray();
    /// <summary>
    /// 接力目标是否合法：必须是「本服」的「可接力」场景模块。
    /// 跨服一律拒绝——两服窗口与菜单互不通用，在国际服界面上跑国服导航会把游戏带飞。
    /// </summary>
    public static bool IsHandoffTarget(string? id, bool cn)
    {
        var d = Find(id);
        if (d is null || !d.HandoffEligible || !d.IsScenario) return false;
        return d.Server == (cn ? ServerRegion.Cn : ServerRegion.Intl);
    }

    /// <summary>
    /// 注册表自检（单测与启动自检共用）：返回问题清单，空 = 通过。
    /// 校验的是「结构性错误」——重复 id、场景缺工厂、工具页带工厂、接力目标跨服/非场景、
    /// 空名字。这些错误在运行期表现为「点了没反应」或「接力到别的服」，很难从日志看出来。
    /// </summary>
    public static IReadOnlyList<string> SelfCheck()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Registry)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) problems.Add("存在空 id 的模块");
            else if (!seen.Add(d.Id)) problems.Add($"模块 id 重复：{d.Id}");
            if (string.IsNullOrWhiteSpace(d.DisplayName)) problems.Add($"{d.Id}: DisplayName 为空");
            if (d.Kind == ModuleKind.Scenario && d.Factory is null) problems.Add($"{d.Id}: 场景模块缺少工厂");
            if (d.Kind == ModuleKind.Tool && d.Factory is not null) problems.Add($"{d.Id}: 工具页不应带场景工厂");
            if (d.HandoffEligible && !d.IsScenario) problems.Add($"{d.Id}: 接力目标必须是挂机场景");
            if (!string.IsNullOrEmpty(d.Id) && d.Id.Any(char.IsUpper)) problems.Add($"{d.Id}: id 必须小写（管道/配置契约）");
        }
        return problems;
    }
}




