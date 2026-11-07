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
