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
            _engine.LogInternal("已到达兑换积分页面，待实现购买逻辑", LogLevel.Info);
            // TODO: 购买逻辑（M2）
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
}
