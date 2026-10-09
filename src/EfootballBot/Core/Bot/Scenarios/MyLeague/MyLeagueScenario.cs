using EfootballBot.Core.Input;

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>
/// 我的联赛挂机场景（大重置第一个框架）：感知（YOLO 6 类 + OCR）→ MyLeagueBrain 决策
/// → 全程模拟手柄操作（Dpad 导航 / A 确认 / B 逃生）。
/// 骨架流程：主菜单"比赛"卡片 → 比赛页进"我的联赛" → 签领奖励一路确认推进 → 比赛画面等待。
/// 国际服游戏不响应 PostMessage 后台鼠标，因此不使用鼠标，全部手柄输入。
/// </summary>
public sealed class MyLeagueScenario : IScenario
{
    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;

    private MlScreen _lastScreen = MlScreen.Unknown;
    private int _streak;

    public MyLeagueScenario(BotEngine engine, Action<string, LogLevel> log)
    {
        _engine = engine;
        _log = log;
    }

    public string Name => "我的联赛";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var screen = MyLeagueBrain.Classify(obs.Detections, obs.Ocr);

        if (screen == _lastScreen)
        {
            _streak++;
        }
        else
        {
            _streak = 0;
            _lastScreen = screen;
            _log($"[我的联赛] 界面：{screen}", LogLevel.Info);
        }

        var action = MyLeagueBrain.Decide(screen, obs.Ocr, _streak);

        switch (action.Kind)
        {
            case MlActionKind.PadEnterMatch:
                // 主菜单 → 底部卡片行 → 最左"比赛" → A（到头多按无害，v1 盲导航）
                _log("[我的联赛] 主菜单导航：下→左×6→A 进比赛页", LogLevel.Info);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.DpadRepeat(PadDir.Left, 6);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.PadConfirm:
                _log($"[我的联赛] 手柄 A：{action.Target}", LogLevel.Info);
                await _engine.Pad.Confirm();
                break;

            case MlActionKind.GamepadBack:
                _log("[我的联赛] 无特征界面连续多轮，按 B 逃生", LogLevel.Warn);
                await _engine.Pad.Back();
                break;

            case MlActionKind.Wait:
                break;
        }
        return action.WaitMs;
    }
}
