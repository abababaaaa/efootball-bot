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
    // 本文件小活动：巡回赛主要奖励核验 —— 主页勾 → i 信息弹窗 x/x，以及撤退。

    // ---------------- 巡回赛主要奖励核验（主页勾 → i 信息弹窗 x/x） ----------------
    /// <summary>
    /// 巡回赛主页主要奖励核验步（用户 2026-10-17 流程，仅巡回赛型）：主页右栏勾 = 已有
    /// 主要奖励被领过，不代表拿满（主页 高光定向券×1 带勾时 i 弹窗里仍是 1/2）。右移进 i →
    /// A 打开信息弹窗 → RewardPopup 读 x/x：全满 → 关闭后从主页退上上页回活动列表换
    /// 下一个活动；没满 → 关闭+左移回前往比赛继续刷。弹窗打不开超 2 次：放弃核验按
    /// 未拿满继续刷（保守不提前退场）。★挑战活动短路不核验——i 弹窗是本周全部活动卡
    /// 的合计进度（实机 挑战赛弹窗 金币 30/90、高光定向券 0/2），对单个活动卡永远读不满；
    /// 挑战完成判定只走挑战清单扫描。返回 null = 本轮不在核验流程，调用方继续常规逻辑。
    /// </summary>
    private async Task<int?> TourRewardCheckStepAsync(Observation obs)
    {
        if (_rewardCheckStage == 3)
        {
            // 弹窗已确认全满：正常是从主页 B 退上上页换下一个活动（主页→介绍页→活动列表，见 Retreating）。
            // 但先认一下这张卡是不是「刚刚刷满的那一张」——活动列表右移到卡行末尾后多余右移只饱和，
            // 退回列表再右移会重新进同一张卡。不认这一点，全满→退列表→槽位+1→全满 会无限打转
            //（实机 2026-11-20：槽位 1→306，空转 7 小时，接力永不触发）。
            _rewardCheckStage = 0;
            string sig = CnWeeklyBrain.CardSignature(obs.Ocr);
            if (sig.Length > 0 && sig == _lastFullCardSig)
            {
                // 同一张卡又全满：右移已饱和，再退列表也只会回到这里。把槽位顶过卡位上限，
                // 照常退到活动列表（主页→介绍页→列表这条已验证的路径），再由 EnterEventAsync
                // 的卡位闸收尾 → 全部完成 → 接力；不去赌「在活动主页直接交班」时目标模块能否认页。
                _slotIndex = MaxEventSlots + 1;
                SetPhase(CwPhase.Retreating);
                _log($"[国服周活动] 又回到刚刷满的同一张活动卡（{sig}）：活动列表已无未刷满的新活动卡，收尾", LogLevel.Success);
                await _engine.Pad.Back();
                return 2200;
            }
            if (sig.Length > 0) _lastFullCardSig = sig;
            SetPhase(CwPhase.Retreating);
            _log("[国服周活动] 主要奖励已拿满（i 弹窗 x/x 全满），退回活动列表换下一个活动", LogLevel.Success);
            await _engine.Pad.Back();
            return 2200;
        }
        // ★挑战活动绝不进 i 查看奖励：i 弹窗的 主要奖励 x/x 是本周全部活动卡的合计进度
        //（实机 2026-10-17 挑战赛弹窗 金币 30/90、高光定向券 0/2），对单个活动卡永远读不满，
        // 拿它判满会无限空转。挑战活动的完成判定只走挑战清单扫描（全勾/全锁→重进→全部完成停止）
        if (_challengeMode || _rewardContinueUntilMatch || _rewardPopupGaveUp ||
            !CnWeeklyBrain.MainRewardHasCheck(obs.Frame))
            return null;
        if (_rewardCheckStage == 0)
        {
            _rewardCheckStage = 1;
            _attempts = 0;
            _log("[国服周活动] 主页主要奖励带勾：右移进 i → A 打开信息弹窗核对 x/x", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Right); // 前往比赛 → i
            return 1500;
        }
        if (_rewardCheckStage == 1)
        {
            _rewardCheckStage = 2;
            _log("[国服周活动] A 打开信息弹窗", LogLevel.Info);
            await _engine.Pad.Confirm();
            return 2200;
        }
        // stage 2：A 已按过，弹窗仍未出现（光标不在 i 等），重试
        if (++_attempts > 2)
        {
            _rewardPopupGaveUp = true;
            _rewardCheckStage = 0;
            _attempts = 0;
            _log("[国服周活动] 信息弹窗多次未打开，放弃 x/x 核验，按未拿满继续刷", LogLevel.Warn);
            await _engine.Pad.Dpad(PadDir.Left); // i → 前往比赛，恢复常规流程
            return 1200;
        }
        _log("[国服周活动] 信息弹窗未出现，重按 A 打开", LogLevel.Info);
        await _engine.Pad.Confirm();
        return 2200;
    }

    /// <summary>i 信息弹窗（主要奖励 x/x 进度）：全满 → B 关闭，stage=3 由主页核验步退场；
    /// 没满/读不出 → B 关闭 + 左移回前往比赛继续刷（读不出按未拿满，保守不提前退场）。
    /// 进度词只取 主要奖励 头与 活动时间 头之间（活动日期 2026/10/16 三段式不吃）。</summary>
    private async Task<int> HandleRewardPopupAsync(Observation obs)
    {
        if (_streak < 1) return 800; // 弹窗淡入防抖
        var (readable, allDone) = CnWeeklyBrain.ParseMainRewardProgress(obs.Ocr);
        if (!readable && ++_attempts <= 2) return 1200; // 进度词未读全，再认一拍
        _rewardCheckStage = 0;
        _attempts = 0;
        await _engine.Pad.Back(); // B 关闭弹窗（光标回 i）
        if (allDone)
        {
            _rewardCheckStage = 3;
            _log("[国服周活动] 主要奖励全部拿满（x/x 全等），关闭弹窗后退回活动列表", LogLevel.Success);
            return 2400;
        }
        // 未拿满：本场打完前不再重复核验（勾会一直挂在主页，否则原地循环）
        _rewardContinueUntilMatch = true;
        _log($"[国服周活动] 主要奖励未拿满（{(readable ? "进度未全满" : "进度未读出")}），左移回前往比赛继续刷；本场结束后再复核", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Left); // i → 前往比赛
        return 2400;
    }

    /// <summary>主要奖励已拿满后的退场：B 清介绍页/设置面板/弹窗回活动列表（上上页），
    /// 槽位 +1 换下一个活动；途中确认弹窗（奖励领取类）按 A 清掉。</summary>
    private async Task<int?> HandleRetreatingAsync(CwScreen cw, MatchScreen ml, Observation obs)
    {
        if (cw == CwScreen.EventsList)
        {
            _slotIndex++;
            // 换下一个活动：挑战/奖励核验/设置核验/阵容子状态全部复位——都是活动级状态，
            // 带进下一个活动会乱套（周顺序 [挑战赛,巡回赛] 时残留挑战态会在巡回赛主页乱按 B；
            // 上个活动放弃过的弹窗核验会压制下个活动的核验）
            ResetChallengeState();
            ResetRewardCheckState();
            _tourReady = false;
            _tourSettingsTries = 0;
            _tourPanelReopens = 0;
            _squadConfigured = false;
            _menuApplied = false;
            _squadTries = 0;
            _coachSigned = false; // 活动级状态：下一个活动可能是要重新签教练的另一张卡
            SetPhase(CwPhase.Flow);
            _log($"[国服周活动] 已退回活动列表，改刷槽位 {_slotIndex}", LogLevel.Success);
            return 800;
        }
        if (cw is CwScreen.EventHomeTour or CwScreen.ChallengeCardHome or CwScreen.EventHome or
            CwScreen.EventIntro or CwScreen.EventSettings or CwScreen.RewardPopup or
            CwScreen.CoachSelect or CwScreen.CoachDetail or CwScreen.SignConfirm)
        {
            if (++_attempts > 5) { SetPhase(CwPhase.Flow); return 700; }
            await _engine.Pad.Back();
            return 2200;
        }
        if (ml == MatchScreen.HasConfirm)
        {
            if (++_attempts > 6) { SetPhase(CwPhase.Flow); return 700; }
            // ★统一弹窗规则：有 取消/关闭 先上移到推进键再按 A
            bool dismissable = obs.Ocr.ContainsJoined("取消") || obs.Ocr.ContainsJoined("关闭");
            _log("[国服周活动] 退场途中弹窗：A 确认（可能是奖励领取）", LogLevel.Info);
            if (dismissable) await _engine.Pad.Dpad(PadDir.Up);
            await _engine.Pad.Confirm();
            return 2200;
        }
        if (++_attempts > 8) SetPhase(CwPhase.Flow);
        return 1500;
    }

    /// <summary>主要奖励核验状态复位（换活动时调用：x/x 结论与「弹窗放弃」标记都是活动级状态）。</summary>
    private void ResetRewardCheckState()
    {
        _rewardCheckStage = 0;
        _rewardPopupGaveUp = false;
        _rewardContinueUntilMatch = false;
    }
}
