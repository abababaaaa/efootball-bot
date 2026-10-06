using EfootballBot.Core.Bot;
using EfootballBot.Core.Graph;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Overlay;

/// <summary>我的联赛场景服务：弹窗处理、物品检查、兑换积分导航。</summary>
public sealed class MyLeagueService
{
    private readonly BotEngine _engine;
    private readonly FocusGraph _focusGraph;

    public MyLeagueService(BotEngine engine)
    {
        _engine = engine;
        _focusGraph = MyLeagueHomeGraph.Build();
    }

    /// <summary>
    /// 检查我的联赛主页"使用的物品"面板，确认 3 个自动购买物品是否都在使用中。
    /// 如有缺失，导航到兑换积分进入购买。
    /// </summary>
    public async Task EnsureItemsAsync(CancellationToken ct)
    {
        // 1. 切换到"使用的物品"面板
        if (!await SwitchToUsedItemsPanelAsync(ct))
        {
            _engine.LogInternal("未能切换到「使用的物品」面板，跳过物品检查", LogLevel.Warn);
            return;
        }

        // 2. OCR 检查 3 个物品
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

        // 3. 导航到兑换积分
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

            // 既不是挑战也不是使用的物品，可能已经在其他面板
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

    /// <summary>
    /// 检测当前焦点卡，排除导航栏和误识别。
    /// 返回 (卡名, 置信度, 是否可靠)。
    /// </summary>
    private async Task<(string Name, bool Reliable)> DetectFocusCardAsync(EfootballBot.Core.Bot.Observation obs, CancellationToken ct)
    {
        var card = FocusCardDetector.Detect(obs.Frame, obs.Ocr);

        // 排除导航栏白色块（左上角 Home 图标区域，非卡片）
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

        // unknown 卡：用坐标匹配最近的已知槽位
        if (card.Name == "unknown")
        {
            var nearest = _focusGraph.Nearest(card.CX, card.CY, maxDy: 0.20, maxDx: 0.25);
            if (nearest is not null)
            {
                _engine.LogInternal($"unknown 卡坐标推断为「{nearest.Id}」", LogLevel.Info);
                return (nearest.Id, true);
            }
        }

        // eFootball联赛 是 MatchHub 的卡，但 FocusCardDetector 可能误识别我的联赛主页的卡
        // 用坐标推断：如果检测到 eFootball联赛，且坐标在右侧（x > 0.35），则是我的联赛主页的卡
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

        // i 的高光太小，OCR 可能读不到，但 FocusCardDetector 应该能检测到白色块
        // 如果检测到的是 i，置信度通常较低，但坐标应该正确
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

            // 硬编码路径（真机验证可靠）
            switch (cardName)
            {
                case "前往比赛":
                    // 真机验证：大概率 下右上，小概率 下右
                    _engine.LogInternal("硬编码路径：前往比赛 → Down → Right → Up（大概率）", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Down); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Right); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                case "i":
                    // i 和前往比赛同行，路径相同
                    _engine.LogInternal("硬编码路径：i → Down → Right → Up（与前往比赛同行）", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Down); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Right); await Task.Delay(300, ct);
                    await _engine.Pad.Dpad(PadDir.Up); await Task.Delay(300, ct);
                    break;
                case "赛程表":
                    // 赛程表 Right 可能到兑换积分或赛季纪录，需要结尾判断
                    _engine.LogInternal("硬编码路径：赛程表 → Right → 判断 → Up（如需要）", LogLevel.Info);
                    await _engine.Pad.Dpad(PadDir.Right); await Task.Delay(300, ct);
                    // 检测是否已到兑换积分
                    var obsAfterRight = await _engine.ObserveAsync(ct);
                    var (afterRightName, _) = await DetectFocusCardAsync(obsAfterRight, ct);
                    if (afterRightName == "兑换积分")
                    {
                        _engine.LogInternal("Right 后已到兑换积分", LogLevel.Info);
                        break;
                    }
                    // 否则再按 Up
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

            // 执行完硬编码路径后，检测是否到达兑换积分
            var obsAfterMove = await _engine.ObserveAsync(ct);
            var (afterMoveName, _) = await DetectFocusCardAsync(obsAfterMove, ct);
            if (afterMoveName == "兑换积分")
            {
                _engine.LogInternal("硬编码路径执行后到达兑换积分，按 A 进入", LogLevel.Info);
                await _engine.Pad.Confirm();
                await Task.Delay(2500, ct);
                return true;
            }

            // 路径执行后未检测到兑换积分，但可能检测失败（白边框卡 OCR 漏读）
            // 尝试直接按 A，如果页面变化说明已到兑换积分
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
        // 从兑换积分页面进入"我的联赛物品"（向右 + A）
        _engine.LogInternal("进入「我的联赛物品」", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Right);
        await Task.Delay(400, ct);
        await _engine.Pad.Confirm();
        await Task.Delay(2500, ct);

        // 物品列表顺序（从上到下）：
        // 1.状态:普通+ 2.状态:好+ 3.状态:杰出 4.经验值:2x 5.经验值:3x 6.经验值:4x
        // 7.控球:99 8.头球:99 9.加速:99 10.FW球员能力值+ 11.MF球员能力值+ 12.DF球员能力值+ 13.主教练强化
        var itemDownCounts = new Dictionary<string, int>
        {
            ["杰出状态"] = 2,      // 第3项，从顶部Down 2次
            ["4倍经验"] = 5,       // 第6项，从顶部Down 5次
            ["主教练强化"] = 12,   // 第13项，从顶部Down 12次
        };

        foreach (var item in missing)
        {
            if (!itemDownCounts.TryGetValue(item, out int downCount))
            {
                _engine.LogInternal($"未知物品「{item}」，跳过", LogLevel.Warn);
                continue;
            }

            _engine.LogInternal($"购买「{item}」，向下移动 {downCount} 次", LogLevel.Info);

            // 从列表顶部开始，先回到顶部（连按Up确保）
            for (int i = 0; i < 15; i++) await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(300, ct);

            // 向下移动到目标物品
            for (int i = 0; i < downCount; i++)
            {
                await _engine.Pad.Dpad(PadDir.Down);
                await Task.Delay(300, ct);
            }

            // 按 A 购买
            _engine.LogInternal($"按 A 购买「{item}」", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(1500, ct);

            // 处理购买确认弹窗（上移 + 点击）
            _engine.LogInternal("处理购买确认弹窗", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(300, ct);
            await _engine.Pad.Confirm();
            await Task.Delay(2000, ct);

            // 处理"物品使用中"成功弹窗（只有 OK 按钮）
            _engine.LogInternal("处理物品使用中弹窗", LogLevel.Info);
            await _engine.Pad.Confirm();
            await Task.Delay(1500, ct);

            _engine.LogInternal($"「{item}」购买完成", LogLevel.Success);
        }

        // 全部购买完毕，连按两次返回我的联赛首页
        _engine.LogInternal("全部购买完毕，返回我的联赛首页", LogLevel.Info);
        await _engine.Pad.Back();
        await Task.Delay(1500, ct);
        await _engine.Pad.Back();
        await Task.Delay(2000, ct);
    }

    /// <summary>进入比赛：前往比赛 → 赛程表 → 选择比赛 → 难度选择 → 进入比赛。</summary>
    public async Task StartMatchAsync(CancellationToken ct)
    {
        // 1. 从我的联赛主页进入赛程表
        _engine.LogInternal("进入赛程表", LogLevel.Info);
        await NavigateToScheduleAsync(ct);

        // 2. 选择比赛（关键比赛或普通比赛）
        _engine.LogInternal("选择比赛", LogLevel.Info);
        await SelectMatchAsync(ct);

        // 3. 处理难度选择弹窗
        _engine.LogInternal("处理难度选择", LogLevel.Info);
        await HandleDifficultySelectAsync(ct);
    }

    /// <summary>从我的联赛主页导航到赛程表。</summary>
    private async Task NavigateToScheduleAsync(CancellationToken ct)
    {
        // 当前应该在 MyLeagueHome，焦点在「前往比赛」
        // 直接按 A 进入赛程表
        await _engine.Pad.Confirm();
        await Task.Delay(2500, ct);
        _engine.LogInternal("已进入赛程表", LogLevel.Info);
    }

    /// <summary>在赛程表选择比赛。</summary>
    private async Task SelectMatchAsync(CancellationToken ct)
    {
        // 赛程表显示即将到来的比赛列表
        // 关键比赛有钥匙图标，普通比赛没有
        // 优先选择关键比赛（如果有），否则选择普通比赛
        // 当前实现：直接按 A 选择第一场比赛（最近的比赛）
        await _engine.Pad.Confirm();
        await Task.Delay(2000, ct);
        _engine.LogInternal("已选择比赛", LogLevel.Info);
    }

    /// <summary>处理难度选择弹窗。</summary>
    private async Task HandleDifficultySelectAsync(CancellationToken ct)
    {
        var targetDifficulty = _engine.Config.MyLeague.Difficulty; // 用户配置的难度，如 "传奇"

        // 等待弹窗出现
        await Task.Delay(1000, ct);
        var obs = await _engine.ObserveAsync(ct);

        // 检测是否为确认弹窗（上下两个按钮，无难度列表）
        if (obs.Ocr.JoinedText.Contains("确认") && !obs.Ocr.JoinedText.Contains("选择赛事等级"))
        {
            _engine.LogInternal("检测到确认弹窗（关键比赛），上移高光并点击", LogLevel.Info);
            await _engine.Pad.Dpad(PadDir.Up);
            await Task.Delay(300, ct);
            await _engine.Pad.Confirm();
            await Task.Delay(2000, ct);
            return;
        }

        // 难度选择弹窗：检测蓝色钩子位置
        // 蓝色钩子在选中的难度左侧
        // 如果传奇有蓝色钩子，直接点击确认
        // 否则移动焦点到传奇，再移动到最下层，向右平移点击确认

        // 检测当前选中的难度（蓝色钩子位置）
        // 由于 OCR 无法直接检测蓝色钩子，我们假设：
        // 1. 如果 OCR 读到"传奇"且弹窗中有蓝色，则已选中
        // 2. 否则需要移动

        // 简化实现：直接移动到传奇位置，然后按 Down 到最下层，Right 到确认，A 点击
        _engine.LogInternal($"目标难度：{targetDifficulty}，移动焦点", LogLevel.Info);

        // 难度列表顺序：新手(0)、业余(1)、标准(2)、职业(3)、顶尖球员(4)、超级球星(5)、传奇(6)
        // 关键比赛可能只有：超级球星(0)、传奇(1)
        var difficultyIndex = targetDifficulty switch
        {
            "新手" => 0,
            "业余" => 1,
            "标准" => 2,
            "职业" => 3,
            "顶尖球员" => 4,
            "超级球星" => 5,
            "传奇" => 6,
            _ => 6, // 默认传奇
        };

        // 先回到列表顶部（连按 Up）
        for (int i = 0; i < 10; i++) await _engine.Pad.Dpad(PadDir.Up);
        await Task.Delay(300, ct);

        // 向下移动到目标难度
        for (int i = 0; i < difficultyIndex; i++)
        {
            await _engine.Pad.Dpad(PadDir.Down);
            await Task.Delay(300, ct);
        }

        // 移动到最下层（确认按钮）
        _engine.LogInternal("移动到最下层确认按钮", LogLevel.Info);
        for (int i = 0; i < 10; i++) await _engine.Pad.Dpad(PadDir.Down);
        await Task.Delay(300, ct);

        // 向右平移到确认按钮
        _engine.LogInternal("向右平移到确认按钮", LogLevel.Info);
        await _engine.Pad.Dpad(PadDir.Right);
        await Task.Delay(300, ct);

        // 点击确认
        _engine.LogInternal("点击确认进入比赛", LogLevel.Info);
        await _engine.Pad.Confirm();
        await Task.Delay(3000, ct);
    }
}
