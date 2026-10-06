using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 我的联赛刷级：进入联赛 →（可选）买道具 → 赛程表识别关键/普通比赛 →
/// AI 挂机完赛（普通比赛可自动弃权）→ 结算连按 → 循环。
/// </summary>
public sealed class MyLeagueScenario : ScenarioBase
{
    public MyLeagueScenario(BotEngine engine, AppConfig cfg, CancellationToken ct) : base(engine, cfg, ct) { }
    public override string DisplayName => "国际服 · 我的联赛刷级";

    private MyLeagueConfig M => Cfg.MyLeague;

    public override async Task RunAsync()
    {
        Log("智能判定当前画面，从所在步骤继续…", LogLevel.Info);

        int guard = 0;
        while (!Ct.IsCancellationRequested)
        {
            Ct.ThrowIfCancellationRequested();

            if (TargetReached(E.Stats.MatchesDone, M.TargetMatches))
            {
                Log($"已完成目标场次（{M.TargetMatches}），停止。", LogLevel.Success);
                return;
            }

            var o = await E.ObserveAsync(Ct);
            o = await E.Overlay.PassThroughAsync(o, Ct);

            switch (o.Screen)
            {
                case GameScreen.Home:
                case GameScreen.MatchHub:
                    // 目标编排式导航：声明目标页，由 PageGraph BFS 寻路执行按键序列
                    await E.GoToAsync(nameof(GameScreen.MyLeagueHome), null, Ct);
                    break;

                case GameScreen.MyLeagueHome:
                    // 顺序严格：① 清扫弹窗（关键比赛=Up→A / 其他=A） ② 购买经验卡 ③ A 前往比赛
                    for (int popup = 0; popup < 8; popup++)
                    {
                        Ct.ThrowIfCancellationRequested();
                        var p = await E.ObserveAsync(Ct);
                        p = await E.Overlay.PassThroughAsync(p, Ct);
                        bool isKeyMatch = p.Ocr.Contains("关键比赛");
                        // 「OK」必须独立成词（精确匹配），否则赛程卡上的 "Fukuoka" 会子串命中导致误判
                        // OCR 可能把字母 O 识别为数字 0，因此同时接受 0K
                        bool hasOk = p.Ocr.Words.Any(w =>
                        {
                            string t = OcrText.Norm(w.Text);
                            return t.Equals("OK", StringComparison.OrdinalIgnoreCase)
                                   || t.Equals("okay", StringComparison.OrdinalIgnoreCase)
                                   || t.Equals("0K", StringComparison.OrdinalIgnoreCase);
                        });
                        bool hasExpired = p.Ocr.Contains("过期") || p.Ocr.ContainsJoined("过期")
                                          || p.Ocr.Contains("获取新物品");
                        // 赛后积分弹窗「收到我的联赛积分」没有确定/OK 关键词，必须显式列入。
                        // ContainsJoined 跨词拼接匹配，对抗 OCR 把长标题拆成碎词（如「收」「到」「我」「的」）。
                        bool hasRewardPopup = p.Ocr.ContainsJoined("收到我的联赛积分")
                                              || p.Ocr.ContainsJoined("获得的活动积分")
                                              || p.Ocr.ContainsJoined("物品已送到收件箱")
                                              || p.Ocr.ContainsJoined("送到收件箱")
                                              // 紫色弹窗上的「详情」+ 大数字 + OK 是辅助特征
                                              || (p.Ocr.Contains("详情") && hasOk);
                        bool hasPopup = isKeyMatch || hasOk || hasExpired || hasRewardPopup
                                        || p.Ocr.Contains("实时更新")
                                        || p.Ocr.Contains("下一步")
                                        || p.Ocr.Contains("确定") || p.Ocr.Contains("物品");
                        if (!hasPopup) break;
                        Log($"联赛主页弹窗（第{popup + 1}轮）：{(isKeyMatch ? "关键比赛" : hasRewardPopup ? "赛后积分" : "其他")}", LogLevel.Info);
                        if (isKeyMatch)
                        {
                            await E.Pad.Dpad(PadDir.Up);
                            await E.Pad.Wait(400, Ct);
                            await E.Pad.Confirm();
                        }
                        else if (hasExpired)
                        {
                            // 「物品已过期」弹窗只有一个 OK 且默认聚焦，立刻按 A
                            Log("物品已过期弹窗，立即确认", LogLevel.Info);
                            if (!await E.PressButtonAsync(new[] { "ok" }, Ct, exactMatch: true))
                            {
                                await E.Pad.Confirm();
                            }
                        }
                        else if (hasRewardPopup)
                        {
                            // 「收到我的联赛积分」等赛后积分弹窗：底部白色 OK 按钮默认聚焦
                            if (!await E.PressButtonAsync(new[] { "ok" }, Ct, exactMatch: true))
                            {
                                await E.Pad.Confirm();
                            }
                        }
                        else if (hasOk)
                        {
                            // OK 按钮可能不在默认焦点上，走 PressButtonAsync 精确匹配（避免误点 Fukuoka）
                            if (!await E.PressButtonAsync(new[] { "ok" }, Ct, exactMatch: true))
                            {
                                await E.Pad.Confirm();
                            }
                        }
                        else if (!await E.PressButtonAsync(new[] { "确定", "确认", "下一步" }, Ct))
                        {
                            await E.Pad.Confirm();
                        }
                        await E.Pad.Wait(1200, Ct);
                    }

                    await BuyItemsIfNeededAsync();
                    if (!await EnterNextMatchAsync()) return;
                    break;

                case GameScreen.Schedule:
                    await HandleScheduleAsync(o);
                    break;

                case GameScreen.PreMatch:
                case GameScreen.DifficultySelect:
                case GameScreen.KeyMatchBadge:
                    Log("检测到赛前画面，直接继续本场", LogLevel.Info);
                    await FinishOneAsync(key: true, forfeit: false);
                    break;

                case GameScreen.InMatch:
                case GameScreen.HalfTime:
                    Log("检测到已在比赛中，挂机等待结束…", LogLevel.Info);
                    await FinishOneAsync(key: true, forfeit: false);
                    break;

                case GameScreen.FullTime:
                case GameScreen.Result:
                case GameScreen.Rewards:
                    await PostMatchSequenceAsync();
                    break;

                case GameScreen.Disconnected:
                    await E.HandlePopupsAsync(o, Ct);
                    break;

                case GameScreen.Shop:
                case GameScreen.ShopConfirm:
                    Log("检测到已在积分商店内，继续购买流程…", LogLevel.Info);
                    await ContinuePurchaseFromShopAsync();
                    break;

                case GameScreen.Loading:
                    await E.Pad.Wait(2000, Ct);
                    break;

                default:
                    if (o.Ocr.Contains("租借") || o.Ocr.Contains("获得各种物品")
                        || o.Ocr.Contains("联赛物品") || o.Ocr.Contains("选择要兑换"))
                    {
                        Log("识别为积分商店（兜底），继续购买流程…", LogLevel.Info);
                        await ContinuePurchaseFromShopAsync();
                        break;
                    }
                    await E.RecoverAsync(o, Ct);
                    if (++guard > 5)
                    {
                        guard = 0;
                        await E.GoToAsync(nameof(GameScreen.MyLeagueHome), null, Ct);
                    }
                    break;
            }
        }
    }

    private int _enterMatchFailures;

    /// <summary>
    /// 焦点归位到左上「前往比赛」卡：Left×2 → Up×4（各 250ms，到边界后多按无效，幂等）。
    /// 商店 B×3 返回主页后焦点可能停在兑换积分/建议/赛事信息等任意位置，
    /// 必须收拢到左上角 go 卡后再出发，否则盲按 A 会重复进商店。
    /// </summary>
    private async Task ResetFocusToGoToMatchAsync()
    {
        for (int i = 0; i < 2; i++) { await E.Pad.Dpad(PadDir.Left); await E.Pad.Wait(250, Ct); }
        for (int i = 0; i < 4; i++) { await E.Pad.Dpad(PadDir.Up); await E.Pad.Wait(250, Ct); }
    }

    /// <summary>
    /// 前往下一场比赛：先归位焦点再按 A；按 A 后验证落点，
    /// 误入商店则 B 退出并计数，连续 2 次归位失效直接停止（返回 false）。
    /// </summary>
    private async Task<bool> EnterNextMatchAsync()
    {
        Log("我的联赛主页：前往下一场比赛", LogLevel.Info);
        await ResetFocusToGoToMatchAsync();
        await E.Pad.Confirm();
        await E.Pad.Wait(3000, Ct);

        var o = await E.ObserveAsync(Ct);
        bool inShop = IsExchangeShop(o);
        if (!inShop)
        {
            _enterMatchFailures = 0;
            return true;
        }

        _enterMatchFailures++;
        Log($"前往比赛误入商店（第 {_enterMatchFailures} 次），B 退出", LogLevel.Warn);
        await E.Pad.Back();
        await E.Pad.Wait(1500, Ct);
        if (_enterMatchFailures >= 2)
        {
            E.Stats.LastError = "连续 2 次按 A 后仍进入商店，焦点归位失效";
            Log("连续 2 次按 A 后仍进入商店，焦点归位失效，已停止。请手动检查游戏画面", LogLevel.Error);
            return false;
        }
        return true;
    }

    private async Task HandleScheduleAsync(Observation o)
    {
        bool key = o.Is(GameScreen.KeyMatchBadge)
                   || o.Ocr.Contains("关键比赛") || o.Ocr.Contains("keymatch");
        bool upcoming = o.Ocr.Contains("即将进行") || o.Ocr.Contains("upcoming");

        if (key) Log("识别到：关键比赛（Key Match）", LogLevel.Success);
        else Log("识别到：普通比赛", LogLevel.Info);

        if (upcoming && !key)
            await E.Pad.Wait(3000, Ct);

        await FinishOneAsync(key, forfeit: M.OnlyKeyMatches && !key);
    }

    private async Task FinishOneAsync(bool key, bool forfeit)
    {
        var o = await E.ObserveAsync(Ct);
        if (o.Is(GameScreen.Schedule) || o.Is(GameScreen.KeyMatchBadge) || o.Is(GameScreen.MyLeagueHome))
        {
            await E.Pad.Confirm();
            await E.Pad.Wait(3000, Ct);
            o = await E.ObserveAsync(Ct);
            if (o.Is(GameScreen.Schedule))
            {
                await E.SelectMenuByTextAsync(new[] { "选择符合", "前往比赛", "关键比赛", "gotomatch", "keymatch" }, Ct, 6);
                await E.Pad.Wait(2500, Ct);
            }
        }

        var run = await PlayOneMatchAsync(key, forfeit);

        if (!run.Completed)
        {
            Log("本场未确认结束（可能仍在比赛中），不计场次，下轮重新观察接管", LogLevel.Warn);
            E.ResetUnknown();
            return;
        }

        E.Stats.MatchesDone++;
        if (run.IsKeyMatch) E.Stats.KeyMatches++; else E.Stats.NormalMatches++;
        if (run.Forfeited) E.Stats.Forfeited++;
        E.ResetUnknown();
        Log($"本场结束（{(run.IsKeyMatch ? "关键" : "普通")}{(run.Forfeited ? "·已弃权" : "")}），" +
            $"累计 {E.Stats.MatchesDone} 场 / 关键 {E.Stats.KeyMatches} / 弃权 {E.Stats.Forfeited}",
            LogLevel.Success);
    }

    /// <summary>
    /// 识别「使用的物品」面板并逐项判定道具是否在使用中。游戏存在两种版式：
    /// ① 全屏页：物品名从屏幕左缘开始（中心 x&lt;0.55）；
    /// ② 联赛主页右侧轮播面板（右下角 RS 小圆点）：物品行在右 1/3（中心 x&gt;0.58），
    ///    粉色标题 OCR 经常失败，改用白色物品行关键词（杰出 / 主教练强化 / 经验值）定位。
    /// 仅标题命中但读不到物品行时返回 true 但三项全部 false（调用方会走商店兜底，安全）。
    /// </summary>
    private static bool DetectInUseItems(OcrResult ocr, out bool sidePanel,
        out bool exp4xInUse, out bool condInUse, out bool mgrInUse, out bool empty)
    {
        empty = ocr.Contains("没有正在使用");
        bool titleHit = ocr.Contains("使用的物品") || empty;

        bool lExp = false, lCond = false, lMgr = false;
        bool rExp = false, rCond = false, rMgr = false;

        foreach (var w in ocr.MergedWords)
        {
            string n = OcrText.Norm(w.Text);
            int kind;
            if (n.Contains("主教练强化") || n.Contains("教练强化")) kind = 2;
            else if (n.Contains("杰出")) kind = 1;
            else if (n.Contains("经验")) kind = 0;
            else continue;

            // 物品行只可能出现在面板中段，滤掉头部/底部按钮栏的偶然误识
            if (w.CenterY < 0.28 || w.CenterY > 0.82) continue;

            if (w.CenterX < 0.55)
            {
                if (kind == 0) lExp = true;
                else if (kind == 1) lCond = true;
                else lMgr = true;
            }
            else if (w.CenterX > 0.58)
            {
                if (kind == 0) rExp = true;
                else if (kind == 1) rCond = true;
                else rMgr = true;
            }
        }

        bool page = titleHit || lExp || lCond || lMgr || rExp || rCond || rMgr;
        if (!page)
        {
            sidePanel = false;
            exp4xInUse = condInUse = mgrInUse = false;
            return false;
        }

        // 右侧面板版式：只有右区命中（或右区命中且标题也在）；否则按全屏页处理
        bool rightHit = rExp || rCond || rMgr;
        bool leftHit = lExp || lCond || lMgr;
        sidePanel = rightHit && !leftHit;
        if (sidePanel)
        {
            exp4xInUse = rExp; condInUse = rCond; mgrInUse = rMgr;
        }
        else
        {
            exp4xInUse = lExp; condInUse = lCond; mgrInUse = lMgr;
        }
        if (empty) exp4xInUse = condInUse = mgrInUse = false;
        return true;
    }

    /// <summary>画面 OCR 指纹：所有原始词按阅读序拼接（截断 160 字），用于判定翻页后画面是否真的变化。</summary>
    private static string OcrFingerprint(OcrResult ocr)
    {
        var s = string.Concat(ocr.Words
            .OrderBy(w => w.Y).ThenBy(w => w.X)
            .Select(w => OcrText.Norm(w.Text)));
        return s.Length <= 160 ? s : s[..160];
    }

    /// <summary>
    /// 联赛主页右摇杆向右翻页查看「使用的物品」，逐项判定哪个道具已生效：
    /// 经验→4x 在用；状态→杰出在用；强化→主教练强化在用。
    /// 全部启用项都在用才跳过；否则前往「兑换积分」购买缺的部分。
    /// </summary>
    private async Task BuyItemsIfNeededAsync()
    {
        bool need4x = M.Buy4xExp, needCond = M.BuyExcellentCondition, needMgr = M.BuyManagerBoost;
        if (!need4x && !needCond && !needMgr)
            return;

        // 1. 先看当前帧（右侧轮播面板可能本来就停在「使用的物品」页，一次摇杆都不用推）；
        //    没命中再右摇杆向右逐页翻。两种版式都认：全屏页物品名在左半幅，
        //    主页右侧面板物品行在右 1/3。
        //    终止：命中物品页 / 翻回主页（看到「前往比赛」）/ 连续两屏指纹不变（到头）/ 最多 4 次。
        Log("检查物品栏：查看「使用的物品」面板…", LogLevel.Info);
        var o = await E.ObserveAsync(Ct);
        o = await E.Overlay.PassThroughAsync(o, Ct);

        bool inUsePage = DetectInUseItems(o.Ocr, out bool sidePanel,
            out bool exp4xInUse, out bool condInUse, out bool mgrInUse, out bool noItem);
        int flips = 0;
        string prevFp = OcrFingerprint(o.Ocr);
        const int maxFlips = 4;
        while (!inUsePage && flips < maxFlips)
        {
            // 全屏轮播绕完一圈会回主页（右侧面板版式下「前往比赛」常驻，
            // flips>=2 停手也足够覆盖该面板的两个分页）
            if (flips >= 2 && o.Ocr.Contains("前往比赛"))
            {
                Log("翻页已绕回主页，未找到「使用的物品」面板", LogLevel.Info);
                break;
            }
            await E.Pad.RsFlick(PadDir.Right);
            await E.Pad.Wait(1400, Ct);
            o = await E.ObserveAsync(Ct);
            o = await E.Overlay.PassThroughAsync(o, Ct);
            flips++;
            inUsePage = DetectInUseItems(o.Ocr, out sidePanel,
                out exp4xInUse, out condInUse, out mgrInUse, out noItem);
            string fp = OcrFingerprint(o.Ocr);
            if (!inUsePage && fp == prevFp)
            {
                Log($"右翻第 {flips} 次后画面无变化，横向面板已到头", LogLevel.Info);
                break;
            }
            prevFp = fp;
        }
        if (inUsePage)
            Log($"第 {(flips == 0 ? "当前屏" : $"{flips} 次翻页")}命中「使用的物品」{(sidePanel ? "（右侧面板）" : "（全屏页）")}", LogLevel.Debug);

        // 2. 逐项判定使用中（DetectInUseItems 已按物品行所在区域给出结果）
        if (!inUsePage)
        {
            // 没确认到物品页（空列表/标题 OCR 失败）：在用/缺失判定不可信，不能跳过——
            // 改去商店逐行核对：商店行内有「使用中」检测 + 游戏购买确认弹窗双保险，不会重复购买
            Log("未能确认「使用的物品」页面（空列表/标题 OCR 失败），改为前往商店逐行核对", LogLevel.Warn);
            exp4xInUse = condInUse = mgrInUse = false;
        }
        else if (noItem)
        {
            Log("物品栏为空：没有正在使用的物品", LogLevel.Info);
        }
        else
        {
            Log($"物品栏：4x经验[{(exp4xInUse ? "在用" : "缺")}] 杰出状态[{(condInUse ? "在用" : "缺")}] 教练强化[{(mgrInUse ? "在用" : "缺")}]", LogLevel.Info);
        }

        bool skip4x = !need4x || exp4xInUse;
        bool skipCond = !needCond || condInUse;
        bool skipMgr = !needMgr || mgrInUse;

        // 无论是否需要购买，都先按翻过去的次数左翻回主页（面板是轮播，Left×flips 必回主页）
        for (int i = 0; i < flips; i++)
        {
            await E.Pad.RsFlick(PadDir.Left);
            await E.Pad.Wait(i == 0 ? 1000 : 900, Ct);
        }

        if (skip4x && skipCond && skipMgr)
        {
            Log("所有启用的道具均在使用中，跳过购买", LogLevel.Debug);
            return;
        }

        Log("存在缺失道具，前往「兑换积分」购买…", LogLevel.Info);

        // 3.（已翻回主页）固定 Down→Right，再按光标实际落点进入：
        //    右列三张卡竖向排列 = 兑换积分(上)/赛季纪录(中)/建议(下)，
        //    Down→Right 后焦点可能落在兑换积分或赛季纪录（不同分辨率/时序不一致），
        //    用焦点卡整卡变白的特征判定落点，在赛季纪录则 Up 一次、在建议则 Up 两次。
        if (!await EnterExchangeShopAsync())
        {
            Log("进入兑换积分商店失败，本轮跳过购买", LogLevel.Warn);
            return;
        }

        await ContinuePurchaseFromShopAsync(skip4x, skipCond, skipMgr);
    }

    /// <summary>是否已在积分商店内页（分类器 + OCR 关键词双判定）。</summary>
    private static bool IsExchangeShop(Observation o)
        => o.Is(GameScreen.Shop) || o.Is(GameScreen.ShopConfirm)
           || o.Ocr.Contains("租借") || o.Ocr.Contains("获得各种物品")
           || o.Ocr.Contains("联赛物品") || o.Ocr.Contains("选择要兑换");

    /// <summary>
    /// 智能进入「兑换积分」：高光闭环导航（每按一键都重判高光实际位置），
    /// 连续 2 帧高光停在「兑换积分」才按 A；进错则 B 退，整轮最多重试 1 次。
    /// </summary>
    private async Task<bool> EnterExchangeShopAsync()
    {
        for (int round = 0; round < 2; round++)
        {
            Log(round == 0
                ? "智能导航：闭环移动高光到「兑换积分」…"
                : "第 2 次尝试：重新归位后闭环移动高光到「兑换积分」…", LogLevel.Info);

            if (await MoveFocusToExchangeAsync())
            {
                await E.Pad.Confirm();
                await E.Pad.Wait(2500, Ct);
                var after = await E.ObserveAsync(Ct);
                if (IsExchangeShop(after))
                {
                    Log("已进入积分商店", LogLevel.Info);
                    return true;
                }
                Log("按 A 后未进入积分商店，B 退出后重试", LogLevel.Warn);
                await E.Pad.Back();
                await E.Pad.Wait(1500, Ct);
            }
            else
            {
                Log("高光未能到达「兑换积分」，重新归位后重试", LogLevel.Warn);
            }
        }
        return false;
    }

    /// <summary>
    /// 我的联赛主页焦点导航图（真机实测槽位与方向边）。
    /// 铁律（用户实测）：左列上下移动不可靠——i 按 ↓ 会直接跳到底部「赛事信息」。
    /// 光标一旦离开第一排（前往比赛/i），必须第一时间 → 进右列，再在右列内比 y 决定 ↑/↓。
    /// 故图中不声明任何左列内部垂直边（go↓schedule 除外，它是进入左列的入口）。
    /// </summary>
    private static FocusGraph BuildLeagueHomeGraph() => new FocusGraph()
        .AddSlot("go",       0.147, 0.265, "前往比赛")
        .AddSlot("info",     0.310, 0.270, "i")
        .AddSlot("schedule", 0.185, 0.515, "赛程表")
        .AddSlot("exchange", 0.505, 0.420, "兑换积分")
        .AddSlot("season",   0.505, 0.610, "赛季纪录")
        .AddSlot("advice",   0.505, 0.800, "建议")
        .AddSlot("eventinfo", 0.185, 0.800, "赛事信息")
        .MarkPhotoSlot("schedule")
        // 第一排：前往比赛 ↔ i
        .AddEdge("go", PadDir.Right, "info")
        .AddEdge("info", PadDir.Left, "go")
        // 离开第一排的唯一左列落点：赛程表（随后立即 Right 进右列）
        .AddEdge("go", PadDir.Down, "schedule")
        // i 按 ↓ 真机实测直跳底部赛事信息（不是赛程表！），之后同样立即 Right 进右列
        .AddEdge("info", PadDir.Down, "eventinfo")
        // 左列 → 右列（脱离第一排后第一时间向右）
        .AddEdge("schedule", PadDir.Right, "exchange")
        .AddEdge("eventinfo", PadDir.Right, "advice")
        // 右列内部：纯垂直移动（可靠），兑换积分 ↑ 赛季纪录 ↓ 建议
        .AddEdge("exchange", PadDir.Down, "season")
        .AddEdge("season", PadDir.Up, "exchange")
        .AddEdge("season", PadDir.Down, "advice")
        .AddEdge("advice", PadDir.Up, "season")
        // 回程（买完道具回前往比赛开赛）
        .AddEdge("schedule", PadDir.Up, "go")
        .AddEdge("exchange", PadDir.Left, "schedule");

    /// <summary>
    /// 闭环移动焦点到「兑换积分」：归位起步后交给通用 FocusWalker——
    /// 声明式焦点图 BFS 寻路（go↓赛程表→兑换积分，不走 i 死边），
    /// 150ms 判稳追踪（高光扫到目标立即接住），死边自动摘除重算，丢失归位一次，
    /// 连续 2 帧确认高光停在目标卡才返回 true（用户硬要求：高光最后停在兑换积分才点）。
    /// </summary>
    private async Task<bool> MoveFocusToExchangeAsync()
    {
        await ResetFocusToGoToMatchAsync();
        var walker = new FocusWalker(E, BuildLeagueHomeGraph(), Ct, (m, lv) => Log(m, lv))
        {
            RecoverAsync = ResetFocusToGoToMatchAsync,
        };
        return await walker.GotoAsync("exchange");
    }

    /// <summary>
    /// 道具「在用」判定：只认「使用中 / 已使用」。
    /// 「有效场数:N 场」是道具时效说明，商店每行常驻，绝不能作为在用信号。
    /// </summary>
    internal static bool IsInUse(OcrResult ocr)
        => ocr.Contains("使用中") || ocr.Contains("已使用");

    /// <summary>
    /// 积分商店内页：Right Tab → 「我的联赛物品」，按列表从上到下的顺序逐项购买。
    /// 列表行号（从顶部焦点行 0=状态普通+ 起）：杰出状态=2，经验值4x=5，主教练强化=12。
    /// 每项流程：行内「使用中」判定 → A → 弹窗分支（使用中/资金不足/确认购买 Up+A）。
    /// </summary>
    private async Task ContinuePurchaseFromShopAsync(bool skip4x = false, bool skipCond = false, bool skipMgr = false)
    {
        // 1. Right 选中「我的联赛物品」Tab → A
        Log("商店：Right → 「我的联赛物品」→ A", LogLevel.Info);
        await E.Pad.Dpad(PadDir.Right);
        await E.Pad.Wait(600, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2500, Ct);

        // 2. 按列表顺序（上→下）逐项处理：行号从顶部焦点行起算
        var items = new (int Row, string Name, bool Skip)[]
        {
            (2,  "杰出状态",   skipCond),
            (5,  "经验值4x",   skip4x),
            (12, "主教练强化", skipMgr),
        };

        int curRow = 0;
        foreach (var (row, name, skip) in items)
        {
            if (Ct.IsCancellationRequested) return;
            if (skip) continue;

            // 移动到目标行（只向下，顺序保证 row 递增）
            int downs = row - curRow;
            if (downs > 0)
            {
                Log($"物品列表：Down×{downs} → {name}", LogLevel.Info);
                for (int i = 0; i < downs; i++)
                {
                    await E.Pad.Dpad(PadDir.Down);
                    await E.Pad.Wait(400, Ct);
                }
                curRow = row;
            }
            await E.Pad.Wait(400, Ct);

            // 行内「使用中」检测（2 次重试）
            bool inUse = false;
            for (int retry = 0; retry < 2 && !inUse; retry++)
            {
                var o = await E.ObserveAsync(Ct);
                var texts = string.Join("|", o.Ocr.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).Take(20).Select(w => w.Text));
                Log($"{name} 使用中检测 (retry {retry + 1}/2)：OCR={texts}", LogLevel.Debug);
                if (IsInUse(o.Ocr))
                    inUse = true;
                else
                    await E.Pad.Wait(300, Ct);
            }
            if (inUse)
            {
                Log($"{name} 已在使用中，跳过", LogLevel.Info);
                continue;
            }

            // A 让游戏弹结果弹窗做最终确认
            Log($"A 选中{name}，等待游戏结果弹窗", LogLevel.Info);
            await E.Pad.Confirm();
            await E.Pad.Wait(2000, Ct);

            var popup = await E.ObserveAsync(Ct);
            var popupText = string.Join("|", popup.Ocr.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).Take(25).Select(w => w.Text));
            Log($"{name} 弹窗原文：{popupText}", LogLevel.Debug);

            // 弹窗分支 a：使用中（行内 OCR 漏了但游戏知道）
            if (IsInUse(popup.Ocr))
            {
                Log($"{name} 弹窗确认使用中，A 关闭，继续下一项", LogLevel.Info);
                await E.Pad.Confirm();
                await E.Pad.Wait(1200, Ct);
                continue;
            }

            // 弹窗分支 b：资金/积分不足 → 后面的更买不起，直接结束购买回主页
            if (popup.Ocr.Contains("资金不足") || popup.Ocr.Contains("积分不足") || popup.Ocr.Contains("不够"))
            {
                Log($"{name} 资金不足，结束本次购买。弹窗：{popupText}", LogLevel.Warn);
                await E.Pad.Back();
                await E.Pad.Wait(1200, Ct);
                await E.Pad.Back();
                await E.Pad.Wait(1500, Ct);
                return;  // 主循环接管
            }

            // 弹窗分支 c：确认购买 → Up → A
            Log($"{name} 确认购买弹窗：Up → A", LogLevel.Info);
            await E.Pad.Dpad(PadDir.Up);
            await E.Pad.Wait(500, Ct);
            await E.Pad.Confirm();
            await E.Pad.Wait(2000, Ct);
            E.Stats.RewardsClaimed++;
            Log($"{name} 购买完成", LogLevel.Success);

            // 购买后结果弹窗循环 A 清掉（最多 3 次）
            for (int p = 0; p < 3; p++)
            {
                var after = await E.ObserveAsync(Ct);
                if (!after.Ocr.Contains("使用中") && !after.Ocr.Contains("已使用")
                    && !after.Ocr.Contains("确定") && !after.Ocr.Words.Any(w => w.Text.Trim() == "OK"))
                    break;
                await E.Pad.Confirm();
                await E.Pad.Wait(1200, Ct);
            }
        }

        // 3. 全部处理完，B 连退回联赛主页（主循环会重新走 BackToHome+Enter+弹窗清扫）
        for (int i = 0; i < 3; i++)
        {
            var back = await E.ObserveAsync(Ct);
            if (back.Is(GameScreen.MyLeagueHome)) break;
            await E.Pad.Back();
            await E.Pad.Wait(1500, Ct);
        }
    }
}
