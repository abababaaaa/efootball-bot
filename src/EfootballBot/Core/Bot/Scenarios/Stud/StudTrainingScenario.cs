// ===========================================================================
// 架构锁 · 模块层 Core/Bot/Scenarios（v7.2）
// 本层职责：单个挂机模块自己的菜单流程与私有状态机（只认自己的界面）。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 不许引用别的模块的 *Scenario（只读借用别人的 *Brain.Classify 认残留页面是允许的）
//   · 不许自己写场次记账 / 比赛内挂机节奏 —— 一律走 MatchPlay.Tick / IdleTapAsync
//   · 共享界面判定与决策一律用 Core/Bot/Match 的 MatchBrain，不要复制一份
// 新增模块：ModuleIds 加常量 + BotModuleCatalog 加一条记录，再写本目录的状态机。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.Stud;

/// <summary>
/// 国际服「种马球员培训（购买）」场景：购买 0GP 种马 / 培训筛选两条独立流程，
/// 全部通过虚拟手柄完成。具体步骤见 <see cref="StudFlowController"/>。
/// </summary>
public sealed class StudTrainingScenario : IScenario
{
    private readonly BotEngine _engine;

    private readonly Action<string, LogLevel> _log;

    private readonly StudFlowController _flow;

    public StudTrainingScenario(BotEngine engine, Action<string, LogLevel> log, bool training)
    {
        _engine = engine;
        _log = log;
        _flow = new StudFlowController(engine, log, training);
        Name = training ? "种马球员培训" : "种马球员购买";
        _log($"[种马] 场景已创建：{Name}（启动时已在游戏内任意界面也可接管）", LogLevel.Info);
    }

    public string Name { get; }
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        return await _flow.StepAsync(obs, ct);
    }
}





