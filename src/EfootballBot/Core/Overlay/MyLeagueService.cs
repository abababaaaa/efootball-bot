using EfootballBot.Core.Bot;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Input;
using EfootballBot.Core.Match;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Overlay;

/// <summary>我的联赛场景状态机：观察当前画面 → 判断状态 → 执行对应操作。</summary>
public sealed class MyLeagueStateMachine
{
    private readonly BotEngine _engine;
    private readonly FocusGraph _focusGraph;
    private readonly MatchFlowService _matchFlow;

    public MyLeagueStateMachine(BotEngine engine)
    {
        _engine = engine;
        _focusGraph = MyLeagueHomeGraph.Build();
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
                _wasInMatch = true; // 标记正在/曾在比赛流程，回到主页后需重新检查消耗品
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
            _engine.LogInternal($"检测到 AI 代打图标（{iconX:F2},{iconY:F2}），判定比赛中", LogLevel.Debug);
            return State.InMatch;
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

        // 页面判断
        if (obs.Screen == GameScreen.MyLeagueHome)
        {
            // 回到主页优先清弹窗：主页上覆盖模态弹窗（如物品已过期）时探针可能仍判主页，
            // 用弹窗中部蓝色确认按钮作为独立视觉信号
            if (FrameAnalyzer.FindBlueDialogButton(obs.Frame, out _))
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

        // 兑换积分页面（OCR 关键词）
        if (text.Contains("兑换积分") || text.Contains("我的联赛积分"))
            return State.Exchange;

        return State.Unknown;
    }

    /// <summary>我的联赛主页：检查物品、进入比赛。</summary>
    private async Task HandleMyLeagueHomeAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        // 一场比赛结束回到主页：消耗品（4x经验/杰出状态/教练强化）每场都要重新检查
        if (_wasInMatch)
        {
            _wasInMatch = false;
            _itemsChecked = false;
            _engine.LogInternal("检测到一场比赛结束，重新检查消耗品", LogLevel.Info);
        }

        // 1. 检查物品（只检查一次）
        if (!_itemsChecked)
        {
            _engine.LogInternal("我的联赛主页：检查物品", LogLevel.Info);
            await EnsureItemsAsync(ct);
            _itemsChecked = true;
            return;
        }

        // 2. 进入比赛：先确保高光在「前往比赛」上
        _engine.LogInternal("我的联赛主页：确保高光在「前往比赛」", LogLevel.Info);

        var (cardName, reliable) = await DetectFocusCardAsync(obs, ct);
        if (cardName != "前往比赛")
        {
            _engine.LogInternal($"当前焦点在「{cardName}」，移动到「前往比赛」", LogLevel.Info);

            // 硬编码路径：从任意卡到前往比赛
            switch (cardName)
            {
                case "i":
                    // i → Left → 前往比赛
                    await _engine.Pad.Dpad(PadDir.Left);
                    await Task.Delay(300, ct);
                    break;
                case "赛程表":
                    // 赛程表 → Up → 前往比赛
                    await _engine.Pad.Dpad(PadDir.Up);
                    await Task.Delay(300, ct);
                    break;
                case "兑换积分":
                    // 兑换积分 → Left → 前往比赛
                    await _engine.Pad.Dpad(PadDir.Left);
                    await Task.Delay(300, ct);
                    break;
                case "赛季纪录":
                    // 赛季纪录 → Up → 兑换积分 → Left → 前往比赛
                    await _engine.Pad.Dpad(PadDir.Up);
                    await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Left);
                    await Task.Delay(300, ct);
                    break;
                case "赛事信息":
                    // 赛事信息 → Up → 赛程表 → Up → 前往比赛
                    await _engine.Pad.Dpad(PadDir.Up);
                    await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up);
                    await Task.Delay(300, ct);
                    break;
                case "建议":
                    // 建议 → Up → 赛季纪录 → Up → 兑换积分 → Left → 前往比赛
                    await _engine.Pad.Dpad(PadDir.Up);
                    await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up);
                    await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Left);
                    await Task.Delay(300, ct);
                    break;
                default:
                    _engine.LogInternal($"未知卡「{cardName}」，尝试默认路径", LogLevel.Warn);
                    // 默认：Up → Up → Up → Left → Left → Left（压到左上角）
                    for (int i = 0; i < 3; i++) await _engine.Pad.Dpad(PadDir.Up);
                    for (int i = 0; i < 3; i++) await _engine.Pad.Dpad(PadDir.Left);
                    await Task.Delay(300, ct);
                    break;
            }

            // 再次检测确认
            obs = await _engine.ObserveAsync(ct);
            (cardName, _) = await DetectFocusCardAsync(obs, ct);
            if (cardName != "前往比赛")
            {
                _engine.LogInternal($"移动后焦点仍在「{cardName}」，无法进入比赛", LogLevel.Error);
                return;
            }
        }

        _engine.LogInternal("高光已在「前往比赛」，按 A 进入", LogLevel.Info);
        await _engine.Pad.Confirm();
        await Task.Delay(2500, ct);
    }

    private bool _itemsChecked = false;
    private bool _wasInMatch = false;

    /// <summary>赛程表：选择比赛。</summary>
    private async Task HandleScheduleAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
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

    /// <summary>检查我的联赛主页"使用的物品"面板。</summary>
    private async Task EnsureItemsAsync(CancellationToken ct)
    {
        // 切换到"使用的物品"面板
        if (!await SwitchToUsedItemsPanelAsync(ct))
        {
            _engine.LogInternal("未能切换到「使用的物品」面板，跳过物品检查", LogLevel.Warn);
            return;
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
            return;
        }

        _engine.LogInternal($"缺失物品：{string.Join("、", missing)}，准备进入兑换积分购买", LogLevel.Warn);

        // 导航到兑换积分
        if (await NavigateToExchangeAsync(ct))
        {
            _engine.LogInternal("已到达兑换积分页面，开始购买缺失物品", LogLevel.Info);
            await BuyMissingItemsAsync(missing, ct);
        }
        else
        {
            _engine.LogInternal("导航到兑换积分失败", LogLevel.Error);
        }
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

    /// <summary>检测当前焦点卡。</summary>
    private async Task<(string Name, bool Reliable)> DetectFocusCardAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        var card = FocusCardDetector.Detect(obs.Frame, obs.Ocr);

        if (card is not null && card.CX < 0.10 && card.CY < 0.10)
        {
            _engine.LogInternal($"检测到导航栏白色块（{card.CX:F2},{card.CY:F2}），忽略", LogLevel.Debug);
            card = null;
        }

        if (card is null)
        {
            _engine.LogInternal("未检测到焦点卡，默认当前高光在「赛程表」", LogLevel.Warn);
            return ("赛程表", false);
        }

        _engine.LogInternal($"焦点检测：{card.Name}（{card.CX:F2},{card.CY:F2} 置信度 {card.Confidence:F2}）", LogLevel.Info);

        if (card.Name == "unknown")
        {
            var nearest = _focusGraph.Nearest(card.CX, card.CY, maxDy: 0.20, maxDx: 0.25);
            if (nearest is not null)
            {
                _engine.LogInternal($"unknown 卡坐标推断为「{nearest.Id}」", LogLevel.Info);
                return (nearest.Id, true);
            }
        }

        if (card.Name == "eFootball联赛")
        {
            var nearest = _focusGraph.Nearest(card.CX, card.CY, maxDy: 0.20, maxDx: 0.25);
            if (nearest is not null)
            {
                _engine.LogInternal($"eFootball联赛 坐标推断为「{nearest.Id}」", LogLevel.Info);
                return (nearest.Id, true);
            }
            _engine.LogInternal("eFootball联赛 坐标推断失败，默认「兑换积分」", LogLevel.Warn);
            return ("兑换积分", false);
        }

        if (card.Name == "i" && card.Confidence < 0.03)
        {
            _engine.LogInternal("i 卡置信度过低，可能误检测", LogLevel.Debug);
            return ("i", false);
        }

        return (card.Name, true);
    }

    /// <summary>用 FocusGraph 导航到兑换积分。</summary>
    private async Task<bool> NavigateToExchangeAsync(CancellationToken ct)
    {
        for (int step = 0; step < 8; step++)
        {
            ct.ThrowIfCancellationRequested();
            var obs = await _engine.ObserveAsync(ct);
            if (obs.Screen != GameScreen.MyLeagueHome)
            {
                _engine.LogInternal($"当前不在我的联赛主页（{obs.Screen}），无法焦点导航", LogLevel.Warn);
                return false;
            }

            var (cardName, reliable) = await DetectFocusCardAsync(obs, ct);

            if (cardName == "兑换积分")
            {
                _engine.LogInternal("焦点已在「兑换积分」，按 A 进入", LogLevel.Info);
                await _engine.Pad.Confirm();
                await Task.Delay(2500, ct);
                return true;
            }

            switch (cardName)
            {
                case "前往比赛":
                    _engine.LogInternal("硬编码路径：前往比赛 → Down → Right → Up（大概率）", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Down); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Right); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                case "i":
                    _engine.LogInternal("硬编码路径：i → Down → Right → Up（与前往比赛同行）", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Down); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Right); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                case "赛程表":
                    _engine.LogInternal("硬编码路径：赛程表 → Right → 判断 → Up（如需要）", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Right); await Task.Delay(300, ct);
                    var obsAfterRight = await _engine.ObserveAsync(ct);
                    var (afterRightName, _) = await DetectFocusCardAsync(obsAfterRight, ct);
                    if (afterRightName == "兑换积分")
                    {
                        _engine.LogInternal("Right 后已到兑换积分", LogLevel.Info);
                        break;
                    }
                    _engine.LogInternal("Right 后未到兑换积分，再按 Up", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                case "赛季纪录":
                    _engine.LogInternal("硬编码路径：赛季纪录 → Up → 兑换积分", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                case "赛事信息":
                    _engine.LogInternal("硬编码路径：赛事信息 → Right → Up → Up → 兑换积分", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Right); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                case "建议":
                    _engine.LogInternal("硬编码路径：建议 → Up → Up → 兑换积分", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                default:
                    _engine.LogInternal($"未知卡「{cardName}」，尝试通用 FocusGraph 寻路", LogLevel.Warn);
                    var slot = _focusGraph.SlotByName(cardName);
                    if (slot is null)
                    {
                        _engine.LogInternal($"未知焦点卡「{cardName}」，无法寻路", LogLevel.Warn);
                        return false;
                    }
                    var path = _focusGraph.FindPath(slot.Id, "兑换积分");
                    if (path is null || path.Count == 0)
                    {
                        _engine.LogInternal($"从「{slot.Id}」到「兑换积分」无路径", LogLevel.Warn);
                        return false;
                    }
                    var (dir, to) = path[0];
                    _engine.LogInternal($"焦点导航：{slot.Id} → {dir} → {to}", LogLevel.Info);
                    await _engine.Pad.Dpad(dir);
                    await Task.Delay(400, ct);
                    break;
            }

            var obsAfterMove = await _engine.ObserveAsync(ct);
            var (afterMoveName, _) = await DetectFocusCardAsync(obsAfterMove, ct);
            if (afterMoveName == "兑换积分")
            {
                _engine.LogInternal("硬编码路径执行后到达兑换积分，按 A 进入", LogLevel.Info);
                await _engine.Pad.Confirm();
                await Task.Delay(2500, ct);
                return true;
            }

            _engine.LogInternal($"硬编码路径执行后焦点在「{afterMoveName}」，尝试按 A 确认", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(2500, ct);
            var obsAfterA = await _engine.ObserveAsync(ct);
            if (obsAfterA.Screen != GameScreen.MyLeagueHome)
            {
                _engine.LogInternal("按 A 后页面已变化，假设已进入兑换积分", LogLevel.Info);
                return true;
            }
            _engine.LogInternal("按 A 后仍在我的联赛主页，继续循环", LogLevel.Info);
        }

        _engine.LogInternal("焦点导航 8 步未到达兑换积分", LogLevel.Warn);
        return false;
    }

    /// <summary>购买缺失物品。</summary>
    private async Task BuyMissingItemsAsync(List<string> missing, CancellationToken ct)
    {
        _engine.LogInternal("进入「我的联赛物品」", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Right);
        await Task.Delay(400, ct);
        await _engine.Pad.Confirm();
        await Task.Delay(2500, ct);

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

            _engine.LogInternal($"「{item}」购买完成", LogLevel.Success);
        }

        _engine.LogInternal("全部购买完毕，返回我的联赛首页", LogLevel.Info);
        await _engine.Pad.Back();
        await Task.Delay(1500, ct);
        await _engine.Pad.Back();
        await Task.Delay(2000, ct);
    }
}
