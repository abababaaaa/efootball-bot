using EfootballBot.Core.Capture;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 国际服活动全清（活动 → 活动列表 → 逐张清挑战/巡回活动）。
/// 活动列表从最左第一张卡开始逐张扫描，两类活动分别处理：
/// ① 挑战活动：全屏详情 → 挑战清单，选最左未打勾卡开踢；打赢解锁下一张，
///    直到最左卡挂锁 / 全部打勾，B 退回列表（详情页再 B 一次），该活动完成；
/// ② 巡回活动：直接进巡回主页，复用 ScenarioBase 的弹窗清扫 / 主要奖励对勾 / i 详情 x/x 判定链；
///    每个巡回活动第一场前，X 打开活动设置：赛事等级=传奇、自动控制=开、弹性自动控制=关。
/// 已完成活动自动打勾并移到列表最右，所以每次活动完成都重新收拢到最左扫描；
/// 一轮扫描零产出则说明剩余卡均不可进入，整体结束。
/// </summary>
public sealed class IntlWeeklyScenario : ScenarioBase
{
    public IntlWeeklyScenario(BotEngine engine, AppConfig cfg, CancellationToken ct) : base(engine, cfg, ct) { }
    public override string DisplayName => "国际服 · 活动全清";

    private WeeklyConfig W => Cfg.Weekly;

    // ---- 外层活动列表扫描状态（选择容器：只由列表导航写入，比赛结束不清空）----
    private bool _sweepStarted;
    private bool _restartSweep = true;
    private int _cardPos;          // 当前扫描到第几张卡（相对最左）
    private int _rightApplied;     // 已按 Right 的次数
    private int _eventsThisSweep;  // 本轮扫描完成的活动数
    private bool _finished;        // 全清结束
    private bool _inTourEvent;     // 当前在巡回活动内（用于比赛直接落 EventHub 时判定活动结束）
    private bool _tourSettingsDone;

    // ---- 挑战清单内选择状态 ----
    private enum ChalPhase { Collapse, Evaluate }
    private ChalPhase _chal = ChalPhase.Collapse;
    private int _chalRights;
    private readonly HashSet<int> _blacklistedCards = new(); // 已完成挑战卡黑名单（按右移次数记录）
    private readonly HashSet<int> _blacklistedEvents = new(); // 已完成活动黑名单（按活动卡索引记录）

    public override async Task RunAsync()
    {
        int stuck = 0;
        GameScreen? lastScreen = null;

        while (!Ct.IsCancellationRequested && !_finished)
        {
            Ct.ThrowIfCancellationRequested();

            var o = await E.ObserveAsync(Ct);
            o = await PassThroughAsync(o);

            // 匹配中 / 过场：只等待，不计卡死，绝不能按 B 取消匹配
            if (o.Is(GameScreen.Loading))
            {
                stuck = 0;
                lastScreen = null;
                await E.Pad.Wait(2500, Ct);
                continue;
            }

            stuck = o.Screen == lastScreen ? stuck + 1 : 0;
            lastScreen = o.Screen;
            if (!o.Is(GameScreen.Unknown)) ResetSafeUnknown();

            switch (o.Screen)
            {
                case GameScreen.Home:
                    _restartSweep = true;
                    await SafeEnterEventHubAsync();
                    break;

                case GameScreen.MatchHub:
                    _restartSweep = true;
                    await SafeEnterEventHubAsync();
                    break;

                case GameScreen.EventHub:
                    // 比赛结束直接落回列表（没经过主页判定）：视为该巡回活动已结束
                    if (_inTourEvent)
                    {
                        _inTourEvent = false;
                        OnEventCompleted("巡回活动结束（已返回活动列表）");
                        break;
                    }
                    await HandleEventHubAsync();
                    break;

                case GameScreen.EventInfo:
                    // 挑战活动全屏详情：默认焦点在「进入」，确认后进挑战清单
                    Log("挑战活动详情：进入", LogLevel.Info);
                    if (!await TryConfirmOrEnterAsync(o))
                    {
                        await E.Pad.Confirm();
                        await E.Pad.Wait(2200, Ct);
                    }
                    break;

                case GameScreen.ChallengeList:
                    await HandleChallengeListAsync();
                    break;

                case GameScreen.ChallengeHome:
                    // 完成挑战页面：检查是否所有挑战已完成
                    bool hasCompleteText = o.Ocr.FindFirst("完成挑战") is not null;
                    bool hasRewardText = o.Ocr.FindFirst("首次以后的奖励") is not null;
                    bool progressFull = o.Ocr.Words.Any(w =>
                        Regex.IsMatch(w.Text, @"\d+\s*[/|]\s*\d+")
                    );

                    Log($"ChallengeHome OCR: {string.Join(" | ", o.Ocr.Words.Take(15).Select(w => w.Text))}", LogLevel.Debug);
                    Log($"ChallengeHome 检测: hasCompleteText={hasCompleteText}, hasRewardText={hasRewardText}, progressFull={progressFull}", LogLevel.Debug);

                    if (hasCompleteText || hasRewardText)
                    {
                        Log("挑战已完成，退出活动页面", LogLevel.Info);
                        await E.Pad.Back();
                        await E.Pad.Wait(1500, Ct);
                        break;
                    }

                    Log("挑战主页：点击前往比赛", LogLevel.Info);
                    if (!await E.SelectMenuByTextAsync(new[] { "前往比赛", "前往比賽", "Go to Match", "kickoff" }, Ct, 4))
                    {
                        await E.Pad.Confirm();
                    }
                    await E.Pad.Wait(3000, Ct);
                    break;

                case GameScreen.MyLeagueHome:
                    _inTourEvent = true;
                    if (await HandleTourHomeAsync())
                    {
                        _inTourEvent = false;
                        OnEventCompleted("巡回活动主要奖励全部 x/x，活动完成");
                    }
                    break;

                case GameScreen.PreMatch:
                case GameScreen.InMatch:
                    await PlayScenarioMatchAsync();
                    break;

                case GameScreen.Result:
                case GameScreen.Rewards:
                case GameScreen.FullTime:
                case GameScreen.HalfTime:
                    await PostMatchSequenceAsync();
                    break;

                case GameScreen.ActivityDetail:
                    // 兜底：异常停在 i 详情页，B 关闭回主页
                    Log("异常停在 i 详情页，B 关闭", LogLevel.Warn);
                    await E.Pad.Back();
                    await E.Pad.Wait(1800, Ct);
                    break;

                case GameScreen.EventSettings:
                    // 兜底：正常由 BeforeTourMatchAsync 同步处理完，不应停留在此
                    Log("停留在活动设置面板：按 B 完成", LogLevel.Warn);
                    await E.Pad.Back();
                    await E.Pad.Wait(1500, Ct);
                    break;

                case GameScreen.Disconnected:
                    await E.HandlePopupsAsync(o, Ct);
                    await SafeEnterEventHubAsync();
                    break;

                default:
                    // 未识别画面：尝试活动详情页兜底
                    if (await TryEnterEventDetailAsync(o))
                    {
                        ResetSafeUnknown();
                        break;
                    }
                    // 安全恢复：先等待（匹配/过场）→ 最多一次 B → 最后才回主页
                    if (await HandleUnknownAsync(o))
                    {
                        _restartSweep = true;
                        _chal = ChalPhase.Collapse;
                        stuck = 0;
                        lastScreen = null;
                    }
                    break;
            }

            // 同一画面长时间无进展：先本地按一次 B 退回上层，仍不动才回主页重导。
            // 阈值高于合法同名重复：列表 Right 扫描最多 12 轮、挑战清单找卡约 10 轮。
            // EventHub/ChallengeList 的重复是扫描动作本身（有 MaxEvents/8 次上限自限），
            // 看门狗只针对其他画面，避免扫卡途中误按 B
            bool watchdogable = o.Screen is not (GameScreen.EventHub or GameScreen.ChallengeList);
            if (watchdogable && stuck == 16)
            {
                Log("同一画面无进展，本地按 1 次 B 尝试退回", LogLevel.Warn);
                await E.Pad.Back();
                await E.Pad.Wait(1800, Ct);
            }
            else if (watchdogable && stuck > 28)
            {
                Log("本地恢复无效，回主页重新导航到活动列表", LogLevel.Warn);
                await E.BackToHomeAsync(Ct);
                _restartSweep = true;
                _chal = ChalPhase.Collapse;
                stuck = 0;
                lastScreen = null;
            }
        }

        if (_finished)
            Log("活动全清结束：剩余活动均已完成或不可进入。", LogLevel.Success);
    }

    // ================= 外层活动列表扫描 =================

    private async Task HandleEventHubAsync()
    {
        if (!_sweepStarted || _restartSweep)
        {
            await StartSweepAsync();
            return;
        }

        // 先把游标 Right 到目标卡
        if (_rightApplied < _cardPos)
        {
            await E.Pad.Dpad(PadDir.Right);
            await E.Pad.Wait(800, Ct);  // 增加等待时间，确保画面稳定
            _rightApplied++;
            return;
        }

        // 检测当前活动卡是否已完成（绿色✓标记）
        await E.Pad.Wait(500, Ct);  // 额外等待，确保画面完全稳定
        var o = await E.ObserveAsync(Ct);
        bool hasCompleteText = o.Ocr.Contains("完成挑战") || o.Ocr.Contains("首次以后的奖励");
        bool hasGreenCheck = CheckGreenCheckmark(o);
        if (hasCompleteText || hasGreenCheck)
        {
            Log($"第 {_cardPos + 1} 张活动卡已完成，拉黑并顺延", LogLevel.Warn);
            _blacklistedEvents.Add(_cardPos);
            _cardPos++;
            if (_cardPos >= W.MaxEvents)
                FinishSweep();
            return;
        }

        // 进入当前卡
        Log($"进入第 {_cardPos + 1} 张活动卡", LogLevel.Info);
        await E.Pad.Confirm();
        await E.Pad.Wait(2600, Ct);

        if (await AwaitCardEntryAsync())
            return;

        // 弹窗拒绝 / 无法进入：顺延下一张
        Log($"第 {_cardPos + 1} 张活动卡无法进入，顺延", LogLevel.Warn);
        _cardPos++;
        if (_cardPos >= W.MaxEvents)
            FinishSweep();
    }

    private async Task StartSweepAsync()
    {
        await CollapseToAiEventsAsync();
        _cardPos = 0;
        _rightApplied = 0;
        _eventsThisSweep = 0;
        _tourSettingsDone = false;
        _sweepStarted = true;
        _restartSweep = false;
        Log("新一轮扫描：从「对阵AI」最左活动卡开始", LogLevel.Info);
    }

    /// <summary>
    /// 按 A 后等待落点：挑战清单 / 巡回主页 / 赛前=成功；详情页则按「进入」继续；
    /// 连续两次仍在列表（含弹窗已确认）= 该卡不可进入。
    /// </summary>
    private async Task<bool> AwaitCardEntryAsync()
    {
        bool sawHub = false;
        for (int i = 0; i < 8; i++)
        {
            Ct.ThrowIfCancellationRequested();
            var o = await E.ObserveAsync(Ct);
            o = await PassThroughAsync(o); // 中途确认弹窗（确定）

            switch (o.Screen)
            {
                case GameScreen.ChallengeList:
                    _chal = ChalPhase.Collapse;
                    return true;
                case GameScreen.MyLeagueHome:
                    _inTourEvent = true;
                    _tourSettingsDone = false;
                    return true;
                case GameScreen.PreMatch:
                case GameScreen.Loading:
                case GameScreen.InMatch:
                    return true;
                case GameScreen.EventInfo:
                    Log("挑战活动详情：按「进入」", LogLevel.Info);
                    if (!await TryConfirmOrEnterAsync(o))
                        await E.Pad.Confirm();
                    await E.Pad.Wait(2500, Ct);
                    sawHub = false;
                    break;
                case GameScreen.EventHub:
                    if (sawHub) return false;
                    sawHub = true;
                    await E.Pad.Wait(1200, Ct);
                    break;
                default:
                    await E.Pad.Wait(1000, Ct);
                    break;
            }
        }
        return false;
    }

    private void OnEventCompleted(string reason)
    {
        _eventsThisSweep++;
        E.Stats.EventsDone++;
        Log($"{reason}（已清活动 {E.Stats.EventsDone}）", LogLevel.Success);
        // 完成的活动会打勾移到最右：重新收拢从最左扫
        _restartSweep = true;
    }

    private void FinishSweep()
    {
        if (_eventsThisSweep == 0)
        {
            _finished = true;
            return;
        }
        Log($"本轮扫描完成 {_eventsThisSweep} 个活动，从最左复查剩余活动", LogLevel.Info);
        _restartSweep = true;
    }

    // ================= 挑战清单 =================

    private async Task HandleChallengeListAsync()
    {
        if (_chal == ChalPhase.Collapse)
        {
            for (int i = 0; i < 5; i++)
            {
                await E.Pad.Dpad(PadDir.Left);
                await E.Pad.Wait(130, Ct);
            }
            await E.Pad.Wait(450, Ct);
            _chal = ChalPhase.Evaluate;
            _chalRights = 0;
            _blacklistedCards.Clear(); // 重新扫描时清空黑名单
            return;
        }

        // Evaluate：判定焦点（最左）卡状态
        var o = await E.ObserveAsync(Ct);
        var info = ReadChallengeCard(o);
        Log($"挑战清单：卡号={(info.Number?.ToString() ?? "?")} 定位={info.Located} 锁={info.LockRatio:F3} ✓绿={info.GreenRatio:F3}", LogLevel.Info);

        // 定位失败：保守右移一次重试，连续超限按完成处理（防漏定位死循环）
        if (!info.Located)
        {
            Log("焦点卡定位失败，右移重试", LogLevel.Warn);
            _chalRights++;
            if (_chalRights > 12)
            {
                Log("定位持续失败：挑战活动按完成处理", LogLevel.Warn);
                await ExitChallengeAsync();
                return;
            }
            await E.Pad.Dpad(PadDir.Right);
            await E.Pad.Wait(500, Ct);
            return;
        }

        // 焦点卡挂锁：已无卡可打（全部完成），拉黑当前活动并退出
        if (!info.Unlocked)
        {
            Log("最左卡挂锁：挑战活动全部完成", LogLevel.Success);
            _blacklistedEvents.Add(_cardPos);
            await ExitChallengeAsync();
            return;
        }

        // 焦点卡已打勾：右移找下一张（连移保护，避免卡环回绕死循环）
        if (info.Done)
        {
            _chalRights++;
            if (_chalRights > 8)
            {
                Log("所有挑战卡均已打勾：挑战活动完成", LogLevel.Success);
                await ExitChallengeAsync();
                return;
            }
            Log("该挑战卡已打勾，右移下一张", LogLevel.Info);
            await E.Pad.Dpad(PadDir.Right);
            await E.Pad.Wait(500, Ct);
            return;
        }

        // 可打卡：进入
        Log("选择可打挑战卡，进入比赛", LogLevel.Info);
        await E.Pad.Confirm();
        await E.Pad.Wait(2600, Ct);

        // 检测是否弹出「参加新的挑战？」对话框
        var confirm = await E.ObserveAsync(Ct);
        if (confirm.Ocr.Contains("参加新的挑战") || confirm.Ocr.Contains("覆盖你目前的挑战进度"))
        {
            Log("该挑战已完成，弹窗提示覆盖进度：取消并加入黑名单", LogLevel.Warn);
            await E.Pad.Back(); // 取消
            await E.Pad.Wait(1500, Ct);
            _blacklistedCards.Add(_chalRights);
            _chalRights++;
            if (_chalRights > 8)
            {
                Log("所有挑战卡均已尝试：挑战活动完成", LogLevel.Success);
                await ExitChallengeAsync();
                return;
            }
            await E.Pad.Dpad(PadDir.Right);
            await E.Pad.Wait(500, Ct);
            return;
        }

        _chal = ChalPhase.Collapse;
    }

    /// <summary>挑战清单 B →（活动详情 B）→ 活动列表。</summary>
    private async Task ExitChallengeAsync()
    {
        for (int i = 0; i < 4; i++)
        {
            Ct.ThrowIfCancellationRequested();
            await E.Pad.Back();
            await E.Pad.Wait(2000, Ct);
            var o = await E.ObserveAsync(Ct);
            o = await PassThroughAsync(o);
            if (o.Is(GameScreen.EventHub))
            {
                OnEventCompleted("挑战活动已全部通关");
                return;
            }
        }
        Log("未能正常退回活动列表，标记重导", LogLevel.Warn);
        _restartSweep = true;
    }

    // ================= 巡回活动设置（X 面板） =================

    /// <summary>每个巡回活动首场「前往比赛」前：X 活动设置，传奇 / 自动控制开 / 弹性关。</summary>
    protected override async Task BeforeTourMatchAsync()
    {
        if (_tourSettingsDone) return;
        try
        {
            await ConfigureTourSettingsAsync();
        }
        catch (Exception ex)
        {
            Log($"活动设置异常（保持当前设置继续）：{ex.Message}", LogLevel.Warn);
            await E.Pad.Back();
            await E.Pad.Wait(1200, Ct);
        }
        _tourSettingsDone = true;
    }

    private async Task ConfigureTourSettingsAsync()
    {
        string target = W.Difficulty;
        Log("打开 X 活动设置：赛事等级=" + target + " / 自动控制=开 / 弹性自动控制=关", LogLevel.Info);
        await E.Pad.X();
        await E.Pad.Wait(1300, Ct);

        var o = await E.ObserveAsync(Ct);
        if (!o.Is(GameScreen.EventSettings) && !o.Ocr.Contains("弹性自动控制"))
        {
            Log("活动设置面板未打开，保持默认设置", LogLevel.Warn);
            return;
        }

        // 收拢到首行（赛事等级）
        for (int i = 0; i < 4; i++)
        {
            await E.Pad.Dpad(PadDir.Up);
            await E.Pad.Wait(120, Ct);
        }
        await E.Pad.Wait(300, Ct);

        // ① 赛事等级：下拉框当前值不含目标 → A 展开，垂直列表选目标
        o = await E.ObserveAsync(Ct);
        if (!RowContains(o, 0.52, 0.96, 0.195, 0.275, target))
        {
            Log($"赛事等级不是「{target}」，展开下拉选择", LogLevel.Info);
            await E.Pad.Confirm();
            await E.Pad.Wait(1300, Ct);
            if (!await SelectDifficultyAsync(target))
            {
                Log($"下拉列表中未定位到「{target}」，B 取消保持原值", LogLevel.Warn);
                await E.Pad.Back();
                await E.Pad.Wait(1000, Ct);
            }
            await E.Pad.Wait(1500, Ct);
        }

        // ② Down×2 跳过「选择的球队」到「自动控制」
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(250, Ct);
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(450, Ct);
        await EnsureToggleAsync(true, 0.435, 0.515);

        // ③ Down 到「弹性自动控制」，要关
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(450, Ct);
        await EnsureToggleAsync(false, 0.550, 0.630);

        // ④ B 完成返回主页
        await E.Pad.Back();
        await E.Pad.Wait(1500, Ct);
        o = await E.ObserveAsync(Ct);
        if (o.Is(GameScreen.EventSettings) || o.Ocr.Contains("弹性自动控制"))
        {
            await E.Pad.Back();
            await E.Pad.Wait(1500, Ct);
        }
        Log("活动设置完成", LogLevel.Info);
    }

    /// <summary>
    /// 难度下拉专用选择：只认蓝色焦点高亮行（A 确认的是焦点行）。
    /// 黄色标记是「当前保存值」，不是焦点，绝不能按它判断（误选巨星的根因）。
    /// </summary>
    private async Task<bool> SelectDifficultyAsync(string target)
    {
        for (int i = 0; i < 8; i++)
        {
            Ct.ThrowIfCancellationRequested();
            var o = await E.ObserveAsync(Ct);
            var w = o.Ocr.Words.FirstOrDefault(x =>
                OcrText.Norm(x.Text).Equals(target, StringComparison.OrdinalIgnoreCase))
                ?? o.Ocr.MergedWords.FirstOrDefault(x =>
                OcrText.Norm(x.Text).Contains(target, StringComparison.OrdinalIgnoreCase));
            if (w is null)
            {
                // 目标还没滚到可视区，继续 Down
                await E.Pad.Dpad(PadDir.Down);
                await E.Pad.Wait(350, Ct);
                continue;
            }
            // 焦点行 = 蓝色高亮。测目标词所在行的蓝底占比。
            var row = new NormRect(
                Math.Max(0, w.X - 0.06), Math.Max(0, w.Y - w.H * 0.5),
                Math.Min(1, w.W + 0.30), w.H * 2.2);
            double blue = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Blue, row);
            if (blue > 0.20)
            {
                Log($"难度「{target}」焦点已就位（blue={blue:F2}），按 A", LogLevel.Info);
                await E.Pad.Confirm();
                return true;
            }
            Log($"难度「{target}」已见但非焦点行（blue={blue:F2}），继续 Down", LogLevel.Debug);
            await E.Pad.Dpad(PadDir.Down);
            await E.Pad.Wait(350, Ct);
        }
        return false;
    }

    /// <summary>确保开关行值为 开/关；A 切换后复测，最多 3 次，跳页则 B 退回。</summary>
    private async Task EnsureToggleAsync(bool wantOn, double yMin, double yMax)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Ct.ThrowIfCancellationRequested();
            var o = await E.ObserveAsync(Ct);
            if (!o.Is(GameScreen.EventSettings) && !o.Ocr.Contains("弹性自动控制"))
            {
                Log("设置面板意外关闭，B 尝试返回", LogLevel.Warn);
                await E.Pad.Back();
                await E.Pad.Wait(1200, Ct);
                return;
            }
            string? v = ReadToggle(o, yMin, yMax);
            bool ok = (wantOn && v == "开") || (!wantOn && v == "关");
            if (ok)
            {
                Log($"开关已为{(wantOn ? "开" : "关")}", LogLevel.Debug);
                return;
            }
            Log($"开关当前={(v ?? "未识别")}，按 A 切换为{(wantOn ? "开" : "关")}", LogLevel.Info);
            await E.Pad.Confirm();
            await E.Pad.Wait(900, Ct);
        }
        Log("开关状态确认失败，保持当前值继续", LogLevel.Warn);
    }

    /// <summary>读设置面板右侧某一行的开关值。</summary>
    private static string? ReadToggle(Observation o, double yMin, double yMax)
    {
        foreach (var w in o.Ocr.Words)
        {
            string t = OcrText.Norm(w.Text);
            if (w.CenterX < 0.52 || w.CenterX > 0.78) continue;
            if (w.CenterY < yMin || w.CenterY > yMax) continue;
            if (t == "开") return "开";
            if (t == "关") return "关";
        }
        foreach (var w in o.Ocr.MergedWords)
        {
            string t = OcrText.Norm(w.Text);
            if (w.CenterX < 0.50 || w.CenterX > 0.80) continue;
            if (w.CenterY < yMin - 0.02 || w.CenterY > yMax + 0.02) continue;
            if (t.Contains("关")) return "关";
            if (t.Contains("开")) return "开";
        }
        return null;
    }

    /// <summary>设置面板指定行内是否包含目标文字。</summary>
    private static bool RowContains(Observation o, double xMin, double xMax, double yMin, double yMax, string text)
    {
        foreach (var w in o.Ocr.Words)
        {
            if (w.CenterX < xMin || w.CenterX > xMax) continue;
            if (w.CenterY < yMin || w.CenterY > yMax) continue;
            if (OcrText.Norm(w.Text).Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return o.Ocr.MergedWords.Any(w =>
            w.CenterX >= xMin && w.CenterX <= xMax && w.CenterY >= yMin && w.CenterY <= yMax
            && OcrText.Norm(w.Text).Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // ================= 比赛 =================

    private async Task PlayScenarioMatchAsync()
    {
        var run = await PlayOneMatchAsync(keyMatch: false, forfeit: false);
        if (!run.Completed)
        {
            Log("本场未确认结束（可能仍在比赛中），不计场次，回到外层继续观察", LogLevel.Warn);
            E.ResetUnknown();
            return;
        }
        E.Stats.MatchesDone++;
        E.ResetUnknown();
        Log($"活动第 {E.Stats.MatchesDone} 场比赛完成", LogLevel.Success);
    }

    // ================= 活动详情页兜底 =================

    /// <summary>活动详情页：尝试点击参加 / 开始按钮进入赛前（未识别画面时的兜底）。</summary>
    private async Task<bool> TryEnterEventDetailAsync(Observation o)
    {
        string[] words = { "进入", "参加", "参与", "开始比赛", "进入比赛", "对战电脑", "对战ai", "play", "enter", "kickoff", "开始" };
        foreach (var w in words)
        {
            var hit = o.Ocr.FindFirst(w);
            if (hit is null) continue;
            Log($"活动详情：点击「{w}」", LogLevel.Debug);
            await E.Pad.Confirm();
            await E.Pad.Wait(2500, Ct);
            var after = await E.ObserveAsync(Ct);
            if (after.Is(GameScreen.PreMatch) || after.Is(GameScreen.Loading)) return true;
            if (await E.SelectMenuByTextAsync(new[] { w }, Ct, 5))
            {
                await E.Pad.Wait(2500, Ct);
                var a2 = await E.ObserveAsync(Ct);
                if (a2.Is(GameScreen.PreMatch) || a2.Is(GameScreen.Loading) || a2.Is(GameScreen.InMatch)) return true;
            }
        }
        return false;
    }

    // ================= 活动卡完成检测 =================

    /// <summary>
    /// 检测活动选择主页当前卡是否已完成（右上角绿色✓图标）。
    /// 绿色✓位于当前选中卡的右上角，位置随选中卡变化。
    /// 卡1: x≈0.38, 卡2: x≈0.68（根据截图）。
    /// 注意：✓图标实测为白色/浅绿色小图标，需检测高亮度像素；
    /// 阈值放宽并同时检测白/绿双通道，防止静态图与实机帧颜色偏差。
    /// </summary>
    private bool CheckGreenCheckmark(Observation o)
    {
        if (o.Frame is null) return false;

        double checkX = 0.38 + _cardPos * 0.30;
        var checkRoi = new NormRect(checkX, 0.07, 0.04, 0.04);

        double white = WhiteRatio(o.Frame, checkRoi);
        double green = FrameAnalyzer.ColorRatioExact(o.Frame, HsvFilter.Green, checkRoi);
        bool done = white >= 0.03 || green >= 0.03;

        Log($"活动卡{_cardPos + 1}完成检测：白={white:F3} 绿={green:F3} → {(done ? "已完成" : "未完成")}", LogLevel.Info);
        SaveDebugFrame(o.Frame, $"event_card{_cardPos + 1}");

        return done;
    }

    /// <summary>保存当前帧到 debug_frames 目录（用于线下校准 ROI 与颜色阈值）。</summary>
    private void SaveDebugFrame(FrameData f, string name)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "debug_frames");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{name}_{DateTime.Now:HHmmss}.png");
            var wb = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
            using var fs = File.OpenWrite(path);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wb));
            enc.Save(fs);
            Log($"调试帧已保存：{path}", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            Log($"保存调试帧失败：{ex.Message}", LogLevel.Debug);
        }
    }

    /// <summary>检测白色像素（R=G=B 且高亮度，用于✓图标识别）。</summary>
    private static double WhiteRatio(FrameData f, NormRect region)
    {
        var (x, y, w, h) = FrameAnalyzer.PixelRect(f, region);
        long hit = 0, n = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy++)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx++)
            {
                int i = row + xx * 4;
                byte bb = b[i], gg = b[i + 1], rr = b[i + 2];
                if (rr > 200 && gg > 200 && bb > 200 &&
                    Math.Abs(rr - gg) < 10 && Math.Abs(gg - bb) < 10) hit++;
                n++;
            }
        }
        return n == 0 ? 0 : hit / (double)n;
    }
}
