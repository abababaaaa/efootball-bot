using EfootballBot.Core.Bot;
using EfootballBot.Core.Input;
using EfootballBot.Core.Match;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Overlay;

/// <summary>我的联赛场景状态机：观察当前画面 → 判断状态 → 执行对应操作。</summary>
public sealed class MyLeagueStateMachine
{
    private readonly BotEngine _engine;
    private readonly MatchFlowService _matchFlow;

    public MyLeagueStateMachine(BotEngine engine)
    {
        _engine = engine;
        _matchFlow = new MatchFlowService(engine);
    }

    /// <summary>场景状态枚举。</summary>
    public enum State
    {
        Unknown,            // 未知
        MyLeagueHome,       // 我的联赛主页
        Schedule,           // 赛程表
        DifficultySelect,   // 难度选择弹窗
        ConfirmDialog,      // 确认弹窗（关键比赛）
        PreMatch,           // 赛前准备
        InMatch,            // 比赛中
        HalfTime,           // 半场
        FullTime,           // 全场结束
        Result,             // 比赛结果
        Rewards,            // 奖励结算
        Exchange,           // 兑换积分页面
        Shop,               // 我的联赛物品页面
        Dialog,             // 通用弹窗
    }

    /// <summary>执行一轮状态机：观察 → 判断 → 操作。返回状态与建议轮询间隔（毫秒）。</summary>
    public async Task<(State State, int DelayMs)> RunOnceAsync(CancellationToken ct)
    {
        var obs = await _engine.ObserveAsync(ct);
        var state = DetectState(obs);
        int delayMs = 600;

        _engine.LogInternal($"状态机：{state}（{obs.Screen}）", LogLevel.Debug);

        switch (state)
        {
            case State.MyLeagueHome:
                await HandleMyLeagueHomeAsync(obs, ct);
                break;
            case State.Schedule:
                await HandleScheduleAsync(obs, ct);
                break;
            case State.DifficultySelect:
                await HandleDifficultySelectAsync(obs, ct);
                break;
            case State.ConfirmDialog:
                await HandleConfirmDialogAsync(obs, ct);
                break;

            // ---- 比赛全流程（赛前开始点 / 比赛中 / 中场 / 全场 / 结果 / 奖励）----
            // 通用模块：右下角蓝色推进按钮立即响应；无按钮时按挂机节奏随机轻按 A
            case State.PreMatch:
            case State.InMatch:
            case State.HalfTime:
            case State.FullTime:
            case State.Result:
            case State.Rewards:
            {
                var (_, matchDelay) = await _matchFlow.RunOnceAsync(idleTapEnabled: true, ct);
                delayMs = matchDelay;
                break;
            }

            case State.Exchange:
                await HandleExchangeAsync(obs, ct);
                break;
            case State.Shop:
                await HandleShopAsync(obs, ct);
                break;
            case State.Dialog:
                await HandleDialogAsync(obs, ct);
                break;

            default:
            {
                // 未知画面兜底：左上角队徽/文字变化会导致探针判错，
                // 右下角蓝色推进按钮是跨页面稳定锚点，照样尝试推进；但不做节奏盲按
                var (_, fallbackDelay) = await _matchFlow.RunOnceAsync(idleTapEnabled: false, ct);
                delayMs = fallbackDelay;
                break;
            }
        }

        _lastState = state;
        return (state, delayMs);
    }

    /// <summary>检测当前状态。</summary>
    private State DetectState(EfootballBot.Core.Bot.Observation obs)
    {
        var text = obs.Ocr.JoinedText;

        // 最高优先级：顶部 AI 代打图标 = 正常比赛中（国际服/国服通用，位置全顶条带扫描；
        // 有图标时 AI 正在操作，按 A 安全，且可纠正探针因左上角图标/文字变化导致的误判）
        if (FrameAnalyzer.FindAiControlIcon(obs.Frame, out double iconX, out double iconY))
        {
            // 右侧顶栏图标区（实测真图标 cx≈0.935）无条件采信；
            // 左侧命中只可能是「国服错版」，必须探针也判比赛类页面——
            // 否则主页左上队徽等黑白元素会被误判成比赛中，导致道具检查被整体跳过
            bool leftSideAccepted = iconX < 0.80
                && obs.Screen is GameScreen.PreMatch or GameScreen.InMatch
                    or GameScreen.HalfTime or GameScreen.FullTime
                    or GameScreen.Result or GameScreen.Rewards;
            if (iconX >= 0.80 || leftSideAccepted)
            {
                _engine.LogInternal($"检测到 AI 代打图标（{iconX:F2},{iconY:F2}，探针 {obs.Screen}），判定比赛中", LogLevel.Debug);
                return State.InMatch;
            }
            _engine.LogInternal($"顶栏左侧疑似图标（{iconX:F2},{iconY:F2}）但探针判 {obs.Screen}，忽略", LogLevel.Debug);
        }

        // 弹窗优先判断
        if (text.Contains("选择赛事等级") || text.Contains("选择AI难度"))
            return State.DifficultySelect;

        if (text.Contains("确认") && text.Contains("关键比赛") && !text.Contains("选择赛事等级"))
            return State.ConfirmDialog;

        if (text.Contains("物品使用中") || text.Contains("已过期") || text.Contains("获取新物品"))
            return State.Dialog;

        if (obs.Screen == GameScreen.Dialog)
            return State.Dialog;

        // 兑换积分落地页：实测探针会把它误判成我的联赛主页（11.0 分），
        // 必须在主页分支前用「页面标题 我的联赛积分 + 菜单卡 租借球员」双词拦截。
        // 主页仅难度弹窗含「我的联赛积分」字样，但不会同时出现「租借球员」。
        // OCR 实测会把「赛」识成繁体「賽」（我的联賽积分），匹配前先折叠异体字。
        string folded = FoldOcrVariants(text);
        if (folded.Contains("我的联赛积分") && folded.Contains("租借球员"))
            return State.Exchange;

        // 页面判断
        if (obs.Screen == GameScreen.MyLeagueHome)
        {
            // 回到主页优先清弹窗：主页上覆盖模态弹窗（如物品已过期）时探针可能仍判主页。
            // 必须同时满足「浅色弹窗遮罩 + 中部蓝按钮」，避免深色主页 UI 被误判成弹窗后永远进不了主页
            if (FrameAnalyzer.HasModalOverlay(obs.Frame)
                && FrameAnalyzer.FindBlueDialogButton(obs.Frame, out _))
                return State.Dialog;
            return State.MyLeagueHome;
        }

        if (obs.Screen == GameScreen.Schedule)
            return State.Schedule;

        if (obs.Screen == GameScreen.PreMatch)
            return State.PreMatch;

        if (obs.Screen == GameScreen.InMatch)
            return State.InMatch;

        if (obs.Screen == GameScreen.HalfTime)
            return State.HalfTime;

        if (obs.Screen == GameScreen.FullTime)
            return State.FullTime;

        if (obs.Screen == GameScreen.Result)
            return State.Result;

        if (obs.Screen == GameScreen.Rewards)
            return State.Rewards;

        if (obs.Screen == GameScreen.Shop)
            return State.Shop;

        // 兑换积分页兜底：OCR 漏掉「租借球员」时，探针非主页 + 标题词仍可识别
        if (obs.Screen != GameScreen.MyLeagueHome && folded.Contains("我的联赛积分"))
            return State.Exchange;

        return State.Unknown;
    }

    /// <summary>我的联赛主页：检查物品、进入比赛。</summary>
    private async Task HandleMyLeagueHomeAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        // 边沿触发：只要本轮「刚回到主页」（上一轮是弹窗/比赛/商店/赛程表/未知等任意非主页状态），
        // 就重新走「清弹窗→查道具」流程。不依赖探针是否识别出比赛状态，
        // 赛后经 Unknown 兜底一路推进回主页也能保证重新检查消耗品。
        if (_lastState != State.MyLeagueHome)
        {
            _itemsChecked = false;
            _itemCheckFails = 0;
            _engine.LogInternal($"刚回到我的联赛主页（上一状态：{_lastState}），开始道具检查", LogLevel.Info);
        }

        // 1. 检查物品：必须确认成功才放行；失败下一轮重试，连续失败超上限则停机（绝不裸奔开赛）
        if (!_itemsChecked)
        {
            _engine.LogInternal("我的联赛主页：检查物品", LogLevel.Info);
            bool ok = await EnsureItemsAsync(ct);
            if (ok)
            {
                _itemsChecked = true;
                _itemCheckFails = 0;
            }
            else
            {
                _itemCheckFails++;
                if (_itemCheckFails >= MaxItemCheckFails)
                {
                    _engine.LogInternal(
                        $"物品检查连续 {_itemCheckFails} 次未完成，停止挂机以防无道具开赛，请人工检查后重新启动",
                        LogLevel.Error);
                    throw new InvalidOperationException("物品检查连续失败，已停止挂机（防止无道具进入比赛）");
                }
                _engine.LogInternal($"物品检查未完成（第 {_itemCheckFails}/{MaxItemCheckFails} 次），稍后重试，本轮不进入比赛", LogLevel.Warn);
            }
            return;
        }

        // 2. 进入比赛：把 DPad 高光锚定到左上角「前往比赛」（不依赖置信度极低的焦点卡检测）
        _engine.LogInternal("我的联赛主页：锚定高光到「前往比赛」", LogLevel.Info);
        await AnchorToGoToMatchAsync(ct);

        // 硬门控：任何情况下未确认道具齐全都不允许开赛
        if (!_itemsChecked)
        {
            _engine.LogInternal("道具尚未确认齐全，跳过本轮开赛", LogLevel.Warn);
            return;
        }

        _engine.LogInternal("高光已在「前往比赛」，按 A 进入", LogLevel.Info);
        await _engine.Pad.Confirm();
        await Task.Delay(2500, ct);
    }

    private const int MaxItemCheckFails = 8;
    private bool _itemsChecked = false;
    private int _itemCheckFails = 0;
    /// <summary>上一轮状态机状态，用于「刚回到主页」边沿触发道具检查。</summary>
    private State _lastState = State.Unknown;

    /// <summary>赛程表：选择比赛。</summary>
    private async Task HandleScheduleAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        // 门控：未完成道具确认（如启动时直接停在赛程表）时，退回主页先清弹窗、检查道具
        if (!_itemsChecked)
        {
            _engine.LogInternal("赛程表：尚未完成道具检查，按 B 退回主页先走「弹窗→道具」流程", LogLevel.Warn);
            await _engine.Pad.Back();
            await Task.Delay(1500, ct);
            return;
        }

        _engine.LogInternal("赛程表：选择比赛", LogLevel.Info);
        await _engine.Pad.Confirm();
        await Task.Delay(2000, ct);
    }

    /// <summary>难度选择弹窗：检测蓝色钩子位置，智能选择难度。</summary>
    private async Task HandleDifficultySelectAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        var targetDifficulty = _engine.Config.MyLeague.Difficulty;
        _engine.LogInternal($"难度选择：目标 {targetDifficulty}", LogLevel.Info);

        // 检测蓝色钩子位置（已选中的难度）
        // OCR 识别难度列表，FrameAnalyzer 检测蓝色区域
        var selectedDifficulty = DetectSelectedDifficulty(obs);
        _engine.LogInternal($"检测到已选中难度：{selectedDifficulty}", LogLevel.Info);

        // 如果已选中目标难度，直接点击确认
        if (selectedDifficulty == targetDifficulty)
        {
            _engine.LogInternal($"已选中目标难度「{targetDifficulty}」，直接点击确认", LogLevel.Info);
            // 移动到最下层（确认按钮）
            for (int i = 0; i < 10; i++) await _engine.Pad.Dpad(PadDir.Down);
            await Task.Delay(300, ct);
            // 向右平移到确认按钮
            await _engine.Pad.Dpad(PadDir.Right);
            await Task.Delay(300, ct);
            // 点击确认
            await _engine.Pad.Confirm();
            await Task.Delay(3000, ct);
            return;
        }

        // 未选中目标难度，需要移动
        _engine.LogInternal($"未选中目标难度，移动光标到「{targetDifficulty}」", LogLevel.Info);

        // 难度列表顺序：新手(0)、业余(1)、标准(2)、职业(3)、顶尖球员(4)、超级球星(5)、传奇(6)
        var difficultyIndex = targetDifficulty switch
        {
            "新手" => 0,
            "业余" => 1,
            "标准" => 2,
            "职业" => 3,
            "顶尖球员" => 4,
            "超级球星" => 5,
            "传奇" => 6,
            _ => 6,
        };

        // 先回到列表顶部
        for (int i = 0; i < 10; i++) await _engine.Pad.Dpad(PadDir.Up);
        await Task.Delay(300, ct);

        // 向下移动到目标难度
        for (int i = 0; i < difficultyIndex; i++)
        {
            await _engine.Pad.Dpad(PadDir.Down);
            await Task.Delay(300, ct);
        }

        // 移动到最下层（确认按钮）
        for (int i = 0; i < 10; i++) await _engine.Pad.Dpad(PadDir.Down);
        await Task.Delay(300, ct);

        // 向右平移到确认按钮
        await _engine.Pad.Dpad(PadDir.Right);
        await Task.Delay(300, ct);

        // 点击确认
        await _engine.Pad.Confirm();
        await Task.Delay(3000, ct);
    }

    /// <summary>检测难度选择弹窗中已选中的难度（行首蓝色对勾位置）。</summary>
    private string DetectSelectedDifficulty(EfootballBot.Core.Bot.Observation obs)
    {
        // 7 档难度（关键比赛可能只有部分，逐行检测即可）
        var difficulties = new[] { "新手", "业余", "标准", "职业", "顶尖球员", "超级球星", "传奇" };
        var ratios = new List<(string Diff, double Ratio)>();

        foreach (var diff in difficulties)
        {
            // 找到该难度名对应的 OCR 词（多字词可能被 OCR 拆开，双向包含匹配，至少 2 字）
            OcrWord? word = null;
            foreach (var w in obs.Ocr.Words)
            {
                if (w.Text.Length >= 2 && (w.Text.Contains(diff) || diff.Contains(w.Text)))
                {
                    word = w;
                    break;
                }
            }

            if (word is null)
            {
                _engine.LogInternal($"难度「{diff}」未出现在列表中（可能是关键比赛）", LogLevel.Debug);
                continue;
            }

            // 蓝勾在难度词左侧：X 约 word.X - 0.06 ~ word.X - 0.015，Y 与词对齐
            double x0 = Math.Max(0, word.X - 0.07);
            double x1 = Math.Max(0, word.X - 0.015);
            double y0 = Math.Max(0, word.Y - 0.012);
            double y1 = Math.Min(1, word.Y + word.H + 0.012);

            if (x1 <= x0) continue;

            var region = new NormRect(x0, y0, x1 - x0, y1 - y0);
            var blueRatio = FrameAnalyzer.CheckBlueRatioExact(obs.Frame, region);
            ratios.Add((diff, blueRatio));
            _engine.LogInternal($"难度「{diff}」行首蓝勾占比：{blueRatio:F3}", LogLevel.Debug);
        }

        // 蓝勾笔画仅 2~3px，占比绝对值很小（实测选中行≈0.011，未选中=0.000）。
        // 用"最大值胜出"：占比最高且 ≥0.005 的行即为选中行。
        var best = ratios.OrderByDescending(r => r.Ratio).FirstOrDefault();
        _engine.LogInternal($"各行蓝勾占比：{string.Join(" / ", ratios.Select(r => $"{r.Diff}={r.Ratio:F3}"))}", LogLevel.Debug);

        if (best.Ratio >= 0.005)
        {
            _engine.LogInternal($"蓝勾位于「{best.Diff}」行（占比 {best.Ratio:F3}）", LogLevel.Info);
            return best.Diff;
        }

        _engine.LogInternal($"未检测到蓝勾（最高占比 {best.Ratio:F3}）", LogLevel.Warn);
        return "未知";
    }

    /// <summary>确认弹窗（关键比赛）：上移高光并点击。</summary>
    private async Task HandleConfirmDialogAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        _engine.LogInternal("确认弹窗：上移高光并点击", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Up);
        await Task.Delay(300, ct);
        await _engine.Pad.Confirm();
        await Task.Delay(2000, ct);
    }

    /// <summary>兑换积分页面：进入我的联赛物品。</summary>
    private async Task HandleExchangeAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        _engine.LogInternal("兑换积分页面：进入我的联赛物品", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Right);
        await Task.Delay(400, ct);
        await _engine.Pad.Confirm();
        await Task.Delay(2500, ct);
    }

    /// <summary>我的联赛物品页面：购买缺失物品。</summary>
    private async Task HandleShopAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        _engine.LogInternal("我的联赛物品页面：购买缺失物品", LogLevel.Info);

        // 物品列表顺序：状态:普通+(0)、状态:好+(1)、状态:杰出(2)、经验值:2x(3)、经验值:3x(4)、经验值:4x(5)、控球:99(6)、头球:99(7)、加速:99(8)、FW球员能力值+(9)、MF球员能力值+(10)、DF球员能力值+(11)、主教练强化(12)
        var itemDownCounts = new Dictionary<string, int>
        {
            ["杰出状态"] = 2,
            ["4倍经验"] = 5,
            ["主教练强化"] = 12,
        };

        foreach (var item in itemDownCounts)
        {
            var itemName = item.Key;
            var downCount = item.Value;

            // 检查是否需要购买该物品
            bool shouldBuy = itemName switch
            {
                "杰出状态" => _engine.Config.MyLeague.BuyExcellentCondition,
                "4倍经验" => _engine.Config.MyLeague.Buy4xExp,
                "主教练强化" => _engine.Config.MyLeague.BuyManagerBoost,
                _ => false,
            };

            if (!shouldBuy)
            {
                _engine.LogInternal($"跳过「{itemName}」（未配置购买）", LogLevel.Debug);
                continue;
            }

            _engine.LogInternal($"购买「{itemName}」，向下移动 {downCount} 次", LogLevel.Info);

            // 先回到列表顶部
            for (int i = 0; i < 15; i++) await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(300, ct);

            // 向下移动到目标物品
            for (int i = 0; i < downCount; i++)
            {
                await _engine.Pad.Dpad(PadDir.Down);
                await Task.Delay(300, ct);
            }

            // 按 A 购买
            _engine.LogInternal($"按 A 购买「{itemName}」", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(1500, ct);

            // 处理购买确认弹窗（上移 + 点击）
            _engine.LogInternal("处理购买确认弹窗", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(300, ct);
            await _engine.Pad.Confirm();
            await Task.Delay(2000, ct);

            // 处理"物品使用中"成功弹窗（点击 OK）
            _engine.LogInternal("处理物品使用中弹窗", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(1500, ct);

            _engine.LogInternal($"「{itemName}」购买完成", LogLevel.Success);
        }

        // 全部购买完毕，返回我的联赛首页
        _engine.LogInternal("全部购买完毕，返回我的联赛首页", LogLevel.Info);
        await _engine.Pad.Back();
        await Task.Delay(1500, ct);
        await _engine.Pad.Back();
        await Task.Delay(2000, ct);
    }

    /// <summary>
    /// 通用弹窗处理（每次回到主页优先执行）：
    /// 1. PressButtonAsync 找 OK/确定/确认/下一步，蓝焦点不在目标上则自动移动（上下双按钮也适用）；
    /// 2. OCR 漏识按钮词时：看到蓝色确认按钮直接 A（单按钮弹窗）；
    /// 3. 上下双按钮且默认焦点在下层（无蓝按钮可见）：先上移再尝试点击。
    /// </summary>
    private async Task HandleDialogAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        string[] targets = { "ok", "确定", "确认", "下一步", "yes" };

        // 1. 指针感知点击：识别按钮词 → 蓝/青焦点检测 → 移动 → A
        if (await _engine.Actions.PressButtonAsync(targets, ct))
        {
            _engine.LogInternal("弹窗：确认按钮已点击", LogLevel.Info);
            await Task.Delay(900, ct);
            return;
        }

        var fresh = await _engine.ObserveAsync(ct);

        // 2. 单按钮弹窗：蓝色确认按钮已聚焦（OCR 可能漏识文字），直接 A
        if (FrameAnalyzer.FindBlueDialogButton(fresh.Frame, out double dcy)
            || FrameAnalyzer.FindBlueAdvanceButton(fresh.Frame, out _))
        {
            _engine.LogInternal($"弹窗：检测到蓝色确认按钮（y={dcy:F2}），直接按 A", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(900, ct);
            return;
        }

        // 3. 上下双按钮弹窗，默认焦点在下层：先上移，再识别点击
        _engine.LogInternal("弹窗：未见蓝色确认按钮，按 Up 移到上层确认项", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Up);
        await Task.Delay(600, ct);

        if (await _engine.Actions.PressButtonAsync(targets, ct))
        {
            _engine.LogInternal("弹窗：上移后确认按钮已点击", LogLevel.Info);
            await Task.Delay(900, ct);
            return;
        }

        // 上移后蓝按钮出现或仍是单按钮：兜底 A
        fresh = await _engine.ObserveAsync(ct);
        if (FrameAnalyzer.FindBlueDialogButton(fresh.Frame, out _)
            || FrameAnalyzer.FindBlueAdvanceButton(fresh.Frame, out _)
            || fresh.Screen == GameScreen.Dialog)
        {
            _engine.LogInternal("弹窗：上移后按 A 确认", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(900, ct);
        }
    }

    /// <summary>
    /// 检查并补齐消耗品。返回 true 表示「已确认配置物品都在使用中」或「购买流程已执行完」，
    /// 可以进入比赛；false 表示未能完成确认（面板切换失败 / 购买导航失败），调用方不得放行开赛。
    /// </summary>
    private async Task<bool> EnsureItemsAsync(CancellationToken ct)
    {
        // 切换到"使用的物品"面板
        if (!await SwitchToUsedItemsPanelAsync(ct))
        {
            _engine.LogInternal("未能切换到「使用的物品」面板，本轮物品检查未完成（禁止放行开赛）", LogLevel.Warn);
            return false;
        }

        // OCR 检查 3 个物品
        var obs = await _engine.ObserveAsync(ct);
        var missing = new List<string>();

        if (_engine.Config.MyLeague.Buy4xExp && !HasItem(obs, "经验值:4x", "4x"))
            missing.Add("4倍经验");
        if (_engine.Config.MyLeague.BuyExcellentCondition && !HasItem(obs, "状态:杰出", "杰出"))
            missing.Add("杰出状态");
        if (_engine.Config.MyLeague.BuyManagerBoost && !HasItem(obs, "主教练强化"))
            missing.Add("主教练强化");

        if (missing.Count == 0)
        {
            _engine.LogInternal("所有自动购买物品都在使用中", LogLevel.Success);
            return true;
        }

        _engine.LogInternal($"缺失物品：{string.Join("、", missing)}，准备进入兑换积分购买", LogLevel.Warn);

        // 导航到兑换积分（双词验证落地页）
        if (await NavigateToExchangeAsync(ct))
        {
            _engine.LogInternal("已到达兑换积分页面，开始购买缺失物品", LogLevel.Info);
            bool boughtAndBack = await BuyMissingItemsAsync(missing, ct);
            if (!boughtAndBack)
            {
                _engine.LogInternal("购买流程未确认完成或未返回主页，本轮禁止放行开赛", LogLevel.Error);
                return false;
            }
            return true;
        }

        _engine.LogInternal("导航到兑换积分失败，本轮物品检查未完成（禁止放行开赛）", LogLevel.Error);
        return false;
    }

    /// <summary>切换到"使用的物品"面板（RsFlick Right）。</summary>
    private async Task<bool> SwitchToUsedItemsPanelAsync(CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            var obs = await _engine.ObserveAsync(ct);
            if (obs.Ocr.JoinedText.Contains("使用的物品"))
            {
                _engine.LogInternal("已切换到「使用的物品」面板", LogLevel.Info);
                return true;
            }

            if (obs.Ocr.JoinedText.Contains("挑战"))
            {
                _engine.LogInternal("当前是「挑战」面板，RsFlick Right 切换", LogLevel.Info);
                await _engine.Pad.RsFlick(PadDir.Right);
                await Task.Delay(800, ct);
                continue;
            }

            _engine.LogInternal($"未识别面板类型：{obs.Ocr.JoinedText.Substring(0, Math.Min(50, obs.Ocr.JoinedText.Length))}", LogLevel.Debug);
            await _engine.Pad.RsFlick(PadDir.Right);
            await Task.Delay(800, ct);
        }

        return false;
    }

    /// <summary>检查 OCR 结果中是否包含指定物品关键词。</summary>
    private bool HasItem(EfootballBot.Core.Bot.Observation obs, params string[] keywords)
    {
        var text = obs.Ocr.JoinedText;
        foreach (var kw in keywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---- 我的联赛主页 → 兑换积分：审核驱动导航 ----
    // 主页卡片布局（用户实测）：
    //   行0：前往比赛(左) / i(右)
    //   行1：赛程表（视觉整行）
    //   行2：兑换积分(左) / 赛季纪录(右)
    //   行3：赛事信息(左) / 建议(右)
    // 实测「赛程表 Right」落点随机（有时直接到兑换积分，有时到赛季纪录，随机器/次而变），
    // 固定方向序列不可行。流程：锚定「前往比赛」→ Down 到赛程表 → 每步移动后用
    // FocusCardDetector 二次审核焦点卡——确认焦点在「兑换积分」才按 A 进入；
    // 审核到其他卡则按卡名智能走一步再审核；审核不到焦点才允许 A 试探兜底（进错页
    // B 退回）。只有落地页 OCR 双词确认才返回 true。

    private const int MenuMoveMs = 450;

    /// <summary>把 DPad 高光从主页任意卡片压回左上角「前往比赛」。</summary>
    private async Task AnchorToGoToMatchAsync(CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(MenuMoveMs, ct);
        }
        await _engine.Pad.Dpad(PadDir.Left);
        await Task.Delay(MenuMoveMs, ct);
    }

    /// <summary>主页判定：同时含两张主卡片词，且无模态弹窗遮罩（难度弹窗透出主页时不算）。</summary>
    private bool IsHomePage(EfootballBot.Core.Bot.Observation obs)
        => obs.Ocr.JoinedText.Contains("前往比赛")
           && obs.Ocr.JoinedText.Contains("赛程表")
           && !(FrameAnalyzer.HasModalOverlay(obs.Frame)
                && FrameAnalyzer.FindBlueDialogButton(obs.Frame, out _));

    /// <summary>折叠 OCR 实测异体/繁体误识，仅用于页面关键词的容错匹配。</summary>
    private static string FoldOcrVariants(string s)
        => s.Replace("賽", "赛").Replace("聯", "联").Replace("積", "积");

    /// <summary>兑换积分落地页：页面标题「我的联赛积分」+ 菜单卡「租借球员」双词（实测截图确认）。</summary>
    private static bool IsExchangeLandingPage(string joinedText)
    {
        string t = FoldOcrVariants(joinedText);
        return t.Contains("我的联赛积分") && t.Contains("租借球员");
    }

    /// <summary>从误入的任意页面（赛程表/比赛结算/确认弹窗等）退回我的联赛主页。</summary>
    private async Task<bool> RecoverToHomeAsync(CancellationToken ct, int maxActions = 8)
    {
        for (int i = 0; i < maxActions; i++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await _engine.ObserveAsync(ct);

            if (IsHomePage(o))
                return true;

            // 返回途中弹出的模态确认框（如"返回主页？"）：先移光标到确认按钮再点
            bool modal = FrameAnalyzer.HasModalOverlay(o.Frame)
                         && FrameAnalyzer.FindBlueDialogButton(o.Frame, out _);
            if (modal || o.Screen == GameScreen.Dialog)
            {
                await HandleDialogAsync(o, ct);
                continue;
            }

            // 结算/过场页：右下角蓝色推进按钮优先推进，否则按 B 退一层
            if (FrameAnalyzer.FindBlueAdvanceButton(o.Frame, out _))
            {
                await _engine.Pad.Confirm();
                await Task.Delay(1000, ct);
            }
            else
            {
                await _engine.Pad.Back();
                await Task.Delay(1000, ct);
            }
        }

        return false;
    }

    /// <summary>
    /// 导航到兑换积分落地页（审核驱动）。落点随机，不预设方向序列：
    /// 锚定前往比赛 → Down 到赛程表 → 循环「二次审核焦点卡 → 确认是兑换积分才按 A，
    /// 是其他卡则按卡名走一步再审核」。审核不到焦点才 A 试探兜底。
    /// 只有 OCR 确认到达「我的联赛积分」页才返回 true。
    /// </summary>
    private async Task<bool> NavigateToExchangeAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var obs = await _engine.ObserveAsync(ct);

            if (!IsHomePage(obs))
            {
                _engine.LogInternal($"去兑换积分前不在我的联赛主页（探针 {obs.Screen}），先退回主页", LogLevel.Warn);
                if (!await RecoverToHomeAsync(ct))
                {
                    _engine.LogInternal("无法回到我的联赛主页，本轮道具购买终止", LogLevel.Error);
                    return false;
                }
            }

            _engine.LogInternal($"导航兑换积分（第 {attempt}/3 轮）：锚定前往比赛 → 下到赛程表 → 逐步审核", LogLevel.Info);
            await AnchorToGoToMatchAsync(ct);
            await _engine.Pad.Dpad(PadDir.Down);
            await Task.Delay(MenuMoveMs, ct);

            for (int probe = 0; probe < 6; probe++)
            {
                ct.ThrowIfCancellationRequested();
                obs = await _engine.ObserveAsync(ct);

                // 保险：绝不允许流程被带进比赛（PreMatch 等），立即退出交给上层处理
                if (obs.Screen is GameScreen.PreMatch or GameScreen.InMatch
                    or GameScreen.HalfTime or GameScreen.FullTime)
                {
                    _engine.LogInternal($"导航误入比赛流程（探针 {obs.Screen}），终止", LogLevel.Error);
                    await _engine.Pad.Back();
                    await Task.Delay(1300, ct);
                    return false;
                }

                if (!IsHomePage(obs))
                {
                    if (!await RecoverToHomeAsync(ct, maxActions: 4))
                    {
                        _engine.LogInternal("导航途中无法退回主页，本轮道具购买终止", LogLevel.Error);
                        return false;
                    }
                    obs = await _engine.ObserveAsync(ct);
                }

                // 二次审核：焦点卡确认
                var card = FocusCardDetector.Detect(obs.Frame, obs.Ocr);
                string desc = card is null
                    ? "未识别到焦点卡"
                    : $"{card.Name}（{card.CX:F2},{card.CY:F2} conf {card.Confidence:F2}）";
                _engine.LogInternal($"焦点审核：{desc}", LogLevel.Info);

                if (card?.IsExchange == true)
                {
                    // 审核确认焦点在「兑换积分」→ 按 A → 落地页双词验证
                    _engine.LogInternal("审核确认焦点在「兑换积分」，按 A 进入", LogLevel.Success);
                    await _engine.Pad.Confirm();
                    await Task.Delay(2600, ct);
                    obs = await _engine.ObserveAsync(ct);
                    if (IsExchangeLandingPage(obs.Ocr.JoinedText))
                    {
                        _engine.LogInternal("已进入兑换积分页（我的联赛积分）", LogLevel.Success);
                        return true;
                    }
                    string badPreview = obs.Ocr.JoinedText.Length > 40
                        ? obs.Ocr.JoinedText[..40] : obs.Ocr.JoinedText;
                    _engine.LogInternal($"按 A 后非兑换积分页（OCR：{badPreview}），退回主页重审", LogLevel.Warn);
                    await _engine.Pad.Back();
                    await Task.Delay(1300, ct);
                    if (!await RecoverToHomeAsync(ct, maxActions: 4))
                    {
                        _engine.LogInternal("误入页面后无法退回主页，本轮道具购买终止", LogLevel.Error);
                        return false;
                    }
                    continue;
                }

                if (card is not null && card.Name is not ("unknown" or "eFootball联赛" or "我的联赛" or "活动"))
                {
                    // 审核到明确的我的联赛主页卡片：按卡名智能走一步，下一步再审核
                    var steps = StepTowardExchange(card);
                    _engine.LogInternal($"焦点在「{card.Name}」，走 {string.Join("·", steps)} 后再审", LogLevel.Info);
                    foreach (var dir in steps)
                    {
                        await _engine.Pad.Dpad(dir);
                        await Task.Delay(MenuMoveMs, ct);
                    }
                    continue;
                }

                if (card is not null)
                {
                    // unknown/非主页卡：按坐标粗调（行优先，再列）
                    PadDir dir = card.CY > 0.60 ? PadDir.Up
                        : card.CY < 0.30 ? PadDir.Down
                        : card.CX > 0.55 ? PadDir.Left
                        : PadDir.Right;
                    _engine.LogInternal($"焦点卡未命名（{card.CX:F2},{card.CY:F2}），按坐标 {(dir == PadDir.Up ? "上" : dir == PadDir.Down ? "下" : dir == PadDir.Left ? "左" : "右")} 调整", LogLevel.Warn);
                    await _engine.Pad.Dpad(dir);
                    await Task.Delay(MenuMoveMs, ct);
                    continue;
                }

                // 完全审核不到焦点卡：A 试探兜底（进错页 B 退回，不盲信）
                _engine.LogInternal("审核不到焦点卡，A 试探后按结果处理", LogLevel.Warn);
                await _engine.Pad.Confirm();
                await Task.Delay(2600, ct);
                obs = await _engine.ObserveAsync(ct);
                if (IsExchangeLandingPage(obs.Ocr.JoinedText))
                {
                    _engine.LogInternal("试探已进入兑换积分页（我的联赛积分）", LogLevel.Success);
                    return true;
                }
                if (obs.Screen is GameScreen.PreMatch or GameScreen.InMatch
                    or GameScreen.HalfTime or GameScreen.FullTime)
                {
                    _engine.LogInternal($"试探误入比赛流程（探针 {obs.Screen}），终止", LogLevel.Error);
                    await _engine.Pad.Back();
                    await Task.Delay(1300, ct);
                    return false;
                }
                if (!IsHomePage(obs))
                {
                    string preview = obs.Ocr.JoinedText.Length > 40
                        ? obs.Ocr.JoinedText[..40] : obs.Ocr.JoinedText;
                    _engine.LogInternal($"试探进入非目标页（OCR：{preview}），退回主页", LogLevel.Warn);
                    await _engine.Pad.Back();
                    await Task.Delay(1300, ct);
                    if (!await RecoverToHomeAsync(ct, maxActions: 4))
                    {
                        _engine.LogInternal("误入页面后无法退回主页，本轮道具购买终止", LogLevel.Error);
                        return false;
                    }
                }
                else
                {
                    _engine.LogInternal("试探后仍在主页（按键未生效），下轮重审", LogLevel.Warn);
                }
            }
        }

        _engine.LogInternal("3 轮审核导航均未进入兑换积分页", LogLevel.Warn);
        return false;
    }

    /// <summary>按当前焦点卡名给出向「兑换积分」走一步的方向（每步走完都会再审核）。</summary>
    private static IEnumerable<PadDir> StepTowardExchange(FocusCard card) => card.Name switch
    {
        // 行2右卡 → 左移一格到兑换积分
        "赛季纪录" => new[] { PadDir.Left },
        // 行1整行 → 右移到行2（落点随机，下一步审核决定是否再左移）
        "赛程表" => new[] { PadDir.Right },
        // 行0 → 下到赛程表再右
        "前往比赛" or "i" => new[] { PadDir.Down, PadDir.Right },
        // 行3 → 上移一层，下一步审核定位
        "赛事信息" or "建议" => new[] { PadDir.Up },
        // 兜底：按坐标行优先调整
        _ => new[] { card.CY > 0.60 ? PadDir.Up : PadDir.Down },
    };

    /// <summary>
    /// 在兑换积分落地页进入「我的联赛物品」商店并购买缺失物品，购买后验证已退回主页。
    /// 前置：当前画面已由 NavigateToExchangeAsync 双词确认是兑换积分落地页。
    /// 返回 false = 商店未进成 / 买完没回到主页（调用方不得放行开赛）。
    /// </summary>
    private async Task<bool> BuyMissingItemsAsync(List<string> missing, CancellationToken ct)
    {
        // 落地页默认焦点可能在左卡「租借球员」：Right 夹紧到右卡「我的联赛物品」后 A
        _engine.LogInternal("兑换积分页：进入「我的联赛物品」", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Right);
        await Task.Delay(MenuMoveMs, ct);
        await _engine.Pad.Confirm();
        await Task.Delay(2600, ct);

        // 硬验证：必须真的到了物品列表页（探针 Shop 实测 3.0，或列表正文词），
        // 否则绝不在错误页面上执行购买序列
        var shopObs = await _engine.ObserveAsync(ct);
        bool inShop = shopObs.Screen == GameScreen.Shop
                      || shopObs.Ocr.JoinedText.Contains("选择要兑换")
                      || shopObs.Ocr.JoinedText.Contains("持有物品");
        if (!inShop)
        {
            _engine.LogInternal($"进入我的联赛物品失败（探针 {shopObs.Screen}），终止购买并退回主页", LogLevel.Error);
            await RecoverToHomeAsync(ct);
            return false;
        }
        _engine.LogInternal("已进入我的联赛物品列表页，开始购买", LogLevel.Success);

        var itemDownCounts = new Dictionary<string, int>
        {
            ["杰出状态"] = 2,
            ["4倍经验"] = 5,
            ["主教练强化"] = 12,
        };

        foreach (var item in missing)
        {
            if (!itemDownCounts.TryGetValue(item, out int downCount))
            {
                _engine.LogInternal($"未知物品「{item}」，跳过", LogLevel.Warn);
                continue;
            }

            _engine.LogInternal($"购买「{item}」，向下移动 {downCount} 次", LogLevel.Info);

            for (int i = 0; i < 15; i++) await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(300, ct);

            for (int i = 0; i < downCount; i++)
            {
                await _engine.Pad.Dpad(PadDir.Down);
                await Task.Delay(300, ct);
            }

            _engine.LogInternal($"按 A 购买「{item}」", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(1500, ct);

            _engine.LogInternal("处理购买确认弹窗", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(300, ct);
            await _engine.Pad.Confirm();
            await Task.Delay(2000, ct);

            _engine.LogInternal("处理物品使用中弹窗", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(1500, ct);

            // 兜底：若「物品使用中」弹窗仍在（焦点/时序偏差），按通用弹窗流程再处理一次
            var afterBuy = await _engine.ObserveAsync(ct);
            if (FrameAnalyzer.HasModalOverlay(afterBuy.Frame)
                && FrameAnalyzer.FindBlueDialogButton(afterBuy.Frame, out _))
            {
                _engine.LogInternal("物品使用中弹窗仍在，补一次确认", LogLevel.Warn);
                await HandleDialogAsync(afterBuy, ct);
            }

            _engine.LogInternal($"「{item}」购买完成", LogLevel.Success);
        }

        // 返回主页：物品列表 B → 落地页 B → 主页。逐步验证，最多补按 5 次
        _engine.LogInternal("全部购买完毕，返回我的联赛主页", LogLevel.Info);
        for (int i = 0; i < 5; i++)
        {
            await _engine.Pad.Back();
            await Task.Delay(1300, ct);
            var o = await _engine.ObserveAsync(ct);
            if (IsHomePage(o))
            {
                _engine.LogInternal("已返回我的联赛主页", LogLevel.Success);
                return true;
            }
            bool modal = FrameAnalyzer.HasModalOverlay(o.Frame)
                         && FrameAnalyzer.FindBlueDialogButton(o.Frame, out _);
            if (modal || o.Screen == GameScreen.Dialog)
                await HandleDialogAsync(o, ct);
        }

        _engine.LogInternal("购买后连续返回仍未确认到达主页，本轮不放行开赛", LogLevel.Warn);
        return false;
    }
}
