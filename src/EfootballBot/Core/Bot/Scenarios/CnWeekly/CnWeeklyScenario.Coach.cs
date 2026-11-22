using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· CnWeeklyScenario 状态机
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `CnWeeklyScenario.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

public sealed partial class CnWeeklyScenario : IScenario
{
    // 本文件小活动：晋级挑战·教练签下链（选教练 → 属性 → 签下确认，含后台鼠标点击）。

    // ---------------- 教练签下链（2026-11-04 实机 晋级挑战） ----------------
    /// <summary>
    /// 教练选择页（2026-11-04 实机：卡上评价 80 / 81，选数字更大的那位）。
    /// ★2026-11-05 实机：这一页只认**鼠标点击**——手柄 Dpad 左右 + A 完全没反应
    /// （日志里连续三拍「左归位→右×1→A」都停在原页）。改为鼠标点评价最大的那张卡：
    /// 点评价字下方一点（评价字在卡头，下方仍是同一张卡的卡面）；
    /// 第一次点 y+0.09，重试再往下到 +0.18，都不通就转手柄兜底（防鼠标通道被拦）。
    /// </summary>
    private async Task<int> HandleCoachSelectAsync(Observation obs)
    {
        if (_streak < 1) return 800; // 卡片淡入/评价数字渲染防抖
        if (++_attempts > 5)
        {
            _log("[国服周活动] 教练选择页多次点击未进入教练资料，B 返回重走流程", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }
        var cards = CnWeeklyBrain.ScanCoachCards(obs.Ocr);
        var best = CnWeeklyBrain.FindBestCoachCardWord(obs.Ocr);
        if (best is not { } h)
        {
            _log("[国服周活动] 教练选择：未读出评价数字，鼠标点屏幕中部兜底", LogLevel.Warn);
            await ClickAtAsync(0.5, 0.35);
            return 2800;
        }
        // 第 3 拍起改用键盘/手柄兜底（鼠标可能被游戏吞掉）
        if (_attempts >= 3)
        {
            int slot = Math.Max(0, cards.FindIndex(c => c.Rating == h.Rating));
            _log($"[国服周活动] 教练选择：鼠标点两次未生效，转手柄：左归位 → 右×{slot} → A", LogLevel.Warn);
            await _engine.Pad.DpadRepeat(PadDir.Left, 3);
            if (slot > 0) await _engine.Pad.DpadRepeat(PadDir.Right, slot);
            await _engine.Pad.Confirm();
            return 3000;
        }
        double dy = _attempts <= 1 ? 0.09 : 0.18;
        string all = string.Join(" / ", cards.Select(c => $"{c.Rating}"));
        _log($"[国服周活动] 教练选择：读到 {cards.Count} 位教练评价（{all}），选最大 {h.Rating}，" +
             $"鼠标点击评价字下方（{h.CenterX:F3},{Math.Min(0.95, h.CenterY + dy):F3}）", LogLevel.Info);
        await ClickAtAsync(h.CenterX, h.CenterY + dy);
        return 3000;
    }

    /// <summary>后台鼠标点击（国服须本程序提权后 PostMessage 才被游戏处理，与天梯点教练卡同款）。</summary>
    private async Task ClickAtAsync(double nx, double ny)
    {
        var win = _engine.Window;
        if (win is null)
        {
            _log("[国服周活动] 游戏窗口句柄缺失，鼠标点击无法执行", LogLevel.Error);
            return;
        }
        var (w, h) = EfootballBot.Core.System.GameWindow.GetClientSize(win.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        await _engine.Pad.HumanWait(150, 320, CancellationToken.None);  // 拟人预停顿
        await _mouse.ClickNormalizedAsync(win.Handle, nx, ny, w, h, downHoldMs: 70);
    }

    /// <summary>教练资料页（用户 2026-11-04 实机）：A 下一步 → 弹签下确认。底栏写
    /// 「RT 确认选择」，A 两次未生效换 RT（对阵表/难度页同款经验）。</summary>
    private async Task<int> HandleCoachDetailAsync()
    {
        if (_streak < 1) return 800; // 资料页淡入防抖
        if (++_attempts > 4)
        {
            _log("[国服周活动] 教练资料页确认多次未生效，B 返回重走流程", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }
        if (_attempts >= 3)
        {
            _log("[国服周活动] 教练资料页：A 两次未生效，改按 RT 确认选择", LogLevel.Info);
            await _engine.Pad.RT();
        }
        else
        {
            _log("[国服周活动] 教练资料页：A 确认选择（下一步）", LogLevel.Info);
            await _engine.Pad.Confirm();
        }
        return 3200;
    }

    /// <summary>
    /// 签下确认弹窗（用户 2026-11-04 实机：「签下确认」+ 确认/取消，蓝色高亮的「确认」是
    /// 默认焦点）：直接 A。A 两次弹窗仍在 = 焦点掉到取消或其他位置，上移一次纠正后再 A。
    /// </summary>
    private async Task<int> HandleSignConfirmAsync()
    {
        if (_streak < 1) return 900; // 弹窗淡入防抖
        _coachSigns++;
        if (_coachSigns > 3)
        {
            _log("[国服周活动] 签下教练后仍反复回到签下确认页，停止挂机（请手动检查活动状态）", LogLevel.Error);
            _engine.StopWithError("教练签下反复出现");
            return 0;
        }
        if (++_attempts > 4)
        {
            _log("[国服周活动] 签下确认弹窗多次未关闭，B 关闭后重走流程", LogLevel.Warn);
            SetPhase(CwPhase.Flow);
            await _engine.Pad.Back();
            return 2400;
        }
        if (_attempts >= 2)
        {
            _log("[国服周活动] 签下确认：A 未生效，上移纠正焦点 → A 确认", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
        }
        else
        {
            _log("[国服周活动] 签下确认：A 确认（默认焦点在确认）", LogLevel.Info);
        }
        await _engine.Pad.Confirm();
        if (!_coachSigned)
        {
            _coachSigned = true;
            _log("[国服周活动] 教练签下完成，继续开赛流程", LogLevel.Success);
        }
        return 3200;
    }
}
