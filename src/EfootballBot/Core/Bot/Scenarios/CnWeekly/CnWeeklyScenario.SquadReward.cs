using EfootballBot.Core.Bot.Match;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

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
    // 本文件小活动：赛后「活动内升级奖励」两页（2026-11-25 国服实机）。
    //   ① 阵容奖励（CwScreen.SquadRewardPick）：六卡评级两两求和，选分最高的一组；同分 前锋>中场>后场。
    //   ② 特殊奖励（CwScreen.LevelRewardPick）：等级 N>40 不选，剩下的选 N 最小；同数 前锋>中场>后场。
    // 两页都是「鼠标点面板标题选中 → 卡面变蓝+勾 → RT 下一步」；手柄方向键在这两页不通（用户实测），
    // 所以选中一律走后台鼠标，方向键只在鼠标通道被拦时当兜底。

    /// <summary>选卡步：0=未选（含读不出重试）1=已点鼠标等卡面变蓝 2=已选中等下一步生效。</summary>
    private int _rewardPickStage;

    /// <summary>当前步的重试计数（0/1 阶段各自用，进下一步即清零）。</summary>
    private int _rewardPickTries;

    /// <summary>本页的选择结论（点哪里、点完验证哪块面板）。</summary>
    private CwRewardChoice? _rewardPickChoice;

    /// <summary>本状态机当前挂在哪一页：阵容奖励 → 特殊奖励 是两页连播，换页必须把状态清零。</summary>
    private CwScreen _rewardPickScreen = CwScreen.None;

    /// <summary>本页累计停留轮数（页级硬上限用：超过 25 轮就走 B 脱身，绝不在同一页无限打转）。</summary>
    private int _rewardPickPageRounds;

    /// <summary>
    /// 赛后「活动内升级奖励」两页的统一处理入口（由 <see cref="HandleCnAsync"/> 在阶段分派之前拦截，
    /// 任何阶段撞上都先接住）。返回 null = 当前不是这两页（状态顺带复位）。
    /// </summary>
    private async Task<int?> RewardPickStepAsync(CwScreen cw, Observation obs, CancellationToken ct)
    {
        if (cw is not (CwScreen.SquadRewardPick or CwScreen.LevelRewardPick))
        {
            ResetRewardPickState();
            return null;
        }
        if (_rewardPickScreen != cw)
        {
            // 两页连播：上一页的选择结论绝不能带到下一页（面板含义完全不同）
            _rewardPickScreen = cw;
            _rewardPickStage = 0;
            _rewardPickTries = 0;
            _rewardPickPageRounds = 0;
            _rewardPickChoice = null;
        }
        if (_streak < 1) return 800; // 页面淡入防抖：此帧文字/卡面还没画全

        bool squad = cw == CwScreen.SquadRewardPick;
        string tag = squad ? "阵容奖励" : "特殊奖励";

        // ---------- 页级硬上限：绝不允许在同一页无限打转 ----------
        // 2026-11-25 实机事故：这页没被识别（判 None → 共享层 LeagueInfoPage），
        // 鼠标在「下一步」上连点三千多次、卡了近两个小时。现在无论哪一环失败，
        // 本页累计 25 轮（约 40 秒）还没离开 → 打 Error 并按 B 尝试脱身，交给外层重新进活动。
        if (++_rewardPickPageRounds > 25)
        {
            _rewardPickPageRounds = 0;
            _log($"[国服周活动] {tag}：本页累计 25 轮仍未离开（选择/推进都没被游戏接受），" +
                 "按 B 尝试脱身，交给外层重进活动（不再原地空点）", LogLevel.Error);
            ResetRewardPickState();
            await _engine.Pad.Back();
            return 2500;
        }

        // ---------- 阶段 0：选哪一块面板并点下去 ----------
        if (_rewardPickStage == 0)
        {
            CwRewardChoice? choice = squad
                ? CnWeeklyBrain.PlanSquadReward(obs.Ocr, obs.Frame)
                : CnWeeklyBrain.PlanLevelReward(obs.Ocr);
            if (choice is null)
            {
                if (++_rewardPickTries <= 2)
                {
                    _log($"[国服周活动] {tag}：{(squad ? "六卡评级" : "三个等级数字")}尚未读全，" +
                         $"再认一拍（第 {_rewardPickTries} 次）", LogLevel.Warn);
                    return 1200;
                }
                choice = CnWeeklyBrain.FallbackChoice();
                _log($"[国服周活动] {tag}：三拍仍未读全，按优先级兜底选前锋（不在此页停留）", LogLevel.Warn);
            }
            else if (!choice.Complete)
            {
                _log($"[国服周活动] {tag}：有字段未读出（{choice.Detail}），" +
                     $"按已读到的结论选 {CnWeeklyBrain.SlotName(choice.Slot)}", LogLevel.Warn);
            }
            _rewardPickChoice = choice;
            _rewardPickStage = 1;
            _rewardPickTries = 0;
            string what = squad ? "评级和" : "等级";
            _log($"[国服周活动] {tag}：{choice.Detail} → 选 {CnWeeklyBrain.SlotName(choice.Slot)}" +
                 $"（{what} {choice.Value}），鼠标点面板标题 ({choice.ClickX:F3},{choice.ClickY:F3})", LogLevel.Success);
            await ClickAtAsync(choice.ClickX, choice.ClickY);
            return 1800;
        }

        var pick = _rewardPickChoice;
        if (pick is null)
        {
            _rewardPickStage = 0;
            return 700;
        }

        // ---------- 阶段 1：等卡面变蓝（选中态） ----------
        if (_rewardPickStage == 1)
        {
            if (CnWeeklyBrain.IsRewardChosen(obs.Frame, pick.PanelX))
            {
                _rewardPickStage = 2;
                _rewardPickTries = 0;
                _log($"[国服周活动] {tag}：{CnWeeklyBrain.SlotName(pick.Slot)} 已选中" +
                     "（卡面变蓝+勾），下一拍 RT 下一步", LogLevel.Success);
                return 900;
            }
            if (++_rewardPickTries <= 2)
            {
                // 标题字/卡面都不一定吃点击：第 1 次点**球员卡本体**（实机卡面 y 0.38~0.63，
                // 面板标题 y≈0.27；旧代码点 ClickY+0.06 = 0.33 正好落在标题与卡片之间的空白带，
                // 连点两次都等于点空 → 用户看到「鼠标点了三千多次没反应」），第 2 次点卡身下部
                double y = _rewardPickTries == 1 ? 0.50 : 0.62;
                _log($"[国服周活动] {tag}：点击后卡面未变蓝，改点球员卡本体重试" +
                     $"（第 {_rewardPickTries} 次，{pick.ClickX:F3},{y:F3}）", LogLevel.Warn);
                await ClickAtAsync(pick.ClickX, y);
                return 1600;
            }
            if (_rewardPickTries == 3)
            {
                int slot = (int)pick.Slot;
                _log($"[国服周活动] {tag}：鼠标点两次未选中，转手柄兜底：左×3 → 右×{slot} → A", LogLevel.Warn);
                await _engine.Pad.DpadRepeat(PadDir.Left, 3);
                if (slot > 0) await _engine.Pad.DpadRepeat(PadDir.Right, slot);
                await EnterAsync(obs, _attempts, "国服进入/确认");
                return 2600;
            }
            // 手柄也没选中：不再纠缠，直接下一步（宁可漏这一个奖励，也绝不能卡死在奖励页）
            _log($"[国服周活动] {tag}：三次都未能选中 {CnWeeklyBrain.SlotName(pick.Slot)}，" +
                 "放弃本页选择直接下一步", LogLevel.Error);
            _rewardPickStage = 2;
            _rewardPickTries = 0;
            return 700;
        }

        // ---------- 阶段 2：发出一次「下一步」（只发一次，防连点落到下一页） ----------
        if (_rewardPickStage == 2)
        {
            _rewardPickStage = 3;
            _rewardPickTries = 0;
            return await AdvanceAfterPickAsync(obs, tag, ct);
        }

        // ---------- 阶段 3：等页面离屏；长时间还在才重来一轮（推进键被吞） ----------
        if (++_rewardPickTries > 6)
        {
            _log($"[国服周活动] {tag}：已选中但「RT 下一步」6 轮未离开本页（推进键被吞），重新识别一次",
                LogLevel.Warn);
            _rewardPickStage = 0;
            _rewardPickTries = 0;
        }
        return 1500;
    }

    /// <summary>奖励页的「下一步」：国服键序 ① RT ② A ③ 后台鼠标点右下按钮（界面上印着「RT 下一步」）。</summary>
    private Task<int> AdvanceAfterPickAsync(Observation obs, string tag, CancellationToken ct)
        => CnAdvance.AdvanceAsync(_engine, _mouse, _streak + 1, $"{tag}下一步", "国服周活动", _log,
            obs.Ocr, obs.Detections, (0.878, 0.936), obs.Frame, ct);

    /// <summary>
    /// 选完奖励后的领取结果弹窗（「新球员加入！」/「奖励已发放至收件箱」）：
    /// 单键「确定」，国服键序 ① RT ② A ③ 起<b>后台鼠标按 6 档落点轮转</b>（用户 2026-11-25 实机：
    /// 这类弹窗两级按键都不动，只有鼠标点得动；而旧版固定「OCR 落点 → 兜底坐标」两档一旦坐标偏出
    /// 按钮就永远关不掉——961 轮 = 56 分钟）。按钮位置两版不同（新球员 y≈0.877、收件箱 y≈0.794），
    /// 且 OCR 会把「确定」与右侧页面字合成一条长行（中心漂到 x=0.716，已在按钮外），
    /// 所以落点由 <see cref="CnAdvance.FindButton"/> 的整词优先规则给出，再配上两版按钮行的兜底。
    /// </summary>
    private async Task<int> RewardResultPopupStepAsync(Observation obs, CancellationToken ct)
    {
        if (_streak < 1) return 800; // 弹窗淡入防抖
        ResetRewardPickState();      // 结果弹窗出现 = 两页选择已结束，选卡状态作废

        int attempt = _streak + 1;
        // 「奖励已发放至收件箱」的「确定」比「新球员加入」高 0.08：按弹窗版本挑按钮行兜底
        bool inbox = obs.Ocr.ContainsJoined("收件箱") || obs.Ocr.ContainsJoined("已发放");
        (double rowX, double rowY) = inbox ? (InboxOkX, InboxOkY) : (NewPlayerOkX, NewPlayerOkY);

        // 现场可诊断：卡超过 24 轮（约 1 分钟）才说话，避免刷屏
        if (attempt % 24 == 0)
            _log($"[国服周活动] 奖励领取弹窗已 {attempt} 轮（约 {attempt * 2.4 / 60:F0} 分钟）仍未关掉：" +
                 "RT/A 与 6 档鼠标落点都在轮转；若下一场仍停在这一页，请手动点一次「确定」并把日志整段回传",
                 LogLevel.Error);

        // ① RT ② A：与其它国服确认页同一条键序（只有 1~2 轮；第 3 轮起全走落点轮转）
        if (attempt <= CnAdvance.ConfirmAtAttempt)
            return await CnAdvance.AdvanceAsync(_engine, _mouse, attempt, "奖励领取弹窗确定",
                "国服周活动", _log, obs.Ocr, obs.Detections, (rowX, rowY), obs.Frame, ct, PopupOkWords);

        switch (CnAdvance.PopupVariantOf(attempt))
        {
            case CnAdvance.PopupVariant.OcrHit:
            {
                var hit = CnAdvance.FindButton(obs.Ocr, obs.Detections, PopupOkWords);
                (double x, double y) = hit ?? (rowX, rowY);
                _log($"[国服周活动] 奖励领取弹窗确定：第{attempt}轮·鼠标" +
                     $"({(hit is null ? "兜底坐标" : "按钮位置")} {x:F3},{y:F3})——需以管理员运行", LogLevel.Warn);
                await CnAdvance.ClickAsync(_engine, _mouse, x, y, "国服周活动", _log, ct);
                return 2400;
            }
            case CnAdvance.PopupVariant.ButtonRow:
                _log($"[国服周活动] 奖励领取弹窗确定：第{attempt}轮·鼠标(弹窗按钮行 {rowX:F3},{rowY:F3})", LogLevel.Warn);
                await CnAdvance.ClickAsync(_engine, _mouse, rowX, rowY, "国服周活动", _log, ct);
                return 2400;
            case CnAdvance.PopupVariant.AltButtonRow:
            {
                (double x, double y) = inbox ? (NewPlayerOkX, NewPlayerOkY) : (InboxOkX, InboxOkY);
                _log($"[国服周活动] 奖励领取弹窗确定：第{attempt}轮·鼠标(另一版按钮行 {x:F3},{y:F3})", LogLevel.Warn);
                await CnAdvance.ClickAsync(_engine, _mouse, x, y, "国服周活动", _log, ct);
                return 2400;
            }
            case CnAdvance.PopupVariant.DoubleClick:
                _log($"[国服周活动] 奖励领取弹窗确定：第{attempt}轮·鼠标双击({rowX:F3},{rowY:F3})", LogLevel.Warn);
                await CnAdvance.ClickAsync(_engine, _mouse, rowX, rowY, "国服周活动", _log, ct);
                await CnAdvance.ClickAsync(_engine, _mouse, rowX, rowY, "国服周活动", _log, ct);
                return 2600;
            case CnAdvance.PopupVariant.RetryRt:
                _log($"[国服周活动] 奖励领取弹窗确定：第{attempt}轮·再按一次 RT（手柄通道重试）", LogLevel.Warn);
                await _engine.Pad.RT();
                return 2400;
            default:
                _log($"[国服周活动] 奖励领取弹窗确定：第{attempt}轮·B 试关弹窗（鼠标各档都没生效）", LogLevel.Warn);
                await _engine.Pad.Back();
                return 2600;
        }
    }

    /// <summary>结果弹窗的确认词（两版弹窗都只印「确定」；确认/OK/確定 是 OCR 误识与旧版兜底）。</summary>
    private static readonly string[] PopupOkWords = { "确定", "確認", "确认", "確定", "OK" };

    /// <summary>「奖励已发放至收件箱」弹窗的「确定」按钮中心（1280×720 内容帧实机标定）。</summary>
    private const double InboxOkX = 0.500;
    private const double InboxOkY = 0.794;

    /// <summary>「新球员加入！」弹窗的「确定」按钮中心（同上，按钮整体低 0.08）。</summary>
    private const double NewPlayerOkX = 0.500;
    private const double NewPlayerOkY = 0.877;

    /// <summary>离开这两页（或切到别的界面）时清掉选择状态：结论是页面级的，不能留到下一次弹窗。</summary>
    private void ResetRewardPickState()
    {
        if (_rewardPickStage == 0 && _rewardPickChoice is null && _rewardPickScreen == CwScreen.None) return;
        _rewardPickStage = 0;
        _rewardPickTries = 0;
        _rewardPickPageRounds = 0;
        _rewardPickChoice = null;
        _rewardPickScreen = CwScreen.None;
    }
}
