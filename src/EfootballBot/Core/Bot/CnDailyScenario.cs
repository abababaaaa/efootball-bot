using EfootballBot.Core.Capture;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.System;
using EfootballBot.Core.Vision;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 国服（实况足球在线 eFootballOnline）巡回赛挂机。真机校准路径：
/// 主大厅 →〔顶部 Tab：比赛〕→〔比赛活动〕卡 →〔对阵电脑〕列表 → 巡回赛主页
/// → 前往比赛 → 难度选择页（难度继承上次，直接确认）→ 匹配中 → 比赛 → 结算 → 回到巡回赛主页循环。
/// 巡回主页的弹窗清扫 / 主要奖励对勾 / i 详情 x/x 判定在 ScenarioBase 中与国际服周活动共享。
/// 状态机可从任意中间画面接续；同一画面长时间无进展则回主大厅重新导航。
/// </summary>
public sealed class CnDailyScenario : ScenarioBase
{
    public CnDailyScenario(BotEngine engine, AppConfig cfg, CancellationToken ct) : base(engine, cfg, ct) { }
    public override string DisplayName => "国服 · 巡回赛挂机";

    private CnConfig C => Cfg.Cn;

    // 顶部「比赛」Tab 页面内的卡片候选（焦点导航兜底用）
    private static readonly string[] MatchMenuCards = { "比赛活动", "天梯巅峰赛", "我的联赛", "好友对战" };

    // 本次运行内已完成的活动卡槽位黑名单（左起第几张，0 基）：巡回赛拿满主要奖励后游戏不打勾，
    // 挑战赛全通关后卡序可能重排；用槽位黑名单在本次运行内跳过，避免反复进入同一活动空刷。
    // 巡回 / 挑战各自独立登记。进程重启 / 重新开始挂机后清空。
    private readonly HashSet<int> _tourBlacklist = new();
    private readonly HashSet<int> _challengeBlacklist = new();
    // 活动列表主页面上直接识别到卡右上角绿勾（=活动已完成）时登记的统一黑名单。
    // 与 _tourBlacklist / _challengeBlacklist 共用同一「逻辑卡索引」空间（含右扫翻页基数）。
    private readonly HashSet<int> _doneEvents = new();
    // SelectAiEventAsync 当前焦点卡槽位（逻辑卡索引，含翻页基数）；活动完成时用它登记黑名单
    private int _currentEventSlot = -1;

    // ---- 挑战清单内选择状态（移动以画面签名验证，不写死卡数）----
    // 关键原则：任何读卡前必须用 MoveAndSettleAsync 等轮播动画停稳；
    // 拉黑不再单独反向全程复核，直接信任 FindPlayable 的左扫结果（以速度换误拉黑风险）。
    // 选卡目标 = 最右一张未锁未打勾卡（完成卡=前缀、锁卡=后缀）。
    private enum ChalStep { Collapse, SweepRight, FindPlayable }
    private ChalStep _chalStep = ChalStep.Collapse;
    private int _moveCount;           // 防失控保险（不代表卡数）
    private int _retryCount;          // 整轮扫描失败重试次数（2 次内重扫，超出不拉黑退出）
    private int _edgeTries;           // 右扫时连续判"到边"的确认次数（需≥2次才真到边，防轮播微步漏卡）

    // ---- 「从 x+1 开始判定」：记录卡号仅在挑战清单屏幕（OCR 可靠）读写，绝不在主页读 ----
    private int? _currentCardNumber;        // 找到目标卡时（清单屏）录入即将要打的关卡号
    private int? _lastCompletedCardNumber;  // 刚完成的关卡号 x（主页奖励门控消费 _currentCardNumber 赋值）

    public override async Task RunAsync()
    {
        int stuck = 0;
        GameScreen? lastScreen = null;

        while (!Ct.IsCancellationRequested)
        {
            Ct.ThrowIfCancellationRequested();
            if (TargetReached(E.Stats.EventsDone, C.DailyLoopLimit))
            {
                Log($"已达到国服挂机循环上限（{C.DailyLoopLimit} 场），停止。", LogLevel.Success);
                return;
            }

            var o = await E.ObserveAsync(Ct);
            o = await PassThroughAsync(o);

            stuck = o.Screen == lastScreen ? stuck + 1 : 0;
            lastScreen = o.Screen;

            switch (o.Screen)
            {
                case GameScreen.Home:
                    await GoToMatchTabAsync();
                    break;

                case GameScreen.MatchHub:
                    await OpenMatchActivityCardAsync();
                    break;

                case GameScreen.EventHub:
                    _currentCardNumber = null;
                    _lastCompletedCardNumber = null; // 新活动从零开始
                    if (!await SelectAiEventAsync())
                    {
                        Log("活动列表中无剩余可自动打的巡回活动，国服挂机结束。", LogLevel.Success);
                        return;
                    }
                    break;

                case GameScreen.NoEvent:
                    // 误入「对阵玩家」Tab 空页：B 返回对阵电脑列表，下轮重新扫描
                    Log("无进行中的对阵玩家活动：B 返回活动列表", LogLevel.Warn);
                    await E.Pad.Back();
                    await E.Pad.Wait(2200, Ct);
                    break;

                case GameScreen.MyLeagueHome:
                    if (await HandleTourHomeAsync() && _currentEventSlot >= 0)
                    {
                        // 本活动主要奖励全部 x/x：拉黑该槽位，本次运行不再进入
                        _tourBlacklist.Add(_currentEventSlot);
                        Log($"活动卡 #{_currentEventSlot + 1} 已拉黑（主要奖励全满），本次运行不再进入", LogLevel.Success);
                        _currentEventSlot = -1;
                    }
                    break;

                case GameScreen.EventInfo:
                    // 挑战赛详情页（欢度十一…第N弹）：点「进入」，弹「确定」先点确定
                    Log("挑战赛详情页：进入", LogLevel.Info);
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
                    await HandleChallengeHomeAsync();
                    break;

                case GameScreen.ActivityDetail:
                    // 兜底（正常流程不会停留在此）：B 关闭详情回主页，下轮重走判定
                    Log("活动详情页：关闭返回主页", LogLevel.Debug);
                    await E.Pad.Back();
                    await E.Pad.Wait(1800, Ct);
                    break;

                case GameScreen.PreMatch:
                    // 确认难度后直接进入比赛等待流程：随后的球场展示过场若判未知，
                    // 外层恢复会按 B（可能退出比赛）；在比赛流程内部则统一按 A 推进
                    await ConfirmDifficultyAsync();
                    await PlayTourMatchAsync();
                    break;

                case GameScreen.Loading:
                    // 「匹配中」是通用开赛信号：只要出现即进入比赛流程（等开球→挂机→结算），
                    // 覆盖挑战赛（无前赛难度页，前往比赛后直连匹配）与各类异常落点
                    if (IsMatchmaking(o))
                    {
                        Log("检测到「匹配中」，进入比赛等待流程", LogLevel.Info);
                        await PlayTourMatchAsync();
                    }
                    else
                    {
                        // KONAMI 转圈 / 黑屏加载，不操作
                        await E.Pad.Wait(2500, Ct);
                    }
                    break;

                case GameScreen.InMatch:
                    await PlayTourMatchAsync();
                    break;

                case GameScreen.Result:
                case GameScreen.Rewards:
                case GameScreen.FullTime:
                case GameScreen.HalfTime:
                    await PostMatchSequenceAsync();
                    break;

                case GameScreen.Disconnected:
                    await E.HandlePopupsAsync(o, Ct);
                    await E.Pad.Wait(5000, Ct);
                    break;

                default:
                    // 通用规则：有「确定」就点确定，有「进入」就点进入；高光不在目标上就移过去
                    if (await TryConfirmOrEnterAsync(o))
                        break;
                    // 安全恢复：先等待（匹配/过场绝不按 B）→ 最多一次 B → 最后才回主页
                    if (await HandleUnknownAsync(o))
                    {
                        stuck = 0;
                        lastScreen = null;
                    }
                    break;
            }

            // 同一画面连续无进展：整体回主大厅重新导航。
            // 挑战清单的连续同名是扫描动作本身（收拢/右移/读卡各算一轮，有 8 次上限自限），
            // 看门狗对它放行，避免扫卡途中误回主页
            bool watchdogable = o.Screen is not (GameScreen.EventHub or GameScreen.ChallengeList);
            if (watchdogable && stuck > 12)
            {
                Log("长时间无画面进展，回主大厅重新导航。", LogLevel.Warn);
                await E.BackToHomeAsync(Ct);
                stuck = 0;
                lastScreen = null;
            }
        }
    }

    /// <summary>主大厅 → 顶部「比赛」Tab：焦点收拢到顶部 Tab 行，右移一格（推荐→比赛），A。</summary>
    private async Task GoToMatchTabAsync()
    {
        Log("国服导航：主大厅 → 顶部「比赛」Tab", LogLevel.Info);
        // 先 Down：若焦点停在头部手柄归属图标上（异常态），先落回磁贴行；
        // 焦点本在磁贴时 Down 无相邻控件、焦点不动。
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(300, Ct);
        for (int i = 0; i < 4; i++)
        {
            await E.Pad.Dpad(PadDir.Up);
            await E.Pad.Wait(120, Ct);
        }
        await E.Pad.Dpad(PadDir.Right); // 推荐 → 比赛
        await E.Pad.Wait(400, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2800, Ct);

        var after = await E.ObserveAsync(Ct);
        if (after.Is(GameScreen.MatchHub)) return;

        // 兜底：直接焦点导航到比赛菜单里的「比赛活动」卡
        Log("Tab 键序未生效，焦点导航「比赛活动」", LogLevel.Warn);
        if (await E.FocusAndConfirmAsync(new[] { "比赛活动" }, MatchMenuCards, Ct))
            await E.Pad.Wait(2800, Ct);
    }

    /// <summary>
    /// 比赛菜单 →「比赛活动」卡：按 Down 进入卡片区（默认落在中间「天梯巅峰赛」卡），
    /// 再 Left 移到最左「比赛活动」卡，A。
    /// </summary>
    private async Task OpenMatchActivityCardAsync()
    {
        Log("国服导航：打开「比赛活动」卡（下→左→确定）", LogLevel.Info);
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(300, Ct);
        await E.Pad.Dpad(PadDir.Left);
        await E.Pad.Wait(300, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2800, Ct);

        var after = await E.ObserveAsync(Ct);
        if (after.Is(GameScreen.EventHub)) return;

        Log("未进入对阵电脑列表，焦点导航「比赛活动」", LogLevel.Warn);
        if (await E.FocusAndConfirmAsync(new[] { "比赛活动" }, MatchMenuCards, Ct))
            await E.Pad.Wait(2800, Ct);
    }

    /// <summary>
    /// 对阵电脑列表选活动：只有卡片右上角「手柄图标 + AI 图标」同时出现才可自动打。
    /// 先收拢焦点（Up×2 到二级 Tab），再 Down, Left 到最左卡；一次观察识别全部可见卡位，
    /// 跳过已拉黑槽位，选「逻辑最左」的可进卡，再按差值 Right 移动焦点并确认。
    /// 返回 false 表示列表已无剩余可进活动（扫描越界或全部被拉黑），调用方应结束挂机。
    /// </summary>
    private async Task<bool> SelectAiEventAsync()
    {
        Log("国服导航：扫描可 AI 自动比赛的活动", LogLevel.Info);

        // 收拢到二级 Tab，再进入卡片区最左卡（无论初始焦点在哪都确定）
        await E.Pad.Dpad(PadDir.Up);
        await E.Pad.Wait(180, Ct);
        await E.Pad.Dpad(PadDir.Up);
        await E.Pad.Wait(180, Ct);
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(450, Ct);
        // 双 Down 保险：若第一次 Down 因时序未离开 Tab 行，第二次补上；
        // 已在卡片区时 Down 无相邻控件、焦点不动。避免随后的 Left 在 Tab 行误切到「对阵玩家」
        await E.Pad.Dpad(PadDir.Down);
        await E.Pad.Wait(300, Ct);
        await E.Pad.Dpad(PadDir.Left);
        await E.Pad.Wait(400, Ct);

        // 逐卡扫描。相似活动卡（红底天安门「欢度十一」等）画面几乎一样，图像签名 MAD 仅 0~5，
        // 无法用内容签名区分相邻卡（会误判"没动"→提前"到最右"漏卡）。故改用【OCR 文字指纹】
        // 判换卡：焦点卡区域内的文字（「第X弹」/奖励等）是相邻相似卡唯一区别。
        _doneEvents.Clear(); // 每次重新扫描活动列表都重新检测绿勾，不带上一轮误判残留（否则永久跳过某卡）
        int logicalIdx = 0;
        var recentFps = new List<string>(); // 最近 3 张焦点卡的 OCR 指纹：判到最右用（防末卡震荡产生幻影卡）
        int stuck = 0;           // 指纹重复/回到最近见过的卡的次数（≥3 判到最右）

        // 取真实窗口几何（全分辨率，循环内不变）
        double offX = 8, offY = 31, cliW = 1280, cliH = 720;
        if (E.Window is not null)
        {
            var geo = GameWindow.GetWindowFrame(E.Window.Handle);
            if (geo.CliW > 0 && geo.CliH > 0) (offX, offY, cliW, cliH) = (geo.OffX, geo.OffY, geo.CliW, geo.CliH);
        }

        for (int screenGuard = 0; screenGuard < 60; screenGuard++)
        {
            var scan = await E.ObserveAsync(Ct);

            // 防误结束保险：扫描帧上若有确认弹窗（固定智能辅助提示等），先确定再重扫
            if (scan.Ocr.ContainsJoined("固定智能辅助") || scan.Ocr.Contains("确定"))
            {
                Log("活动列表上有确认弹窗：确定后重新扫描", LogLevel.Info);
                await E.Pad.Confirm();
                await E.Pad.Wait(1800, Ct);
                return true;
            }

            // 定位焦点卡：焦点卡由底部「继续」按钮白色高亮唯一标识（活动卡不放大，顶边最高判据失效）
            var card = ChallengeCardReader.LocateActivityFocusedCard(scan.Frame, offX, offY, cliW, cliH);
            if (card is null)
            {
                SaveEvidenceFrame(scan.Frame, "activitynolocate");
                Log("活动卡焦点卡定位失败，结束扫描", LogLevel.Warn);
                return false;
            }
            var (left, _, right, _) = card.Value;
            double cx = (((left + right) / 2.0) - offX) / cliW; // 焦点卡中心（客户区归一化，绿勾/图标据此定位）

            // OCR 文字指纹判「是否真的换卡」（相似卡唯一区别是文字）
            string fp = ActivityFingerprint(scan, cx, offX, offY, cliW, cliH);
            // 判换卡：新指纹必须没在最近 3 张里出现过。末卡 OCR 会在几种读法间震荡
            // （如「二度矶1第弹」↔「1第弹」），仅与上一帧比会一直误判「换卡」→ 幻影卡 + 永不判到最右。
            bool isNewCard = recentFps.Count == 0 || !recentFps.Any(p => SameFingerprint(p, fp));

            if (isNewCard)
            {
                if (recentFps.Count > 0) logicalIdx++;
                recentFps.Add(fp);
                if (recentFps.Count > 3) recentFps.RemoveAt(0);
                stuck = 0;

                // 存证真实活动列表帧（限前 4 张），供离线校准绿勾/焦点卡坐标，避免继续用屏幕截图猜
                if (logicalIdx < 4) SaveEvidenceFrame(scan.Frame, $"act_{logicalIdx}");

                // 绿勾 = 活动已完成 → 拉黑（焦点卡右上角，cx 准确）
                bool hasCheck = HasActivityCheck(scan.Frame, cx);
                if (hasCheck && _doneEvents.Add(logicalIdx))
                    Log($"第 {logicalIdx + 1} 张卡绿勾：已拉黑（活动已完成）", LogLevel.Info);

                // 手柄 + AI 图标 = 可自动打（两次观测兜底聚焦动画）
                bool found = TryFindIconPair(scan.Frame, cx, out double pad, out double ai);
                if (!found)
                {
                    await E.Pad.Wait(350, Ct);
                    scan = await E.ObserveAsync(Ct);
                    found = TryFindIconPair(scan.Frame, cx, out pad, out ai);
                }

                bool banned = IsEventBanned(logicalIdx);
                string bannedBy = _doneEvents.Contains(logicalIdx) ? "绿勾"
                    : _tourBlacklist.Contains(logicalIdx) ? "巡回"
                    : _challengeBlacklist.Contains(logicalIdx) ? "挑战" : "";
                Log($"第 {logicalIdx + 1} 张卡(cx={cx:F3} fp={fp})：绿勾={hasCheck} 手柄白度={pad:F2} AI白度={ai:F2}{(banned ? $"（已拉黑跳过·{bannedBy}）" : "")}", LogLevel.Info);

                if (found && !banned)
                {
                    _currentEventSlot = logicalIdx;
                    Log($"第 {logicalIdx + 1} 张卡手柄+AI 齐全，进入", LogLevel.Success);
                    await E.Pad.Confirm();
                    await E.Pad.Wait(3000, Ct);
                    return true;
                }
            }
            else if (++stuck >= 3)
            {
                Log($"活动列表焦点卡指纹重复 {stuck} 次，确认已到最右（绿勾拉黑 {_doneEvents.Count} / 巡回 {_tourBlacklist.Count} / 挑战 {_challengeBlacklist.Count}）", LogLevel.Warn);
                return false;
            }

            // 右移焦点一步；给足放大动画停稳时间，避免在过渡帧判定
            await E.Pad.Dpad(PadDir.Right);
            await E.Pad.Wait(850, Ct);
        }

        Log("活动列表右扫超出保险上限", LogLevel.Warn);
        return false;
    }

    /// <summary>
    /// 焦点卡区域（cx±0.15、y 0.10~0.60）内 OCR 词按阅读序拼接的指纹。
    /// 相邻相似卡画面几乎一样，唯一区别是标题/编号文字，故用文字判「是否换卡」。
    /// </summary>
    private string ActivityFingerprint(Observation o, double cx, double offX, double offY, double cliW, double cliH)
    {
        var roi = ChallengeCardReader.Map(o.Frame, new NormRect(cx - 0.15, 0.10, 0.30, 0.50), offX, offY, cliW, cliH);
        return string.Concat(o.Ocr.Words
            .Where(w => roi.Contains(w.CenterX, w.CenterY))
            .OrderBy(w => w.CenterY).ThenBy(w => w.CenterX)
            .Select(w => OcrText.Norm(w.Text)));
    }

    /// <summary>两个 OCR 指纹是否指同一张卡：完全相等，或其一被 OCR 漏字成另一指纹的子串（如「二度矶1第弹」↔「1第弹」）。</summary>
    private static bool SameFingerprint(string a, string b)
    {
        if (a == b) return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (Math.Min(a.Length, b.Length) < 3) return false; // 过短不算，防泛化误判
        return a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal);
    }

    // 手柄/AI 图标白度阈值（图标在卡片右上角，相对白按钮中心 cx 的客户区归一化偏移，偏移为 ROI 左边缘）。
    // 手柄为实心块实测 0.29~0.30；AI 为细圆环+字母实测 0.11~0.14；背景 0。
    // 真机帧实测图标中心：手柄≈cx+0.068、AI≈cx+0.098、绿勾≈cx+0.133（旧 0.08/0.125 偏右，AI 落到绿勾上漏判）。
    // 手柄白度阈值：窗口化实测实心块 0.29~0.30，全屏下 ROI 微偏可能掉到 0.18；
    // 降至 0.12 保留余量，AI 阈值 0.06 仍作第二道门控防假阳性。
    private const double PadIconMin = 0.12;
    private const double AiIconMin = 0.06;

    // 图标位移容限（客户区归一化，覆盖白按钮 cx 抖动约 ±0.011 与放大动画；分辨率无关）
    private static readonly double[] SearchSX = { -0.015, 0.0, 0.015 };
    private static readonly double[] SearchSY = { -0.007, 0.0, 0.007 };

    // 活动卡「已完成」绿勾检测：真实帧像素实测绿勾统一在卡右上角（x 偏移 cx+0.11~0.18、y 0.12~0.24）。
    private const int ActivityCheckMinPixels = 200;

    private bool HasActivityCheck(FrameData f, double cx)
    {
        var roi = ToFrameRoi(f, new NormRect(cx + 0.09, 0.11, 0.13, 0.15));
        return FrameAnalyzer.CountBrightGreen(f, roi) >= ActivityCheckMinPixels;
    }

    /// <summary>帧像素 → 帧归一化 ROI（自动裁剪到帧内）。</summary>
    private static NormRect PxRoi(FrameData f, int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Math.Max(0, f.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, f.Height - 1));
        w = Math.Clamp(w, 1, f.Width - x);
        h = Math.Clamp(h, 1, f.Height - y);
        return new NormRect(x / (double)f.Width, y / (double)f.Height, w / (double)f.Width, h / (double)f.Height);
    }

    /// <summary>槽位是否已被任何黑名单登记（主页面绿勾 / 巡回完成 / 挑战完成 三合一）。</summary>
    private bool IsEventBanned(int logicalIdx)
        => _doneEvents.Contains(logicalIdx)
           || _tourBlacklist.Contains(logicalIdx)
           || _challengeBlacklist.Contains(logicalIdx);

    /// <summary>
    /// 在焦点卡右上角搜索「手柄 + AI」双图标：用焦点卡中心 cx + 客户区归一化偏移（分辨率无关），
    /// 而非 LocateFocusedCardEx 不可靠的 top/bottom。返回最优位置的白度。
    /// </summary>
    private bool TryFindIconPair(FrameData f, double cx, out double bestPad, out double bestAi)
    {
        bool ok = false;
        bestPad = 0; bestAi = 0;
        foreach (double sx in SearchSX)
        {
            foreach (double sy in SearchSY)
            {
                double pad = WhiteRatio(f, ToFrameRoi(f, new NormRect(cx + 0.059 + sx, 0.200 + sy, 0.019, 0.020)));
                double ai = WhiteRatio(f, ToFrameRoi(f, new NormRect(cx + 0.089 + sx, 0.200 + sy, 0.019, 0.020)));
                // 记录「两者都尽量大」的位置用于日志
                if (Math.Min(pad, ai) > Math.Min(bestPad, bestAi)) { bestPad = pad; bestAi = ai; }
                if (pad >= PadIconMin && ai >= AiIconMin) ok = true;
            }
        }
        return ok;
    }

    /// <summary>小区域逐像素白色占比（v≥0.75 且 s≤0.18）。</summary>
    private static double WhiteRatio(FrameData f, NormRect r) => ColorRatioEx(f, HsvFilter.White, r);

    /// <summary>
    /// 难度选择页：难度继承上次选择，焦点已在当前难度卡上，直接 A 确认。
    /// 个别版本右下角提示「RT 确定」，A 无效时补按右扳机。
    /// </summary>
    private async Task ConfirmDifficultyAsync()
    {
        Log("赛前难度页：继承上次难度，确认", LogLevel.Info);
        await E.Pad.Confirm();
        await E.Pad.Wait(2800, Ct);

        var after = await E.ObserveAsync(Ct);
        if (!after.Is(GameScreen.PreMatch)) return;

        Log("A 未确认难度，按 RT（页面提示 RT 确定）", LogLevel.Warn);
        await E.Pad.RT();
        await E.Pad.Wait(3000, Ct);
    }

    /// <summary>比赛进行中：复用通用比赛流程（周期 A 跳过回放/弹窗，等待结算），结束后计数。</summary>
    private async Task PlayTourMatchAsync()
    {
        var run = await PlayOneMatchAsync(keyMatch: false, forfeit: false);
        if (run.Completed)
        {
            E.Stats.EventsDone++;
            E.Stats.MatchesDone++;
            E.ResetUnknown();
            Log($"巡回赛第 {E.Stats.EventsDone} 场完成。", LogLevel.Success);
        }
    }

    // ================= 匹配中 = 通用开赛信号 =================

    /// <summary>
    /// 当前 Loading 是否为「匹配中」（而非 KONAMI 转圈/黑屏加载）。
    /// OCR 常把中文逐字拆开，用全文拼接匹配。
    /// </summary>
    private static bool IsMatchmaking(Observation o)
        => o.Ocr.ContainsJoined("匹配中") || o.Ocr.ContainsJoined("搜索对手")
           || o.Ocr.ContainsJoined("寻找对手") || o.Ocr.ContainsJoined("正在匹配")
           || o.Ocr.ContainsJoined("对手连接") || o.Ocr.ContainsJoined("连接对手")
           || o.Ocr.ContainsJoined("已找到对手") || o.Ocr.ContainsJoined("匹配对手")
           || o.Ocr.Contains("matching") || o.Ocr.Contains("searching");

    // ================= 挑战赛 =================

    // 教练模式 toggle 绿色填充区（客户区归一化；1280×720 截图实测绿区 x0.795~0.826 y0.064~0.090，
    // 避开右端白色旋钮）。ON=绿填充 RGB(53,198,92)，OFF=灰底绿度≈0
    private static readonly NormRect CoachToggleGreenRoi = new(0.794, 0.062, 0.034, 0.030);
    private const double CoachGreenMin = 0.25;

    /// <summary>
    /// 挑战清单选卡（焦点卡由 ChallengeCardReader 定位，移动一律签名门控等停稳，不写死卡数）：
    /// ① Collapse：Left 逐次移动到最左（签名不变=到边）；
    /// ② SweepRight：右扫读卡——锁卡即停（锁卡永远是后缀）并 Left 退回；右移到尽头后末卡未锁无勾直接进；
    /// ③ FindPlayable：从最右未锁卡向左找「未锁且未打勾」卡并直接 A 进入；到最左仍无 → 信任左扫结果拉黑退出。
    /// （已按用户要求精简：删除 VerifyDone 反向全程复核与 Enter 进入前复核。）
    /// </summary>
    private async Task HandleChallengeListAsync()
    {
        // 弹窗护栏：确认类提示（固定智能辅助 / 控制选项已更改 / 参加新的挑战）先处理
        var guard = await E.ObserveAsync(Ct);
        if (await HandleChallengePopupAsync(guard)) return;

        switch (_chalStep)
        {
            case ChalStep.Collapse:
            {
                _moveCount = 0;
                _edgeTries = 0;
                // 「从 x+1 开始」加速：焦点卡未锁（已完成带勾=完成前缀，或可打无勾=下一张要打的卡），
                // 或卡号 ≤ 刚完成的 x，都说明目标「最右未锁未勾卡」在当前或其右侧，无需回最左。
                // 锁卡（后缀）、首次进入（x=null）、定位失败 → 保守 Left 回最左。
                var (probe, _, _) = await CaptureCardAsync();
                bool skipCollapse = probe.Unlocked
                    || (_lastCompletedCardNumber.HasValue
                        && probe.Number.HasValue
                        && probe.Number.Value <= _lastCompletedCardNumber.Value);
                if (skipCollapse)
                {
                    Log($"挑战清单：焦点卡号 {(probe.Number?.ToString() ?? "?")} 未锁/在完成区（做完 {_lastCompletedCardNumber}），跳过回最左直接右扫", LogLevel.Info);
                    _chalStep = ChalStep.SweepRight;
                    return;
                }
                Log("挑战清单：收拢到最左卡", LogLevel.Info);
                while (_moveCount < 10)
                {
                    bool moved = await MoveAndSettleAsync(PadDir.Left);
                    if (!moved) break;
                    _moveCount++;
                }
                _chalStep = ChalStep.SweepRight;
                return;
            }

            case ChalStep.SweepRight:
            {
                var (c, frame, _) = await CaptureCardAsync();
                if (!c.Located)
                {
                    SaveEvidenceFrame(frame, "nolocate");
                    await RestartOrLeaveAsync("右扫时焦点卡定位失败");
                    return;
                }
                Log($"挑战清单：卡号={(c.Number?.ToString() ?? "?")} 锁={c.LockRatio:F3} ✓绿={c.GreenRatio:F3}", LogLevel.Info);

                if (!c.Unlocked)
                {
                    // 锁卡=后缀：Left 退到最右未锁卡，进入目标查找
                    Log("当前卡已上锁：Left 退到最右未锁卡", LogLevel.Info);
                    await MoveAndSettleAsync(PadDir.Left);
                    _chalStep = ChalStep.FindPlayable;
                    return;
                }

                bool moved = await MoveAndSettleAsync(PadDir.Right);
                if (moved)
                {
                    _edgeTries = 0;
                    _moveCount++;
                    if (_moveCount < 12) return;
                    Log("右扫达保险上限，以当前卡为最右卡", LogLevel.Warn);
                }
                else
                {
                    // 轮播小步滚动可能低于签名阈值造成假边缘：连续 2 次推不动才当真到边
                    _edgeTries++;
                    if (_edgeTries < 2)
                    {
                        Log($"疑似右边缘，继续右移确认（{_edgeTries}/2）", LogLevel.Info);
                        return;
                    }
                    _edgeTries = 0;
                    Log("连续 2 次右移未动，确认已到右边缘", LogLevel.Info);
                }
                // 尽头硬门控：重新读取当前末卡（保险上限时最后一次移动可能已换卡）。
                // 末卡未锁且无绿勾 → 必须继续打，直接选为目标；有绿勾才 Left 退回进入向左回找。
                var (last, _, _) = await CaptureCardAsync();
                if (last.Located && last.Unlocked && !last.Done)
                {
                    Log($"末卡（卡号={(last.Number?.ToString() ?? "?")}）无绿勾：选为目标继续打", LogLevel.Success);
                    await EnterTargetAsync(last);
                    return;
                }
                await MoveAndSettleAsync(PadDir.Left);
                _chalStep = ChalStep.FindPlayable;
                return;
            }

            case ChalStep.FindPlayable:
            {
                var (c, frame, _) = await CaptureCardAsync();

                if (c.Located && c.Unlocked && !c.Done)
                {
                    Log($"找到目标卡（卡号={(c.Number?.ToString() ?? "?")}）", LogLevel.Success);
                    await EnterTargetAsync(c);
                    return;
                }

                // 跳过本卡：写明具体原因，便于从日志还原是哪一项异常
                string why = !c.Located ? "未定位到焦点卡"
                    : !c.Unlocked ? $"已上锁（锁={c.LockRatio:F3}）"
                    : $"已打勾（✓绿={c.GreenRatio:F3}）";
                Log($"跳过卡 {(c.Number?.ToString() ?? "?")}：{why}", LogLevel.Info);

                bool moved = await MoveAndSettleAsync(PadDir.Left);
                if (moved) return; // 继续向左找

                // 已到最左仍无可打卡：信任左扫结果（已删 VerifyDone 反向全程复核），拉黑退出
                SaveEvidenceFrame(frame, "alldone");
                Log("已到最左无可打卡：确认本活动全部完成，拉黑退出", LogLevel.Success);
                await ExitChallengeAsync();
                return;
            }
        }
    }

    /// <summary>选定目标卡后直接 A 进入，并轮询处理「参加新的挑战?」覆盖弹窗（Up → 进入）。不再进入前重读。</summary>
    private async Task EnterTargetAsync(ChallengeCardReader.Info c)
    {
        _currentCardNumber = c.Number; // 记下将要打的关卡号（清单屏 OCR 可靠），供完成后定位 x+1
        Log($"进入选定挑战卡（卡号={(c.Number?.ToString() ?? "?")}）", LogLevel.Info);
        await E.Pad.Confirm();
        await E.Pad.Wait(1400, Ct);
        // 「参加新的挑战?」覆盖弹窗：默认焦点在「取消」，需 Up → A 点「进入」；轮询 3 次兜底
        for (int i = 0; i < 3; i++)
        {
            var p = await E.ObserveAsync(Ct);
            if (!await HandleChallengePopupAsync(p)) break;
        }
        ResetChalState();
    }

    /// <summary>
    /// 向一个方向推进并等轮播停稳，返回是否真的移动。
    /// 推动协议（防止单次输入被吞造成假边缘）：最多 2 次推动——第 1 次右摇杆满推、
    /// 第 2 次方向键兜底；每次推动后轮询签名 0.5s，MAD≥MoveMadMin 即确认移动，
    /// 随后等签名稳定（最多 1s）。2 次推动都纹丝不动才判定到边（返回 false）。
    /// </summary>
    private async Task<bool> MoveAndSettleAsync(PadDir dir)
    {
        var (_, frame0, geo) = await CaptureCardAsync();
        byte[] sig0 = ChallengeCardReader.Signature(frame0, geo.offX, geo.offY, geo.cliW, geo.cliH);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt < 1) await E.Pad.RsFlick(dir);
            else await E.Pad.Dpad(dir);

            byte[] sig = sig0;
            bool changed = false;
            // 移动判稳直接用最新帧（LatestFrame 30fps 持续更新），不经过 OCR 900ms 节流
            for (int waited = 0; waited < 500; waited += 150)
            {
                await E.Pad.Wait(150, Ct);
                var f = E.LatestFrame;
                if (f is null) continue;
                sig = ChallengeCardReader.Signature(f, geo.offX, geo.offY, geo.cliW, geo.cliH);
                if (ChallengeCardReader.SignatureMad(sig0, sig) >= ChallengeCardReader.MoveMadMin)
                {
                    changed = true;
                    break;
                }
            }
            if (!changed) continue; // 本次推动未动：换下一种推动方式再试

            // 已移动：等签名稳定（滚动动画结束）
            int stable = 0;
            for (int waited = 0; waited < 1000; waited += 150)
            {
                await E.Pad.Wait(150, Ct);
                var f = E.LatestFrame;
                if (f is null) continue;
                byte[] next = ChallengeCardReader.Signature(f, geo.offX, geo.offY, geo.cliW, geo.cliH);
                if (ChallengeCardReader.SignatureMad(sig, next) < ChallengeCardReader.MoveMadMin)
                {
                    if (++stable >= 1) return true;
                }
                else
                {
                    stable = 0;
                }
                sig = next;
            }
            Log("移动后等待稳定超时：按当前帧继续", LogLevel.Warn);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 扫描异常的确定性恢复：重试预算内回 Collapse 整轮重扫；
    /// 超出则不拉黑退出活动（外层会重新进入再试），并提示回传异常帧。
    /// </summary>
    private async Task RestartOrLeaveAsync(string reason)
    {
        _retryCount++;
        if (_retryCount <= 2)
        {
            Log($"{reason}：第 {_retryCount} 次整轮重扫", LogLevel.Warn);
            _chalStep = ChalStep.Collapse;
            _moveCount = 0;
            return;
        }
        Log($"{reason}：已重试 {_retryCount - 1} 次仍失败，不拉黑退出活动" +
            "（请回传程序目录 dump 下 chal_*.png 与日志）", LogLevel.Error);
        await ExitChallengeNoBlacklistAsync();
    }

    /// <summary>帧存证到程序目录 dump\chal_{tag}_HHmmss.png（异常帧/全完成帧通用），供用户回传校准。</summary>
    private void SaveEvidenceFrame(FrameData f, string tag)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "dump");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"chal_{tag}_{DateTime.Now:HHmmss}.png");
            var wb = new WriteableBitmap(f.Width, f.Height, 96, 96, PixelFormats.Bgra32, null);
            wb.WritePixels(new Int32Rect(0, 0, f.Width, f.Height), f.Bgra, f.Width * 4, 0);
            using var fs = File.OpenWrite(path);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(wb));
            enc.Save(fs);
            Log($"帧已存证：{path}", LogLevel.Warn);
        }
        catch (Exception ex)
        {
            Log($"帧存证失败：{ex.Message}", LogLevel.Debug);
        }
    }

    /// <summary>抓帧并读取焦点卡，同时返回帧与几何参数（供签名比对）。</summary>
    private async Task<(ChallengeCardReader.Info info, FrameData frame,
        (double offX, double offY, double cliW, double cliH) geo)> CaptureCardAsync()
    {
        var o = await E.ObserveAsync(Ct);
        double offX = 8, offY = 31, cliW = 1280, cliH = 720;
        if (E.Window is not null)
        {
            var g = GameWindow.GetWindowFrame(E.Window.Handle);
            if (g.CliW > 0 && g.CliH > 0)
                (offX, offY, cliW, cliH) = (g.OffX, g.OffY, g.CliW, g.CliH);
        }
        var info = ChallengeCardReader.Read(o.Frame, o.Ocr, offX, offY, cliW, cliH);
        return (info, o.Frame, (offX, offY, cliW, cliH));
    }

    /// <summary>重置选卡导航扫描状态，下次清单访问重新收拢。</summary>
    private void ResetChalState()
    {
        _chalStep = ChalStep.Collapse;
        _moveCount = 0;
        _retryCount = 0;
        _edgeTries = 0;
    }

    /// <summary>挑战完成退出：拉黑当前活动槽位，B 逐级退回活动列表（清单 → 详情 → 列表）。</summary>
    private async Task ExitChallengeAsync()
    {
        if (_currentEventSlot >= 0)
        {
            _challengeBlacklist.Add(_currentEventSlot);
            Log($"活动卡 #{_currentEventSlot + 1} 已拉黑（挑战全部完成），本次运行不再进入", LogLevel.Success);
            _currentEventSlot = -1;
        }
        _currentCardNumber = null;
        _lastCompletedCardNumber = null; // 整活动结束，下一个活动从零开始
        await LeaveChallengeListAsync();
    }

    /// <summary>异常退出：不拉黑活动槽位（外层可重新进入再试），仅 B 退回活动列表。</summary>
    private async Task ExitChallengeNoBlacklistAsync()
        => await LeaveChallengeListAsync();

    private async Task LeaveChallengeListAsync()
    {
        ResetChalState();
        for (int i = 0; i < 4; i++)
        {
            Ct.ThrowIfCancellationRequested();
            await E.Pad.Back();
            await E.Pad.Wait(2000, Ct);
            var o = await E.ObserveAsync(Ct);
            o = await PassThroughAsync(o);
            if (o.Is(GameScreen.EventHub)) return;
        }
        Log("挑战退出后未回到活动列表，交外层恢复", LogLevel.Warn);
    }

    /// <summary>
    /// 挑战赛主页（前往比赛 / 教练模式 RS 开关 / 胜场 x/x / 进度 x/x）：
    /// ① 任何弹窗一律先点「确定」（智能辅助提示 / 控制选项已更改）；
    /// ② 胜场已达标 → 本卡完成，B 回挑战清单；比赛次数耗尽仍未胜 → 拉黑本活动退出；
    /// ③ 保证教练模式为开（绿色激活），否则按 RS（右摇杆按下）激活并确认变更弹窗；
    /// ④ Left 归位「前往比赛」→ A 开赛（随后「匹配中」触发比赛流程）。
    /// </summary>
    private async Task HandleChallengeHomeAsync()
    {
        // ① 弹窗清扫（参加新的挑战 / 控制选项已更改 / 固定智能辅助等确认类弹窗）
        for (int i = 0; i < 6; i++)
        {
            Ct.ThrowIfCancellationRequested();
            var p = await E.ObserveAsync(Ct);
            if (!await HandleChallengePopupAsync(p)) break;
        }

        var o = await E.ObserveAsync(Ct);
        if (!o.Is(GameScreen.ChallengeHome)) return; // 弹窗关闭后已跳转，交回外层

        // ② 进度读取（仅日志；活动信息区「进度 n/3」，原始词拼接防 OCR 拆字）
        var (tryA, tryB) = ReadRatioPair(o, 0.33, 0.48, 0.40, 0.53);
        Log($"挑战赛主页：进度 {(tryB > 0 ? $"{tryA}/{tryB}" : "未识别")}", LogLevel.Info);

        // ③ 奖励门控（用户指定的主要判定点，「前往比赛」灰/白不可靠、不作判定依据）：
        //   a)「首次以后的奖励」=主要奖励早已领取 → 本卡已完成，B 回挑战清单选下一张；
        //   b)「主要奖励」行右侧有绿色圆勾（实测有勾0.146/无勾0，阈值0.05）→ 同上；
        //   c)「主要奖励」无勾 → 当前卡需要打，继续；
        //   d) 两者都读不到 → 存帧 Warn，保守继续（误打代价远小于误退出）。
        if (o.Ocr.ContainsJoined("首次以后的奖励"))
        {
            MarkCurrentCardCompleted("「首次以后的奖励」");
            await BackToChallengeListAsync();
            return;
        }
        if (o.Ocr.ContainsJoined("主要奖励"))
        {
            bool hasCheck = HasMainRewardCheck(o, RewardCheckRoi, RewardCheckMin, out var checkEvidence);
            Log($"主要奖励对勾：{(hasCheck ? "有" : "无")}（{checkEvidence}）", LogLevel.Info);
            if (hasCheck)
            {
                MarkCurrentCardCompleted("主要奖励已打勾");
                await BackToChallengeListAsync();
                return;
            }
        }
        else
        {
            SaveEvidenceFrame(o.Frame, "rewardunknown");
            Log("奖励区未读到「主要奖励」（也非「首次以后的奖励」）：保守继续开赛（帧已存证）", LogLevel.Warn);
        }

        // ④ 教练模式必须为开（AI 操控）
        await EnsureCoachModeAsync();

        // ⑤ 前往比赛（Left 归位防焦点停在右侧 i 图标）
        Log("挑战赛主页：前往比赛", LogLevel.Info);
        await E.Pad.Dpad(PadDir.Left);
        await E.Pad.Wait(250, Ct);
        await E.Pad.Dpad(PadDir.Left);
        await E.Pad.Wait(250, Ct);
        await E.Pad.Confirm();
        await E.Pad.Wait(2500, Ct);
    }

    /// <summary>确保右上角教练模式 toggle 绿色激活；未激活按 RS（右摇杆按下）开启并确认变更弹窗。</summary>
    private async Task EnsureCoachModeAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Ct.ThrowIfCancellationRequested();
            var o = await E.ObserveAsync(Ct);
            double green = CoachToggleGreenScore(o);
            Log($"教练模式开关峰值绿占比={green:F3}", LogLevel.Info);
            if (green >= CoachGreenMin)
            {
                Log("教练模式已激活（绿色）", LogLevel.Debug);
                return;
            }
            Log("教练模式未激活，按 RS 开启", LogLevel.Info);
            await E.Pad.R3();
            await E.Pad.Wait(1200, Ct);
            // 切换后弹「控制选项已更改」→ 确定
            var p = await E.ObserveAsync(Ct);
            if (p.Ocr.Contains("确定"))
            {
                await E.Pad.Confirm();
                await E.Pad.Wait(1500, Ct);
            }
        }
        Log("教练模式激活状态多次确认失败，保持当前继续", LogLevel.Warn);
    }

    /// <summary>
    /// 教练模式开关绿度评分（OCR 锚定，宽高比无关）：
    /// 找「教练模式」文字，扫其右侧同行条带，取逐列绿色计数的峰值占条带高度比。
    /// 开=绿色填充开关柱（峰值 ≈0.5~0.9）；关=灰色（≈0）。OCR 失败回退旧固定 ROI 绿度。
    /// </summary>
    private double CoachToggleGreenScore(Observation o)
    {
        var word = o.Ocr.FindFirst("教练模式");
        if (word is null)
            return ColorRatioEx(o.Frame, HsvFilter.Green, ToFrameRoi(o.Frame, CoachToggleGreenRoi));

        var f = o.Frame;
        double x0 = word.X + word.W + 0.005;
        double y0 = Math.Max(0, word.Y - 0.008);
        if (x0 >= 1.0) return 0;
        var strip = new NormRect(x0, y0, Math.Min(0.13, 1.0 - x0), Math.Min(1.0 - y0, word.H + 0.016));
        var (px, py, pw, ph) = FrameAnalyzer.PixelRect(f, strip);
        if (pw < 8 || ph < 6) return 0;

        var b = f.Bgra;
        int peak = 0;
        for (int xx = px; xx < px + pw; xx++)
        {
            int cnt = 0;
            for (int yy = py; yy < py + ph; yy++)
            {
                int i = yy * f.Width * 4 + xx * 4;
                if (FrameAnalyzer.MatchHsv(b[i + 2], b[i + 1], b[i], HsvFilter.Green)) cnt++;
            }
            peak = Math.Max(peak, cnt);
        }
        return peak / (double)ph;
    }

    // ================= 比赛中：跳过比赛快速结束 =================

    /// <summary>主要奖励行右侧绿色圆勾（客户区归一化；真机实测有勾0.146/无勾0.000）。</summary>
    private static readonly NormRect RewardCheckRoi = new(0.935, 0.472, 0.048, 0.065);
    private const double RewardCheckMin = 0.05;

    /// <summary>
    /// 直播中右上 HUD「跳过比赛」药丸右端的绿色圆勾（客户区归一化）。
    /// 条件达成时药丸为「≡ 跳过比赛 ✓」、无「累积进球」字样（真机实测亮绿 375/区域约 5056）；
    /// 未达成时该区域亮绿仅 8。阈值 ≥100。
    /// </summary>
    private static readonly NormRect SkipCheckRoi = new(0.945, 0.021, 0.050, 0.110);
    private const int SkipCheckMinPixels = 100;

    /// <summary>
    /// 主页奖励门控判定当前关卡已完成：把 Enter 步骤记下的卡号转移到 _lastCompletedCardNumber（=x）。
    /// 只消费 Enter 时的记忆值，绝不在此（主页屏幕）重新读卡——那是上一版误拉黑第五章的根因。
    /// </summary>
    private void MarkCurrentCardCompleted(string reason)
    {
        _lastCompletedCardNumber = _currentCardNumber;
        Log(_currentCardNumber.HasValue
            ? $"关卡 {_currentCardNumber} 完成（{reason}），下一张从 {_currentCardNumber + 1} 开始判定"
            : $"关卡完成（{reason}），但 Enter 时卡号未读到，无法定位 x+1", LogLevel.Info);
    }

    /// <summary>挑战赛主页 → B 回挑战清单（不拉黑活动），并重置选卡状态让清单重新收拢扫描。</summary>
    private async Task BackToChallengeListAsync()
    {
        await E.Pad.Back();
        await E.Pad.Wait(2000, Ct);
        ResetChalState();
    }

    /// <summary>
    /// 比赛中的场景特殊动作，按优先级：
    /// ①「确认跳过」二次确认弹窗：Up 移焦点到「确认跳过」→ A；
    /// ② 底部 RT 蓝色横幅（前往比赛/跳过）：纯视觉检测，立即 A（不依赖 OCR）；
    /// ③ 误开的「累计进球 n 个 / 关闭」条件弹窗：A 关闭 → B 回比赛；
    /// ④ 暂停菜单：跳过卡发光才 A，灰「查看条件」一律 B 回比赛；
    /// ⑤ 直播中：HUD 绿勾「≡ 跳过比赛 ✓」（条件达成）立即开菜单跳过；
    ///    旧文本「累积进球 n/3」n≥3 同样处理；n<3/读不到/无勾=干扰项忽略。
    /// 任何环节都不停留在菜单、绝不盲目按 A。
    /// </summary>
    protected override async Task<bool> TrySpecialInMatchActionAsync(Observation o)
    {
        // ⓪ 挑战活动弹窗（参加新的挑战 / 控制选项已更改等）：清单导航与进比赛后都可能出现
        if (await HandleChallengePopupAsync(o)) return true;

        // ① 二次确认弹窗（全文拼接兼容「确认/跳过」被 OCR 拆成两个词）
        if (o.Ocr.ContainsJoined("确认跳过"))
        {
            await ConfirmSkipPopupAsync();
            return true;
        }

        // ② RT 蓝色横幅（「RT 前往比赛」「RT 跳过」）：纯视觉检测，立即 A
        if (o.Frame is not null && FrameAnalyzer.FindRtBanner(o.Frame, out _))
        {
            Log("检测到底部 RT 蓝横幅：立即按 A", LogLevel.Info);
            await E.Pad.Confirm();
            await E.Pad.Wait(1200, Ct);
            return true;
        }

        // ③ 误开的跳过条件弹窗（白弹窗「累计进球3个 / 关闭」，关闭默认聚焦）：A 关闭 → B 回比赛
        if (o.Ocr.Contains("关闭") &&
            (o.Ocr.ContainsJoined("累计进球") || o.Ocr.ContainsJoined("累积进球")))
        {
            Log("跳过条件弹窗：点「关闭」→ B 返回比赛", LogLevel.Info);
            await E.Pad.Confirm();
            await E.Pad.Wait(1200, Ct);
            await E.Pad.Back();
            await E.Pad.Wait(2000, Ct);
            return true;
        }

        // ④ 暂停菜单已打开：跳过卡发光才 A；灰「查看条件」/定位失败一律 B，绝不按 A
        bool inPauseMenu = o.Ocr.Contains("查看条件")
            || (o.Ocr.Contains("比赛回放") && o.Ocr.Contains("重新开始"));
        if (inPauseMenu)
            return await ConsumePauseMenuAsync(o);

        // ⑤ 直播中跳过比赛：
        // 5a 条件已达成 → HUD 药丸右端出现绿色圆勾「≡ 跳过比赛 ✓」（无累积进球字样）：开菜单跳过
        // 5b 兼容旧文本：HUD「累积进球 n/3」且 n≥3 也开菜单
        // n<3/读不到/无勾 = 干扰识别项，忽略继续比赛
        if (o.Frame is not null && HudHasSkipText(o) &&
            FrameAnalyzer.CountBrightGreen(o.Frame, ToFrameRoi(o.Frame, SkipCheckRoi)) >= SkipCheckMinPixels)
        {
            Log("HUD「跳过比赛」绿勾已出现：打开暂停菜单跳过比赛", LogLevel.Info);
            await E.Pad.Start();
            await E.Pad.Wait(2000, Ct);
            var menu = await E.ObserveAsync(Ct);
            return await ConsumePauseMenuAsync(menu);
        }
        if (HasSkipHudPill(o))
        {
            int? n = ReadHudGoalProgress(o);
            if (n is >= 3)
            {
                Log($"HUD 累积进球 {n}/3：打开暂停菜单尝试跳过比赛", LogLevel.Info);
                await E.Pad.Start();
                await E.Pad.Wait(2000, Ct);
                var menu = await E.ObserveAsync(Ct);
                return await ConsumePauseMenuAsync(menu);
            }
            Log($"HUD 跳过条件未达成（{(n?.ToString() ?? "?")}/3）：忽略继续比赛", LogLevel.Debug);
        }
        return false;
    }

    /// <summary>
    /// 暂停菜单消费：找到「跳过比赛」卡且发光（条件达成）才 A → 二次确认弹窗；
    /// 其他一切情况（灰「查看条件」/找不到词/未发光）一律 B 返回比赛，绝不按 A。
    /// </summary>
    private async Task<bool> ConsumePauseMenuAsync(Observation menu)
    {
        if (menu.Ocr.FindFirst("跳过比赛") is { } w && IsButtonGlowing(menu, w))
        {
            Log("暂停菜单：「跳过比赛」可用，确认", LogLevel.Success);
            await E.Pad.Confirm();
            await E.Pad.Wait(1500, Ct);
            await ConfirmSkipPopupAsync();
            return true;
        }
        Log("暂停菜单：跳过比赛未发光（条件未达成），B 返回比赛继续", LogLevel.Info);
        await E.Pad.Back();
        await E.Pad.Wait(2000, Ct);
        return true;
    }

    /// <summary>
    /// 右上 HUD 区域词（CenterX>0.78、CenterY<0.14）拼接：先按 Y 聚类成行
    /// （同属一行的字 Y 可有 ~0.01 抖动，直接按 Y 排序会把「跳过比赛」打乱成「比过跳赛」），
    /// 行内按 X 排序、行间按行 Y 排序。
    /// </summary>
    private static string HudJoined(Observation o)
    {
        var words = o.Ocr.Words
            .Where(w => w.CenterX > 0.78 && w.CenterY < 0.14)
            .OrderBy(w => w.CenterY)
            .ToList();
        if (words.Count == 0) return "";

        var lines = new List<List<OcrWord>>();
        foreach (var w in words)
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(w.CenterY - line.Average(x => x.CenterY)) <= 0.02)
                line.Add(w);
            else
                lines.Add(new List<OcrWord> { w });
        }

        var sb = new StringBuilder();
        foreach (var line in lines.OrderBy(l => l.Average(x => x.CenterY)))
            foreach (var w in line.OrderBy(x => x.CenterX))
                sb.Append(OcrText.Norm(w.Text));
        return sb.ToString();
    }

    /// <summary>右上 HUD 是否含「跳过比赛」（OCR 逐字拆开也能拼中）。</summary>
    private static bool HudHasSkipText(Observation o) => HudJoined(o).Contains("跳过比赛");

    /// <summary>
    /// 是否存在右上 HUD 跳过条件提示（客户区右上角 x>0.78,y<0.14）：
    /// 「跳过比赛」与「累积进球/累计进球」配对出现即 HUD 提示（不是可点按钮）。
    /// </summary>
    private static bool HasSkipHudPill(Observation o)
    {
        string joined = HudJoined(o);
        return joined.Contains("跳过比赛")
            && (joined.Contains("累积进球") || joined.Contains("累计进球"));
    }

    /// <summary>
    /// 读右上 HUD 内「累积进球 n/3」的 n；分隔符漏识时取「进球」后的第一个数字。
    /// 读不到返回 null（调用方按干扰项忽略，绝不因此开菜单）。
    /// </summary>
    private static int? ReadHudGoalProgress(Observation o)
    {
        var hud = o.Ocr.Words
            .Where(w => w.CenterX > 0.78 && w.CenterY < 0.14)
            .OrderBy(w => w.CenterY).ThenBy(w => w.X).ToList();
        string joined = string.Concat(hud.Select(w => OcrText.Norm(w.Text)));
        var m = global::System.Text.RegularExpressions.Regex.Match(joined, @"(\d+)[/|Il]3");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        var m2 = global::System.Text.RegularExpressions.Regex.Match(joined, @"进球(\d+)");
        if (m2.Success) return int.Parse(m2.Groups[1].Value);
        return null;
    }

    /// <summary>
    /// 挑战赛确认类弹窗统一处理，返回 true 表示命中并已按键。
    /// ①「参加新的挑战?」默认焦点在蓝「取消」→ Up 移到「进入」→ A；
    /// ②「控制选项已更改」/固定智能辅助等单按钮（OCR 出现「确定」）→ 直接 A。
    /// 在挑战清单、挑战赛主页、比赛中三处共用。
    /// </summary>
    private async Task<bool> HandleChallengePopupAsync(Observation o)
    {
        if (o.Ocr.ContainsJoined("参加新的挑战") || o.Ocr.ContainsJoined("你可以加入新挑战"))
        {
            Log("弹窗「参加新的挑战?」：Up 移焦点到「进入」→ A", LogLevel.Info);
            await E.Pad.Dpad(PadDir.Up);
            await E.Pad.Wait(450, Ct);
            await E.Pad.Confirm();
            await E.Pad.Wait(2200, Ct);
            return true;
        }
        if (o.Ocr.Contains("确定"))
        {
            Log("确认类弹窗：确定", LogLevel.Info);
            await E.Pad.Confirm();
            await E.Pad.Wait(1600, Ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 「跳过比赛」二次确认弹窗：默认焦点在下方「关闭」，「确认跳过」未聚焦。
    /// 按 Up 把焦点移到「确认跳过」（已在该项时 Up 不动），再 A 确认。
    /// 弹窗已离开则直接返回；确认后复测一次，仍在则补做一轮。
    /// </summary>
    private async Task ConfirmSkipPopupAsync()
    {
        for (int round = 0; round < 2; round++)
        {
            Ct.ThrowIfCancellationRequested();
            var o = await E.ObserveAsync(Ct);
            if (!o.Ocr.ContainsJoined("确认跳过")) return;

            Log("二次确认弹窗：Up 移焦点到「确认跳过」→ A", LogLevel.Info);
            await E.Pad.Dpad(PadDir.Up);
            await E.Pad.Wait(450, Ct);
            await E.Pad.Confirm();
            await E.Pad.Wait(2500, Ct);

            o = await E.ObserveAsync(Ct);
            if (!o.Ocr.ContainsJoined("确认跳过")) return;
        }
        Log("二次确认弹窗两轮后仍未关闭，交回比赛循环后续处理", LogLevel.Warn);
    }

    /// <summary>
    /// 在指定区域（客户区归一化）内解析一组 n/m 比值，解析不到返回 (0,0)。
    /// ① ROI 先经 ToFrameRoi 映射到整窗帧坐标再过滤词；
    /// ② 读法1：拼接后正则（兼容 OCR 把 / 误识为 | I l ╱ 等）；
    /// ③ 读法2：分隔符被完全漏识时，收集行内数字片段按 X 排序取最后两个
    ///   （相邻数字词 x 间隙 ≤0.010 先合并，防多位数被拆；独立两数间间隙约 0.015 不合并）。
    /// </summary>
    private (int A, int B) ReadRatioPair(Observation o, double xMin, double xMax, double yMin, double yMax)
    {
        var fr = ToFrameRoi(o.Frame, new NormRect(xMin, yMin, xMax - xMin, yMax - yMin));
        var words = o.Ocr.Words
            .Where(w => fr.Contains(w.CenterX, w.CenterY))
            .OrderBy(w => w.CenterY).ThenBy(w => w.X)
            .ToList();
        if (words.Count == 0) return (0, 0);

        // 读法1：分隔符词存在（含被误识成 I/l/|）
        string compact = global::System.Text.RegularExpressions.Regex.Replace(
            string.Concat(words.Select(w => w.Text)), @"\s+", "");
        var m = global::System.Text.RegularExpressions.Regex.Match(compact, @"(\d+)[/|Il╱／](\d+)");
        if (m.Success)
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));

        // 读法2：收集纯数字词，行内按 x 间隙合并多位数拆字
        var parts = new List<(string Text, double X0, double X1, double CY)>();
        foreach (var w in words
            .Where(w => OcrText.Norm(w.Text) is { Length: >= 1 } t && t.All(char.IsDigit))
            .OrderBy(w => w.CenterY).ThenBy(w => w.X))
        {
            string t = OcrText.Norm(w.Text);
            if (parts.Count > 0)
            {
                var p = parts[^1];
                bool sameLine = Math.Abs(p.CY - w.CenterY) < Math.Max(0.01, w.H * 0.7);
                if (sameLine && w.X - p.X1 <= 0.010)
                {
                    parts[^1] = (p.Text + t, p.X0, w.X + w.W, p.CY);
                    continue;
                }
            }
            parts.Add((t, w.X, w.X + w.W, w.CenterY));
        }

        if (parts.Count >= 2
            && int.TryParse(parts[^2].Text, out int a)
            && int.TryParse(parts[^1].Text, out int b))
            return (a, b);
        return (0, 0);
    }
}
