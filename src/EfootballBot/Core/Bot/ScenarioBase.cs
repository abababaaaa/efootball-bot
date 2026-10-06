using EfootballBot.Core.Action;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Flow;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Input;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>一场比赛的上下文与结果。</summary>
public sealed class MatchRun
{
    public bool IsKeyMatch;
    public bool Forfeited;
    public bool Completed;
}

public abstract class ScenarioBase
{
    protected readonly BotEngine E;
    protected readonly AppConfig Cfg;
    protected readonly CancellationToken Ct;
    /// <summary>单场比赛状态机（PlayOneMatchAsync 委托给它）。</summary>
    public readonly MatchFlow MatchFlow;
    public abstract string DisplayName { get; }

    protected ScenarioBase(BotEngine engine, AppConfig cfg, CancellationToken ct)
    {
        E = engine; Cfg = cfg; Ct = ct;
        MatchFlow = new MatchFlow(engine, engine.Actions, engine.Pad, TrySpecialInMatchActionAsync);
    }

    public abstract Task RunAsync();

    protected void Log(string msg, LogLevel lv = LogLevel.Info) => E.LogInternal(msg, lv);

    /// <summary>
    /// 巡回主页「前往比赛」前的钩子：国际服周活动用于打开 X 活动设置
    /// （赛事等级不符时：自动控制开 / 弹性自动控制关）；国服无此步骤。
    /// </summary>
    protected virtual Task BeforeTourMatchAsync() => Task.CompletedTask;

    /// <summary>
    /// 比赛循环每轮观察到 InMatch 时的场景特殊动作钩子（国服用于右上角出现
    /// 「跳过比赛」可用提示时：暂停 → 跳过比赛快速结束）。默认无动作。
    /// 返回 true 表示已执行动作，比赛循环本轮直接重新观察。
    /// </summary>
    protected virtual Task<bool> TrySpecialInMatchActionAsync(Observation o) => Task.FromResult(false);

    // 中途未识别画面的安全恢复计数：已知画面出现时清零
    private int _safeUnknown;

    /// <summary>已知画面正常处理时调用，清除未识别计数。</summary>
    protected void ResetSafeUnknown() => _safeUnknown = 0;

    /// <summary>
    /// 中途画面未识别时的安全处理（绝不立刻按 B——匹配中/赛前按 B 会取消流程）：
    /// ① 先找「确定/确认/下一步」弹窗按 A；② 连续 4 次只等待（匹配/过场可能持续几十秒）；
    /// ③ 仍不动按 1 次 B 尝试退回上层；④ 再不动才回主页重新导航。
    /// 返回 true 表示执行了回主页大重置（调用方需重新导航到活动列表）。
    /// </summary>
    protected async Task<bool> HandleUnknownAsync(Observation o)
    {
        // 物品已过期弹窗 / 明确的确认弹窗（下一步 / OK / 确定 / 确认）：立刻确认，不按 B
        if (o.Is(GameScreen.Dialog)
            || o.Ocr.Contains("下一步") || o.Ocr.Contains("ok")
            || o.Ocr.Contains("确定") || o.Ocr.Contains("确认")
            || o.Ocr.Contains("过期") || o.Ocr.ContainsJoined("过期"))
        {
            Log("未识别画面中发现确认弹窗，立即识别并点击", LogLevel.Debug);
            if (!await E.Actions.PressButtonAsync(new[] { "下一步", "ok", "确定", "确认", "next" }, Ct))
            {
                await E.Pad.Confirm();
                await E.Pad.Wait(700, Ct);
            }
            _safeUnknown = 0;
            return false;
        }

        _safeUnknown++;
        if (_safeUnknown <= 4)
        {
            Log($"中途未识别画面，等待不操作（{_safeUnknown}/4，可能在匹配/过场）", LogLevel.Warn);
            await E.Pad.Wait(2200, Ct);
            return false;
        }

        if (_safeUnknown <= 8)
        {
            if (_safeUnknown == 5)
            {
                Log("持续未识别，按 1 次 B 尝试退回上层（不回主菜单）", LogLevel.Warn);
                await E.Pad.Back();
                await E.Pad.Wait(1600, Ct);
            }
            else
            {
                await E.Pad.Wait(1800, Ct);
            }
            return false;
        }

        Log("中途画面长时间无法恢复，回主页重新导航", LogLevel.Warn);
        await E.BackToHomeAsync(Ct);
        _safeUnknown = 0;
        return true;
    }

    protected bool TargetReached(int done, int target) => target > 0 && done >= target;

    /// <summary>
    /// 进入活动列表，但导航超时（如比赛其实还在踢，回不到主页）只告警不抛异常，
    /// 避免单次 WaitForAny 超时直接终止整个机器人。
    /// </summary>
    protected async Task<bool> SafeEnterEventHubAsync()
    {
        try
        {
            await E.EnterEventHubAsync(Ct);
            return true;
        }
        catch (Exception ex)
        {
            Log($"重新进入活动列表失败（{ex.GetType().Name}: {ex.Message}），本轮跳过稍后重试", LogLevel.Warn);
            await E.Pad.Wait(3000, Ct);
            return false;
        }
    }

    /// <summary>
    /// 全流程闭环导航到比赛页指定卡片：
    /// 在首页 → 先锚定游戏主菜单（Tab 跑偏则自动 LB 返回），再进入比赛页；
    /// 已在比赛页 → 直接闭环步行到目标卡（禁用卡上自动走回）。
    /// 返回 false=导航失败已记录错误并停机，调用方应结束场景。
    /// </summary>
    protected async Task<bool> NavigateToCardAsync(string targetCard)
    {
        var o = await E.ObserveAsync(Ct);
        if (o.Is(GameScreen.Home))
        {
            if (!await E.Navigation.EnsureAnchoredAsync(new HomeNode(), Ct)) return false;
            if (!await E.GoToAsync(nameof(GameScreen.MatchHub), null, Ct))
            {
                Log("无法进入比赛页，导航终止", LogLevel.Error);
                return false;
            }
        }
        else if (!o.Is(GameScreen.MatchHub))
        {
            Log($"当前不在首页 / 比赛页（{o.Screen}），无法导航到比赛卡片", LogLevel.Error);
            return false;
        }

        return await E.Navigation.EnsureAnchoredAsync(new MatchHubNode(targetCard), Ct);
    }

    /// <summary>
    /// 从赛前画面开始，完整打完（或放弃）一场比赛，直到回到调用方所在页面之外。
    /// 调用时应处于 PreMatch / Schedule 刚按完进入之后。
    /// 实际逻辑委托给 <see cref="MatchFlow"/> 状态机（7 阶段）。
    /// </summary>
    protected async Task<MatchRun> PlayOneMatchAsync(bool keyMatch, bool forfeit)
    {
        var run = new MatchRun { IsKeyMatch = keyMatch };

        // 初始观察 + PassThrough（与原版一致）
        var o = await E.ObserveAsync(Ct);
        o = await E.Overlay.PassThroughAsync(o, Ct);

        var opts = new MatchOptions
        {
            ForfeitNormal = forfeit,
            IsKeyMatch = keyMatch,
            Difficulty = Cfg.MyLeague.Difficulty,
            MatchTimeoutMin = Cfg.Timing.MatchTimeoutMin,
            IdleTapMinMs = Cfg.Timing.IdleTapMinMs,
            IdleTapMaxMs = Cfg.Timing.IdleTapMaxMs,
        };

        var result = await MatchFlow.RunAsync(o, opts, Ct);
        switch (result)
        {
            case MatchResult.Completed:
                run.Completed = true;
                break;
            case MatchResult.Forfeited:
                run.Forfeited = true;
                run.Completed = true;
                break;
            case MatchResult.Timeout:
                // 未确认完赛，不跑赛后连按，交回外层重新观察
                break;
        }
        return run;
    }

    /// <summary>
    /// 赛后结算 / 奖励 → 逐个点击返回。
    /// 检测到「下一步 / OK / 确定 / 确认」时走 PressButtonAsync（识别按钮→若不在默认焦点则移动→点击），
    /// 避免按钮非默认焦点时直接按 A 无效。无按钮的画面（加载等）直接按 A 继续。
    /// </summary>
    protected async Task PostMatchSequenceAsync()
    {
        Log("处理赛后结算与奖励…", LogLevel.Info);
        var deadline = DateTime.Now.AddSeconds(180);
        int stable = 0;
        GameScreen? last = null;
        string[] confirmBtns = { "下一步", "ok", "确定", "确认", "skip", "跳过" };
        while (DateTime.Now < deadline)
        {
            Ct.ThrowIfCancellationRequested();
            var o = await E.ObserveAsync(Ct);

            // 有明确确认按钮：走 PressButtonAsync（自动处理焦点移动）
            bool hasBtn = confirmBtns.Any(kw => o.Ocr.Contains(kw));
            if (hasBtn)
            {
                stable = 0;
                if (!await E.Actions.PressButtonAsync(confirmBtns, Ct))
                {
                    await E.Pad.Confirm();
                    await E.Pad.Wait(700, Ct);
                }
                continue;
            }

            if (o.Screen is GameScreen.Result or GameScreen.Rewards or GameScreen.FullTime
                or GameScreen.Dialog or GameScreen.Loading)
            {
                stable = 0;
                await E.Pad.Confirm();
                await E.Pad.Wait(700, Ct);
                continue;
            }
            if (last == o.Screen) stable++;
            else stable = 0;
            last = o.Screen;
            if (stable >= 3) break;
            await E.Pad.Confirm();
            await E.Pad.Wait(700, Ct);
        }
    }

    // ================= 巡回赛主页共享流程（国服 / 国际服周活动通用） =================

    /// <summary>
    /// 活动列表收拢到「对阵AI」最左活动卡：Up 到二级 Tab 行，检测「对阵AI」是否白亮选中
    /// （暗=焦点在「对阵玩家」，Left 切回），再 Down 进卡片区、Left 连按到最左。
    /// 注意：该列表是横向卡片，不能用旧版纵向 Up/Down 选卡，否则焦点停在 Tab 上 A 无效。
    /// </summary>
    protected async Task CollapseToAiEventsAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            await E.Pad.Dpad(PadDir.Up);
            await E.Pad.Wait(140, Ct);
        }
        await E.Pad.Wait(400, Ct);

        var o = await E.ObserveAsync(Ct);
        // 两个 Tab 各自锚定测亮度：只凭 AI Tab 单侧 ROI 误判率高（会把已选中的 AI 误判成暗
        // 而 Left 切到玩家空页）。规则：AI 暗 且 玩家明显更亮才 Left。
        var aiTab = o.Ocr.Words
            .Where(w => OcrText.Norm(w.Text).Equals("对阵ai") || OcrText.Norm(w.Text).Equals("对阵电脑"))
            .MinBy(w => w.X)
            ?? o.Ocr.FindFirst("对阵ai");
        var playerTab = o.Ocr.Words
            .Where(w => OcrText.Norm(w.Text).Equals("对阵玩家"))
            .MinBy(w => w.X)
            ?? o.Ocr.FindFirst("对阵玩家");
        if (aiTab is not null && playerTab is not null)
        {
            double aiGlow = TabGlow(o, aiTab);
            double playerGlow = TabGlow(o, playerTab);
            Log($"二级 Tab：对阵AI 亮度={aiGlow:F2} / 对阵玩家 亮度={playerGlow:F2}", LogLevel.Debug);
            if (aiGlow < 0.25 && playerGlow > aiGlow + 0.10)
            {
                Log("焦点在「对阵玩家」，Left 切到「对阵AI」", LogLevel.Info);
                await E.Pad.Dpad(PadDir.Left);
                await E.Pad.Wait(450, Ct);
            }
        }

        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(350, Ct);
        for (int i = 0; i < 6; i++)
        {
            await E.Pad.Dpad(PadDir.Left);
            await E.Pad.Wait(140, Ct);
        }
        await E.Pad.Wait(400, Ct);
    }

    /// <summary>测某个 Tab 词周围区域的高亮/白色占比（选中 Tab 会白亮）。</summary>
    private static double TabGlow(Observation o, OcrWord w)
    {
        if (o.Frame is null) return 0;
        double rx = Math.Clamp(w.X - 0.09, 0, 0.9);
        double ry = Math.Clamp(w.Y - w.H, 0, 0.9);
        var region = new NormRect(rx, ry, Math.Min(0.20, 1 - rx), w.H * 2.8);
        return Math.Max(
            FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Bright, region),
            FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.White, region));
    }

    // ---- 挑战清单卡状态：用国服 ChallengeCardReader 动态定位焦点卡（替代旧固定 ROI）----

    /// <summary>
    /// 用国服 ChallengeCardReader 动态定位焦点卡并读取锁/绿勾状态。
    /// 语义化返回 Info：Located=是否定位到焦点卡；Unlocked=未挂锁（可打）；Done=已打勾完成。
    /// 锁/绿勾阈值与动态定位规则与国服一致（ChallengeCardReader.Read 内建）。
    /// </summary>
    protected ChallengeCardReader.Info ReadChallengeCard(Observation o)
    {
        double offX = 8, offY = 31, cliW = 1280, cliH = 720;
        if (E.Window is not null)
        {
            var g = GameWindow.GetWindowFrame(E.Window.Handle);
            if (g.CliW > 0 && g.CliH > 0)
            {
                // DPI/全屏下逻辑窗口尺寸 ≠ 物理帧尺寸，先按帧/窗比修正
                double sx = o.Frame.Width > 0 && g.WinW > 0 ? (double)o.Frame.Width / g.WinW : 1.0;
                double sy = o.Frame.Height > 0 && g.WinH > 0 ? (double)o.Frame.Height / g.WinH : 1.0;
                (offX, offY, cliW, cliH) = (g.OffX * sx, g.OffY * sy, g.CliW * sx, g.CliH * sy);
            }
        }
        return ChallengeCardReader.Read(o.Frame, o.Ocr, offX, offY, cliW, cliH);
    }

    /// <summary>右侧「获得的主要奖励」对勾检测区（客户区坐标；对勾直径约 24px，条目 1~2 条）。</summary>
    private static readonly NormRect MainRewardCheckZone = new(0.90, 0.62, 0.085, 0.28);
    // 有勾实测绿度 0.018，无勾 0；阈值 0.008
    private const double MainRewardCheckMin = 0.008;

    /// <summary>
    /// 巡回赛主页处理（严格按链路）：
    /// ① 连续确认赛后弹窗（获得的活动积分 / 物品已送到收件箱）直到无弹窗；
    /// ② 检测右侧主要奖励对勾：无勾 → 设置（子类钩子）后直接前往比赛；
    /// ③ 有勾 → 进 i 详情页核对主要奖励：全部 x/x 则活动完成、回活动列表换下一个；
    ///    存在 y/x 则设置后返回前往比赛继续刷。
    /// 返回 true 表示主要奖励已满、已退回活动列表（调用方应换下一个活动）。
    /// </summary>
    protected async Task<bool> HandleTourHomeAsync()
    {
        // ① 赛后弹窗：有「确定/下一步」就一直确认
        for (int i = 0; i < 8; i++)
        {
            Ct.ThrowIfCancellationRequested();
            var p = await E.ObserveAsync(Ct);
            if (!HasModalPopup(p)) break;
            Log("赛后弹窗：确认（活动积分/收件箱）", LogLevel.Info);
            await E.Pad.Confirm();
            await E.Pad.Wait(1800, Ct);
        }

        // ② 主要奖励对勾
        var o = await E.ObserveAsync(Ct);

        // 护栏：分类器误判（如挑战赛前画面也有「前往比赛」）时，没有巡回/联赛主页强特征词，
        // 绝不能 Left+A「前往比赛」，等待复测后交给外层状态机
        if (!HasTourHomeMarker(o))
        {
            Log("画面有「前往比赛」但无活动积分/赛事等级/排名等主页特征，等待复测避免误操作", LogLevel.Warn);
            await E.Pad.Wait(1500, Ct);
            return false;
        }

        bool hasCheck = HasMainRewardCheck(o, MainRewardCheckZone, MainRewardCheckMin, out var checkEvidence);
        Log($"主要奖励对勾：{(hasCheck ? "有" : "无")}（{checkEvidence}）", LogLevel.Info);

        if (!hasCheck)
        {
            await BeforeTourMatchAsync();
            await StartMatchFromHomeAsync();
            return false;
        }

        // ③ 有勾：先 Left 归位「前往比赛」，再 Right 到 i 图标，A 进入详情
        await E.Pad.Dpad(PadDir.Left);
        await E.Pad.Wait(200, Ct);
        await E.Pad.Dpad(PadDir.Right);
        await E.Pad.Wait(350, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2500, Ct);

        var detail = await E.ObserveAsync(Ct);
        bool allDone = ParseAllMainRewardsDone(detail);

        // B 关闭详情页回主页
        await E.Pad.Back();
        await E.Pad.Wait(1800, Ct);
        await WaitScreenAsync(GameScreen.MyLeagueHome, 3);

        if (allDone)
        {
            Log("主要奖励全部 x/x：本活动已完成，返回活动列表选择下一个活动", LogLevel.Success);
            await E.Pad.Back(); // 主页底部 B 返回 → 活动列表
            await E.Pad.Wait(2500, Ct);
            var list = await WaitScreenAsync(GameScreen.EventHub, 3);
            if (list is null)
            {
                Log("未回到活动列表，再按一次 B", LogLevel.Warn);
                await E.Pad.Back();
                await E.Pad.Wait(2500, Ct);
            }
            return true;
        }

        Log("主要奖励仍有 y/x：返回前往比赛，继续刷", LogLevel.Info);
        await BeforeTourMatchAsync();
        await StartMatchFromHomeAsync();
        return false;
    }

    /// <summary>主页是否有模态弹窗：主页本身无「确定」按钮，OCR 出现确定/弹窗独有词即有弹窗。</summary>
    private static bool HasModalPopup(Observation o)
        => o.Ocr.Contains("确定")
           || o.Ocr.Contains("获得的活动积分")
           || o.Ocr.Contains("物品已送到收件箱")
           || o.Ocr.Contains("过期")
           || o.Ocr.ContainsJoined("过期")
           || o.Ocr.Contains("获取新物品");

    /// <summary>巡回/我的联赛主页强特征词（赛前画面不会有）。</summary>
    private static bool HasTourHomeMarker(Observation o)
        => o.Ocr.Contains("活动积分") || o.Ocr.Contains("赛事等级")
           || o.Ocr.Contains("下一场") || o.Ocr.Contains("联赛排名")
           || o.Ocr.Contains("赛程表") || o.Ocr.Contains("积分榜");

    /// <summary>焦点归位「前往比赛」（弹窗/详情关闭后焦点可能在右侧 i 图标），Left 后 A。</summary>
    protected async Task StartMatchFromHomeAsync()
    {
        Log("巡回赛主页：前往比赛", LogLevel.Info);
        await E.Pad.Dpad(PadDir.Left);
        await E.Pad.Wait(250, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2500, Ct);
    }

    /// <summary>等待进入指定屏幕（最多 tries 次观测），返回到达时的观察；未到达返回 null。</summary>
    protected async Task<Observation?> WaitScreenAsync(GameScreen screen, int tries)
    {
        for (int i = 0; i < tries; i++)
        {
            Ct.ThrowIfCancellationRequested();
            var o = await E.ObserveAsync(Ct);
            if (o.Is(screen)) return o;
            await E.Pad.Wait(900, Ct);
        }
        return null;
    }

    /// <summary>
    /// 解析详情页「主要奖励」区进度，全部为 x/x（分子=分母）才返回 true。
    /// 区域边界优先用 OCR 动态定位（「主要奖励」标题 ~「活动时间」标题），回退固定值。
    /// 注意：必须用原始 Words 按行重组——OcrText.Norm 会删掉斜杠。
    /// </summary>
    protected bool ParseAllMainRewardsDone(Observation o)
    {
        double yTop = 0.22, yBottom = 0.52;
        foreach (var w in o.Ocr.Words)
        {
            string t = OcrText.Norm(w.Text);
            if (t == "主要奖励" && w.CenterX < 0.4) yTop = w.Y + w.H;
            else if (t.Contains("活动时间")) yBottom = w.Y;
        }

        // 区域内原始词按行聚类
        var inRows = o.Ocr.Words
            .Where(w => w.CenterY >= yTop && w.CenterY < yBottom)
            .OrderBy(w => w.CenterY)
            .ToList();
        var lines = new List<List<OcrWord>>();
        foreach (var w in inRows)
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l.Average(x => x.CenterY) - w.CenterY)
                < Math.Max(w.H, l.Average(x => x.H)) * 0.8);
            if (line is null) lines.Add(new List<OcrWord> { w });
            else line.Add(w);
        }

        int total = 0, done = 0;
        foreach (var line in lines)
        {
            // 同一行可能并排放多张奖励卡（如 金币卡「30/120」+ 高光券卡「1/2」），
            // 按词间 x 间隙切成独立条目，避免去空格后数字粘连成「1201」
            foreach (var segment in SplitByXGap(line))
            {
                string compact = global::System.Text.RegularExpressions.Regex.Replace(
                    string.Concat(segment.Select(w => w.Text)), @"\s+", "");
                Log($"主要奖励行原文：{compact}", LogLevel.Debug);
                // 兼容 OCR 把 / 误识为 | I l ╱ 等（该段只有数字与分隔符，无误伤风险）
                var matches = global::System.Text.RegularExpressions.Regex.Matches(
                    compact, @"(\d+)[/|Il╱／](\d+)");
                foreach (global::System.Text.RegularExpressions.Match m in matches)
                {
                    int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
                    total++;
                    if (a == b) done++;
                    Log($"主要奖励进度：{a}/{b}{(a == b ? "（已满）" : "")}", LogLevel.Info);
                }
            }
        }

        if (total == 0)
        {
            Log("未解析到主要奖励进度，保守视为未完成，继续比赛", LogLevel.Warn);
            return false;
        }
        return done == total;
    }

    /// <summary>
    /// 一行词按 x 间隙分段：相邻词间隙超过 max(0.03, 相邻词宽的0.6倍) 即断开。
    /// 同一条目内被拆开的数字/斜杠间距仅几像素；并排卡片间间隙约 0.29。
    /// </summary>
    protected static List<List<OcrWord>> SplitByXGap(List<OcrWord> line)
    {
        var ordered = line.OrderBy(w => w.X).ToList();
        var result = new List<List<OcrWord>>();
        if (ordered.Count == 0) return result;

        var cur = new List<OcrWord> { ordered[0] };
        for (int k = 1; k < ordered.Count; k++)
        {
            double gap = ordered[k].X - (ordered[k - 1].X + ordered[k - 1].W);
            double cut = Math.Max(0.03, Math.Max(ordered[k].W, ordered[k - 1].W) * 0.6);
            if (gap > cut)
            {
                result.Add(cur);
                cur = new List<OcrWord>();
            }
            cur.Add(ordered[k]);
        }
        result.Add(cur);
        return result;
    }

    /// <summary>
    /// 客户区归一化坐标 → 整窗帧归一化坐标。WGC 帧含标题栏/边框，
    /// 必须按真实窗口外框与客户区偏移换算，小 ROI 才不会错位。
    /// </summary>
    protected NormRect ToFrameRoi(FrameData f, NormRect cr)
    {
        if (E.Window is null) return cr;
        var g = GameWindow.GetWindowFrame(E.Window.Handle);
        if (g.CliW <= 0 || g.CliH <= 0 || f.Width <= 0 || f.Height <= 0) return cr;

        // DPI 缩放或全屏模式可能导致窗口坐标（逻辑像素）与捕获帧（物理像素）尺寸不一致。
        // 用帧尺寸与窗口尺寸的比值修正，确保小 ROI 映射正确。
        double sx = (double)f.Width / g.WinW;
        double sy = (double)f.Height / g.WinH;
        double fx = (g.OffX * sx + cr.X * g.CliW * sx) / f.Width;
        double fy = (g.OffY * sy + cr.Y * g.CliH * sy) / f.Height;
        double fw = cr.W * g.CliW * sx / f.Width;
        double fh = cr.H * g.CliH * sy / f.Height;
        return new NormRect(fx, fy, fw, fh);
    }

    /// <summary>小区域逐像素指定颜色占比。</summary>
    protected static double ColorRatioEx(FrameData f, HsvFilter filter, NormRect r)
    {
        var (x, y, w, h) = FrameAnalyzer.PixelRect(f, r);
        int hit = 0;
        var b = f.Bgra;
        for (int yy = y; yy < y + h; yy++)
        {
            int row = yy * f.Width * 4;
            for (int xx = x; xx < x + w; xx++)
            {
                int i = row + xx * 4;
                if (FrameAnalyzer.MatchHsv(b[i + 2], b[i + 1], b[i], filter)) hit++;
            }
        }
        return hit / (double)(w * h);
    }

    /// <summary>
    /// 「主要奖励」绿勾检测（OCR 锚定 + 逐列绿色簇扫描，分辨率/宽高比无关）。
    /// 全屏（21:9）下 UI 整体重排，固定归一化 ROI 全部落空，必须跟随 OCR 文字。
    /// 结构：标题「(获得的)主要奖励」下方为条目带，每行 = 左侧圆形图标 + 文字 + 右端绿勾。
    /// 图标列与绿勾列水平间隔 ≥0.1 帧宽 → 列簇分离：
    ///   图标列右缘以远的簇 = 绿勾（绿圆含白勾，绿像素约占外接框 40%+）；
    ///   只有图标列 = 无勾；条目图标为金币（黄）时无图标簇，仅绿勾簇也在远端。
    /// OCR 找不到标题时回退旧固定检测区（16:9 窗口化校准值），保持旧行为兜底。
    /// 返回是否有勾；evidence 输出日志用（绿勾簇绿占比 / 回退时为区域绿度）。
    /// </summary>
    protected bool HasMainRewardCheck(Observation o, NormRect fallbackZone, double fallbackMin, out string evidence)
    {
        var f = o.Frame;
        double fallbackGreen = ColorRatioEx(f, HsvFilter.Green, ToFrameRoi(f, fallbackZone));

        var title = o.Ocr.FindFirst("主要奖励");
        if (title is null)
        {
            evidence = $"回退固定区绿度={fallbackGreen:F3}";
            return fallbackGreen >= fallbackMin;
        }

        // 条目带：标题下方 1~2 行（约 0.10 覆盖 1.5 行，比旧 0.20 精确，避免绿勾被空白稀释）
        double x0 = Math.Max(0, title.X - 0.03);
        double y0 = title.Y + title.H + 0.004;
        var band = new NormRect(x0, y0, 1.0 - x0, Math.Min(0.10, 1.0 - y0));
        var (px, py, pw, ph) = FrameAnalyzer.PixelRect(f, band);
        if (pw < 20 || ph < 10)
        {
            evidence = $"条目带无效，回退固定区绿度={fallbackGreen:F3}";
            return fallbackGreen >= fallbackMin;
        }

        // 逐列绿色计数
        var colGreen = new int[pw];
        var b = f.Bgra;
        for (int yy = py; yy < py + ph; yy++)
        {
            int row = yy * f.Width * 4;
            for (int xx = px; xx < px + pw; xx++)
            {
                int i = row + xx * 4;
                if (FrameAnalyzer.MatchHsv(b[i + 2], b[i + 1], b[i], HsvFilter.Green))
                    colGreen[xx - px]++;
            }
        }

        // 列簇提取：连续有绿列（列高≥3px 防噪点），簇宽 ≥0.8% 帧宽（≈10px@1280 / 15px@1920）
        int minColHit = Math.Max(3, ph / 20);
        int minClusterW = Math.Max(6, (int)(f.Width * 0.008));
        var clusters = new List<(int Start, int End)>();
        int cs = -1;
        for (int i = 0; i < pw; i++)
        {
            if (colGreen[i] >= minColHit)
            {
                if (cs < 0) cs = i;
            }
            else if (cs >= 0)
            {
                if (i - cs >= minClusterW) clusters.Add((cs, i - 1));
                cs = -1;
            }
        }
        if (cs >= 0 && pw - cs >= minClusterW) clusters.Add((cs, pw - 1));

        // 图标列右缘：图标在标题左端正下方，直径 ≈0.03~0.05 帧宽，取 title.X+0.07 为界
        int iconRightPx = (int)((title.X + 0.07) * f.Width);
        var checkClusters = clusters.Where(c => px + c.Start > iconRightPx).ToList();
        if (checkClusters.Count == 0)
        {
            evidence = $"绿簇={clusters.Count}（均图标列），无勾";
            return false;
        }

        // 绿勾簇外接框内绿占比（绿勾圆直径≈24px，外接框含大量空白，实测填充率约 0.10~0.35；阈值 0.08）
        int cx0 = px + checkClusters.Min(c => c.Start);
        int cx1 = px + checkClusters.Max(c => c.End);
        long hit = 0, total = 0;
        for (int yy = py; yy < py + ph; yy++)
        {
            int row = yy * f.Width * 4;
            for (int xx = cx0; xx <= cx1; xx++)
            {
                int i = row + xx * 4;
                total++;
                if (FrameAnalyzer.MatchHsv(b[i + 2], b[i + 1], b[i], HsvFilter.Green)) hit++;
            }
        }
        double checkGreen = total > 0 ? hit / (double)total : 0;
        evidence = $"绿簇={clusters.Count}，勾区绿占比={checkGreen:F3}";
        return checkGreen >= 0.08;
    }

    // ================= 通用按钮高光/确认（活动详情/弹窗） =================

    private const double GlowThreshold = 0.12;

    /// <summary>
    /// 通用确认规则：屏幕上有「确定」优先点确定，否则有「进入」点进入。
    /// 目标按钮未被高光聚焦时，定位当前焦点按钮并按方向键移过去，到位再按 A。
    /// 适用于挑战赛信息弹窗、活动详情页等分类器未专门建模的画面。
    /// </summary>
    protected async Task<bool> TryConfirmOrEnterAsync(Observation o)
    {
        var target = o.Ocr.FindFirst("确定") ?? o.Ocr.FindFirst("进入");
        if (target is null) return false;

        // 已聚焦：直接确认
        if (IsButtonGlowing(o, target))
        {
            Log($"通用确认：「{target.Text}」已聚焦，确认", LogLevel.Debug);
            await E.Pad.Confirm();
            await E.Pad.Wait(2000, Ct);
            return true;
        }

        // 焦点可能在其他按钮上：逐次朝目标移动，最多 5 步
        string[] buttonWords = { "确定", "进入", "详情", "关闭", "取消", "返回", "下一步", "完成" };
        for (int step = 0; step < 5; step++)
        {
            Ct.ThrowIfCancellationRequested();
            o = await E.ObserveAsync(Ct);

            target = o.Ocr.FindFirst("确定") ?? o.Ocr.FindFirst("进入");
            if (target is null) return false;
            if (IsButtonGlowing(o, target))
            {
                await E.Pad.Confirm();
                await E.Pad.Wait(2000, Ct);
                return true;
            }

            // 找当前聚焦的按钮
            OcrWord? focus = null;
            double bestGlow = GlowThreshold;
            foreach (var kw in buttonWords)
                foreach (var w in o.Ocr.FindAll(kw))
                {
                    double g = E.Actions.ButtonGlow(o.Frame, w);
                    if (g > bestGlow) { bestGlow = g; focus = w; }
                }

            if (focus is null)
            {
                // 定位不到焦点：弹窗类默认焦点通常已在「确定」，盲按 A 兜底
                Log($"定位不到焦点，直接确认「{target.Text}」", LogLevel.Debug);
                await E.Pad.Confirm();
                await E.Pad.Wait(2000, Ct);
                var after = await E.ObserveAsync(Ct);
                return after.Screen != o.Screen || !after.Ocr.Contains(target.Text);
            }

            double dx = target.CenterX - focus.CenterX;
            double dy = target.CenterY - focus.CenterY;
            Log($"移动焦点：「{focus.Text}」→「{target.Text}」", LogLevel.Debug);
            if (Math.Abs(dx) > Math.Abs(dy))
                await E.Pad.Dpad(dx > 0 ? PadDir.Right : PadDir.Left);
            else
                await E.Pad.Dpad(dy > 0 ? PadDir.Down : PadDir.Up);
            await E.Pad.Wait(450, Ct);
        }
        return false;
    }

    /// <summary>按钮是否被高光聚焦：综合白色描边与亮底比例。</summary>
    protected bool IsButtonGlowing(Observation o, OcrWord w) => E.Actions.ButtonGlow(o.Frame, w) >= GlowThreshold;
}
