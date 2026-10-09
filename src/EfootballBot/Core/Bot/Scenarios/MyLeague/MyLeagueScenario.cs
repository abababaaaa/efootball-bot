using EfootballBot.Core.Input;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>
/// 我的联赛挂机场景（大重置第一个框架）：感知（YOLO 6 类 + OCR）→ MyLeagueBrain 决策
/// → 后台鼠标点击按钮（先移光标再点击）/ 手柄 B 逃生。
/// 骨架流程：主菜单找"我的联赛"入口 → 物品列表浏览 → 确认弹窗/下一步推进 → 比赛画面等待。
/// </summary>
public sealed class MyLeagueScenario : IScenario
{
    private readonly BotEngine _engine;
    private readonly Action<string, LogLevel> _log;
    private readonly BackgroundMouse _mouse = new();

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
        var screen = MyLeagueBrain.Classify(obs.Detections);

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

        var action = MyLeagueBrain.Decide(screen, obs.Detections, obs.Ocr, _streak);

        switch (action.Kind)
        {
            case MlActionKind.GamepadBack:
                _log("[我的联赛] 无特征界面连续多轮，按 B 逃生", LogLevel.Warn);
                await _engine.Pad.Back();
                break;

            case MlActionKind.Wait:
                break;

            default:
                var win = _engine.Window;
                if (win is null || obs.Frame.Width == 0)
                {
                    _log("[我的联赛] 窗口/帧不可用，跳过本轮点击", LogLevel.Warn);
                    return 1000;
                }
                // YOLO 框/OCR 行中心（归一化）→ 后台点击（内置 移动→按下→抬起）
                await _mouse.ClickNormalizedAsync(win.Handle, action.X, action.Y,
                    obs.Frame.Width, obs.Frame.Height, ct: ct);
                break;
        }
        return action.WaitMs;
    }
}
