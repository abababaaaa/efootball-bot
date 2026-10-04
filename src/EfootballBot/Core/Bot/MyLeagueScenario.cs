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
            o = await PassThroughAsync(o);

            switch (o.Screen)
            {
                case GameScreen.Home:
                case GameScreen.MatchHub:
                    await E.EnterMyLeagueAsync(Ct);
                    continue;

                case GameScreen.MyLeagueHome:
                    // 顺序严格：① 清扫弹窗（关键比赛=Up→A / 其他=A） ② 购买经验卡 ③ A 前往比赛
                    for (int popup = 0; popup < 8; popup++)
                    {
                        Ct.ThrowIfCancellationRequested();
                        var p = await E.ObserveAsync(Ct);
                        p = await PassThroughAsync(p);
                        bool isKeyMatch = p.Ocr.Contains("关键比赛");
                        // 「OK」必须独立成词（精确匹配），否则赛程卡上的 "Fukuoka" 会子串命中导致误判
                        bool hasOk = p.Ocr.Words.Any(w =>
                        {
                            string t = OcrText.Norm(w.Text);
                            return t.Equals("OK", StringComparison.OrdinalIgnoreCase)
                                   || t.Equals("okay", StringComparison.OrdinalIgnoreCase);
                        });
                        bool hasPopup = isKeyMatch || hasOk
                                        || p.Ocr.Contains("实时更新")
                                        || p.Ocr.Contains("下一步")
                                        || p.Ocr.Contains("确定") || p.Ocr.Contains("物品");
                        if (!hasPopup) break;
                        Log($"联赛主页弹窗（第{popup + 1}轮）：{(isKeyMatch ? "关键比赛" : "其他")}", LogLevel.Info);
                        if (isKeyMatch)
                        {
                            await E.Pad.Dpad(PadDir.Up);
                            await E.Pad.Wait(400, Ct);
                            await E.Pad.Confirm();
                        }
                        else if (hasOk)
                        {
                            // OK 弹窗默认焦点在 OK 上，直接 A（不能走 PressButtonAsync 子串匹配，会误点 Fukuoka）
                            await E.Pad.Confirm();
                        }
                        else if (!await E.PressButtonAsync(new[] { "确定", "确认", "下一步" }, Ct))
                        {
                            await E.Pad.Confirm();
                        }
                        await E.Pad.Wait(1200, Ct);
                    }

                    await BuyItemsIfNeededAsync();
                    await EnterNextMatchAsync();
                    break;

                case GameScreen.Schedule:
                    await HandleScheduleAsync(o);
                    break;

                case GameScreen.PreMatch:
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
                        await E.EnterMyLeagueAsync(Ct);
                    }
                    break;
            }
        }
    }

    private async Task EnterNextMatchAsync()
    {
        Log("我的联赛主页：前往下一场比赛", LogLevel.Info);
        await E.Pad.Confirm();
        await E.Pad.Wait(3000, Ct);
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
    /// 联赛主页右摇杆向右翻页查看「使用的物品」：
    /// 若"没有正在使用的物品"，前往「兑换积分」购买经验卡等道具。
    /// 导航：默认焦点在"前往比赛" → Down → Right → 右侧面板"兑换积分"。
    /// </summary>
    private async Task BuyItemsIfNeededAsync()
    {
        bool buyEnabled = M.Buy4xExp || M.BuyExcellentCondition || M.BuyManagerBoost;

        // 1. 右摇杆向右翻页到"使用的物品"页
        Log("检查物品栏：右摇杆向右翻页查看「使用的物品」…", LogLevel.Info);
        await E.Pad.RsFlick(PadDir.Right);
        await E.Pad.Wait(1400, Ct);
        var o = await E.ObserveAsync(Ct);
        o = await PassThroughAsync(o);

        bool inUsePage = o.Ocr.Contains("使用的物品") || o.Ocr.Contains("没有正在使用");
        if (!inUsePage)
        {
            await E.Pad.RsFlick(PadDir.Right);
            await E.Pad.Wait(1400, Ct);
            o = await E.ObserveAsync(Ct);
        }

        bool noItem = o.Ocr.Contains("没有正在使用");
        if (!noItem && (o.Ocr.Contains("经验") || o.Ocr.Contains("强化") || o.Ocr.Contains("状态")))
        {
            Log("物品栏已有生效中的道具，跳过购买", LogLevel.Debug);
            return;
        }
        if (!noItem && !inUsePage)
        {
            Log("未能确认物品栏状态，跳过购买（不影响刷级）", LogLevel.Warn);
            return;
        }
        if (!buyEnabled)
        {
            Log("物品栏为空，但未勾选任何自动购买项，跳过购买", LogLevel.Info);
            return;
        }

        Log("没有正在使用的物品，前往「兑换积分」购买…", LogLevel.Info);

        // 2. 右摇杆翻回第一页
        await E.Pad.RsFlick(PadDir.Left);
        await E.Pad.Wait(1000, Ct);
        await E.Pad.RsFlick(PadDir.Left);
        await E.Pad.Wait(1200, Ct);

        // 3. Down→Right 进入右侧面板的"兑换积分"→ A
        Log("导航：Down → Right → 「兑换积分」→ A", LogLevel.Info);
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(350, Ct);
        await E.Pad.Dpad(PadDir.Right);
        await E.Pad.Wait(900, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2500, Ct);

        await ContinuePurchaseFromShopAsync();
    }

    /// <summary>积分商店内页：Right Tab → Down×5 → 验证焦点 → 判定使用中 → A 弹结果弹窗确认。</summary>
    private async Task ContinuePurchaseFromShopAsync()
    {
        // 1. Right 选中「我的联赛物品」Tab → A
        Log("商店：Right → 「我的联赛物品」→ A", LogLevel.Info);
        await E.Pad.Dpad(PadDir.Right);
        await E.Pad.Wait(600, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2500, Ct);

        // 2. Down×5 → 经验值4x（Tab 切换后第一行=状态普通+，往下 5 行到经验值4x）
        Log("物品列表：Down×5 → 经验值4x", LogLevel.Info);
        for (int i = 0; i < 5; i++)
        {
            await E.Pad.Dpad(PadDir.Down);
            await E.Pad.Wait(400, Ct);
        }
        await E.Pad.Wait(400, Ct);

        // 3. 验证焦点：OCR 找"经验值4x"或"Exp."，确保真到了目标行
        var posChk = await E.ObserveAsync(Ct);
        var posWords = string.Join("|", posChk.Ocr.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).Take(12).Select(w => w.Text));
        Log($"Down×5 后位置验证 OCR：{posWords}", LogLevel.Debug);
        bool onExp4x = posChk.Ocr.Contains("经验值4x") || posChk.Ocr.Contains("4x")
                       || posChk.Ocr.Contains("exp4x") || posChk.Ocr.Contains("Exp");
        if (!onExp4x)
        {
            // OCR 漏识，再 Down×2 做兜底（可能列表多了一行）
            Log("位置验证未识别经验值4x，再 Down×2 兜底", LogLevel.Warn);
            await E.Pad.Dpad(PadDir.Down);
            await E.Pad.Wait(400, Ct);
            await E.Pad.Dpad(PadDir.Down);
            await E.Pad.Wait(400, Ct);
        }

        // 4. 判断经验值4x 是否在使用中：OCR 3 次重试 + 弹窗二次确认
        bool inUse = false;
        var popupAfter = posChk;
        for (int retry = 0; retry < 3 && !inUse; retry++)
        {
            var o = await E.ObserveAsync(Ct);
            var texts = string.Join("|", o.Ocr.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).Take(20).Select(w => w.Text));
            Log($"使用中检测 (retry {retry + 1}/3)：OCR={texts}", LogLevel.Debug);
            if (o.Ocr.Contains("使用中") || o.Ocr.Contains("物品使用中"))
            {
                inUse = true;
                break;
            }
            await E.Pad.Wait(300, Ct);
        }

        if (inUse)
        {
            Log("经验值4x 已在使用中，B×2 回联赛主页", LogLevel.Success);
            await E.Pad.Back();
            await E.Pad.Wait(1200, Ct);
            await E.Pad.Back();
            await E.Pad.Wait(1500, Ct);
            return;  // 主循环会回到联赛主页并重新走 BackToHome+Enter+弹窗清扫
        }

        // 5. OCR 也没识别使用中，A 让游戏弹结果弹窗做最终确认
        //    （游戏一定会弹：使用中 → OK 弹窗 / 可购买 → 确认购买弹窗 / 没钱 → 资金不足弹窗）
        Log("A 选中经验值4x，等待游戏结果弹窗", LogLevel.Info);
        await E.Pad.Confirm();
        await E.Pad.Wait(2000, Ct);

        var popup = await E.ObserveAsync(Ct);
        var popupText = string.Join("|", popup.Ocr.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).Take(25).Select(w => w.Text));
        Log($"游戏结果弹窗原文：{popupText}", LogLevel.Debug);

        // 5a. 游戏弹"物品使用中"（OCR 3 次漏了但游戏知道）
        if (popup.Ocr.Contains("使用中") || popup.Ocr.Contains("物品使用中"))
        {
            Log("游戏弹窗确认物品使用中（OCR 轮次未捕到），OK 关闭后 B×2 回主页", LogLevel.Success);
            await E.Pad.Confirm();
            await E.Pad.Wait(800, Ct);
            await E.Pad.Back();
            await E.Pad.Wait(1200, Ct);
            await E.Pad.Back();
            await E.Pad.Wait(1500, Ct);
            return;  // 主循环接管
        }

        // 5b. 资金/积分不足
        if (popup.Ocr.Contains("资金不足") || popup.Ocr.Contains("积分不足")
            || popup.Ocr.Contains("不够") || popup.Ocr.Contains("insufficient"))
        {
            Log($"资金不足，无法购买经验值4x。弹窗：{popupText}", LogLevel.Warn);
            await E.Pad.Back();
            await E.Pad.Wait(1200, Ct);
            await E.Pad.Back();
            await E.Pad.Wait(1500, Ct);
            return;  // 主循环接管
        }

        // 5c. 确认购买弹窗
        Log("确认购买弹窗：Up → A", LogLevel.Info);
        await E.Pad.Dpad(PadDir.Up);
        await E.Pad.Wait(500, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2000, Ct);
        E.Stats.RewardsClaimed++;
        Log("道具购买完成", LogLevel.Success);

        // 6. 购买后结果弹窗循环 A 清掉（最多 3 次）
        for (int p = 0; p < 3; p++)
        {
            var after = await E.ObserveAsync(Ct);
            if (!after.Ocr.Contains("物品使用中") && !after.Ocr.Contains("已使用")
                && !after.Ocr.Contains("OK") && !after.Ocr.Contains("确定"))
                break;
            await E.Pad.Confirm();
            await E.Pad.Wait(1200, Ct);
        }

        // 7. B 连退回联赛主页（主循环会重新走 BackToHome+Enter+弹窗清扫）
        for (int i = 0; i < 3; i++)
        {
            var back = await E.ObserveAsync(Ct);
            if (back.Is(GameScreen.MyLeagueHome)) break;
            await E.Pad.Back();
            await E.Pad.Wait(1500, Ct);
        }
    }
}
