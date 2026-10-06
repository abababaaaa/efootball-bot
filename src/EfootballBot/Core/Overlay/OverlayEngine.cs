using EfootballBot.Core.Action;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using Bot = EfootballBot.Core.Bot;

namespace EfootballBot.Core.Overlay;

/// <summary>覆盖层引擎：弹窗/断线/棋盘页检测与清除。从 BotEngine/ScenarioBase 搬运。</summary>
public sealed class OverlayEngine
{
    private readonly Bot.BotEngine _engine;
    private readonly ActionExecutor _actions;
    private readonly Pad _pad;
    private int _unknownStreak;

    public OverlayEngine(Bot.BotEngine engine, ActionExecutor actions, Pad pad)
    {
        _engine = engine;
        _actions = actions;
        _pad = pad;
    }

    public void ResetUnknown() => _unknownStreak = 0;

    /// <summary>
    /// 处理断线弹窗 / 通用确认框，返回 true 表示做过处理。
    /// </summary>
    public async Task<bool> HandlePopupsAsync(Bot.Observation o, CancellationToken ct)
    {
        if (o.Is(GameScreen.Disconnected))
        {
            _engine.LogInternal("检测到断线 / 通信错误弹窗，尝试确认返回…", Bot.LogLevel.Warn);
            await _pad.Confirm();
            await Task.Delay(2000, ct);
            return true;
        }
        if (o.Is(GameScreen.Dialog))
        {
            // 弹窗按钮可能不在默认焦点（如关键比赛弹窗默认焦点是「取消」，OK 是上方白底按钮），
            // PressButtonAsync 以蓝/青彩色焦点为准移动指针，亮白底不会被误判为聚焦
            if (!await _actions.PressButtonAsync(new[] { "ok", "确定", "确认", "下一步", "yes" }, ct))
            {
                await _pad.Confirm();
                await Task.Delay(700, ct);
            }
            return true;
        }
        return false;
    }

    /// <summary>处理加载 / 标题 / 奖励 / 断线等“路过型”画面。返回处理后最新观察。</summary>
    public async Task<Bot.Observation> PassThroughAsync(Bot.Observation o, CancellationToken ct)
    {
        int guard = 0;
        while (guard++ < 6)
        {
            ct.ThrowIfCancellationRequested();
            switch (o.Screen)
            {
                case GameScreen.Loading:
                    await _pad.Wait(1500, ct);
                    break;
                case GameScreen.TitleScreen:
                    _engine.LogInternal("标题画面，按 A 进入游戏", Bot.LogLevel.Info);
                    await _pad.Confirm();
                    await _pad.Wait(6000, ct);
                    break;
                case GameScreen.Rewards:
                    _engine.LogInternal("处理奖励弹窗", Bot.LogLevel.Debug);
                    await _pad.Confirm();
                    await _pad.Wait(900, ct);
                    break;
                case GameScreen.Dialog:
                    // 确认弹窗：走 PressButtonAsync（OK/确定 可能不在默认焦点上，需移动后点击）
                    _engine.LogInternal("处理确认弹窗（识别按钮→移动焦点→点击）", Bot.LogLevel.Debug);
                    if (!await _actions.PressButtonAsync(new[] { "ok", "确定", "确认", "下一步", "yes" }, ct))
                    {
                        await _pad.Confirm();
                        await _pad.Wait(700, ct);
                    }
                    break;
                case GameScreen.Disconnected:
                    await HandlePopupsAsync(o, ct);
                    await _pad.Wait(8000, ct);
                    break;
                case GameScreen.FullTime:
                    await _pad.Confirm();
                    await _pad.Wait(2500, ct);
                    break;
                case GameScreen.CampaignBoard:
                    o = await DismissCampaignBoardAsync(o, ct);
                    break;
                default:
                    return o;
            }
            o = await _engine.ObserveAsync(ct);
        }
        return o;
    }

    /// <summary>
    /// 国际服 International Match Campaign 棋盘格活动页（每场比赛结束后弹出）。
    /// 光标默认停在棋盘节点上，右下「下一步」是未选中白底，直接按 A 无效；
    /// 必须先按方向键右把光标移到按钮上（变青色发光）再按 A。
    /// 棋盘可能连续推多个奖励节点，循环处理直到离开该页。
    /// </summary>
    public async Task<Bot.Observation> DismissCampaignBoardAsync(Bot.Observation o, CancellationToken ct)
    {
        _engine.LogInternal("国际活动棋盘页：右移光标到「下一步」→ A 继续", Bot.LogLevel.Info);
        for (int i = 0; i < 12 && o.Is(GameScreen.CampaignBoard); i++)
        {
            ct.ThrowIfCancellationRequested();
            await _pad.Dpad(PadDir.Right);
            await _pad.Wait(800, ct);
            await _pad.Confirm();
            await _pad.Wait(2300, ct);
            o = await _engine.ObserveAsync(ct);
        }
        if (o.Is(GameScreen.CampaignBoard))
            _engine.LogInternal("活动棋盘页多次确认后仍未离开，交回外层处理", Bot.LogLevel.Warn);
        return o;
    }

    /// <summary>卡死恢复：优先找“下一步/确定/确认”按 A；否则只等待重观察（绝不盲按 B/回主页）。</summary>
    public async Task RecoverAsync(Bot.Observation o, CancellationToken ct)
    {
        _unknownStreak++;

        // 物品已过期弹窗：唯一按钮 OK 默认聚焦，OCR 可能漏识按钮文字，直接按 A 立刻确认
        if (o.Ocr.Contains("过期") || o.Ocr.ContainsJoined("过期") || o.Ocr.Contains("获取新物品"))
        {
            _engine.LogInternal("检测到「物品已过期」弹窗，立即确认", Bot.LogLevel.Warn);
            if (!await _actions.PressButtonAsync(new[] { "ok", "确定", "确认" }, ct))
            {
                await _pad.Confirm();
                await Task.Delay(700, ct);
            }
            return;
        }

        // 优先：发现「下一步 / OK / 确定 / 确认」→ 走 PressButtonAsync（识别→移动→点击）
        var next = o.Ocr.FindFirst("下一步") ?? o.Ocr.FindButton("ok")
                   ?? o.Ocr.FindFirst("确定") ?? o.Ocr.FindFirst("确认") ?? o.Ocr.FindButton("next");
        if (next is not null)
        {
            _engine.LogInternal($"未识别画面，但发现「{next.Text}」，识别并点击", Bot.LogLevel.Warn);
            if (!await _actions.PressButtonAsync(new[] { "下一步", "ok", "确定", "确认", "next" }, ct))
            {
                await _pad.Confirm();
                await Task.Delay(700, ct);
            }
            return;
        }

        // 直播画面已有草皮视觉判定、赛前过场在比赛流程内部处理，外层出现未知通常是
        // 加载/黑屏——只等待重观察，绝不盲按 B（可能取消匹配/误退）
        if (_unknownStreak % 3 == 1)
        {
            _engine.LogInternal("未识别画面，等待 2s 后重新观察（不按 B，避免误退/取消匹配）…", Bot.LogLevel.Warn);
        }
        await Task.Delay(2000, ct);
    }
}
