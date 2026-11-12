using EfootballBot.Core.Bot.Scenarios.CnWeekly;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.PeakLadder;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios.CnMyLeague;

/// <summary>
/// 国服我的联赛挂机场景：继承国际服我的联赛的完整联赛内流程（主页推进/难度核对/道具购买/
/// 比赛挂机/终场弹窗链/经验值页），仅进入导航与确认键按国服适配——
/// 主页 上×2 进页签行 → 左×4 饱和归位「推荐」→ 右进「比赛」页签 → 下（卡行默认焦点即
/// 天梯巅峰赛，与国服天梯模块实测一致）→ 右×1 到「我的联赛」（与天梯巅峰赛同排右侧，
/// 用户 2026-10-17 实机指认：从比赛高光向下是天梯巅峰赛，还需向右一次）→ A 进入。
/// 国服特有：比赛起始页确认键是 RT 而非 A（天梯巅峰赛 2026-10-14 实机教训，按 A 永远卡在
/// 起始页）；本程序须以管理员运行（国服为提权进程，UIPI 拦低权限注入；手柄走内核不受限）。
/// 判定只用固定 UI 词；主页页签复用国服天梯巅峰赛的特征（推荐|比赛|签约|商店 页签行 +
/// 比赛活动/我的联赛/好友对战 卡片词），难度/道具/停止场次用独立的 CnMyLeague 配置段。
/// </summary>
public sealed class CnMyLeagueScenario : MyLeagueScenario
{
    /// <summary>主页导航失败预算：连续多轮未进入联赛 → 暂停 8s 后重来（防死循环刷屏）。</summary>
    private int _homeAttempts;

    /// <summary>残留页 B 逃生预算：连续多轮未退出 → 暂停 8s 后重来。</summary>
    private int _escapeAttempts;

    /// <summary>难度页确认预算（旧式列表页）：连续多轮未离开难度页 → B 退出重走「前往比赛」（页面重开光标归位）。</summary>
    private int _diffAttempts;

    /// <summary>新式难度页确认轮次（国际服同款 UI）：见 <see cref="DifficultyLegPlan"/>，第 4/7 轮 B 逃生。</summary>
    private int _diffRound;

    /// <summary>最近一轮国服界面判定（ClassifyScreen 产出，InterceptAsync 消费；单线程无竞争）。</summary>
    private PlScreen _pl = PlScreen.None;
    private CwScreen _cw = CwScreen.None;

    /// <summary>关键比赛预告弹窗阶梯轮次（每轮换一招；进难度页/起始页/比赛即清零）。</summary>
    private int _keyMatchRounds;

    /// <summary>上一次见到关键比赛弹窗的时间（超 2 分钟没再见即把阶梯轮次收回第 1 招）。</summary>
    private DateTime _lastKeyMatchPopupAt = DateTime.MinValue;

    /// <summary>后台鼠标（国服客户端部分页面只吃鼠标，见天梯巅峰赛/周活动同款用法）。</summary>
    private readonly BackgroundMouse _mouse = new();

    public CnMyLeagueScenario(BotEngine engine, Action<string, LogLevel> log)
        : base(engine, log)
    {
    }

    public override string Name => "我的联赛（国服）";

    /// <summary>赛后活动任务页仅国际服独享：国服不识别不接管。</summary>
    protected override bool SupportsPostMatchActivity => false;

    /// <summary>只打关键比赛（杀进程跳过普通比赛）仅国际服：国服比赛内有「跳过比赛」菜单项，
    /// 不需要重启游戏。</summary>
    protected override bool SupportsSkipNormalMatch => false;

    /// <summary>国服独立配置段：与国际服我的联赛设置互不影响。</summary>
    protected override MyLeagueConfig LeagueConfig => _engine.Config.CnMyLeague;

    /// <summary>
    /// 国服界面先判：主页页签（天梯巅峰赛 Brain 的页签行特征）与国服周活动残留页命中时
    /// 记 Unknown（由拦截钩子接管，不走国际服共享决策）；全未命中回落国际服判定。
    /// 兜底：OCR 页签行漏读任一词（推荐/签约/商店）导致两个 Brain 都不命中时，国际服判定
    /// 会把主页「比赛」页签误判成国际服式比赛页（标题「比赛」+ 我的联赛磁贴词同构）——
    /// 国服没有该独立页面，此分类只可能来自主页页签，一律按主页导航处理。
    /// </summary>
    protected override MlScreen ClassifyScreen(Observation obs, bool matchContext)
    {
        _pl = PeakLadderBrain.Classify(obs.Ocr);
        _cw = _pl == PlScreen.None
            ? CnWeeklyBrain.Classify(obs.Detections, obs.Ocr)
            : CwScreen.None;
        // PeakLadder「活动主页」的兜底路径（前往比赛+比赛记录）与国服我的联赛主页同构——
        // 国服联赛主页磁贴就叫「比赛记录」（国际服为 赛季纪录/比赛历史），两词同屏会把
        // 联赛主页抢判成天梯活动主页 → 拦截钩子当残留页 B 退回主页死循环
        //（2026-10-17 实机：进主页即被 B 退回）。无天梯专属词不认，交还国际服判定
        //（前往比赛 → LeaguePage → 共享流程正常推进）
        if (_pl == PlScreen.ActivityHome && !HasPeakLadderEvidence(obs.Ocr))
            _pl = PlScreen.None;
        // 国服主页页签/其他模块残留页：记 Unknown 由拦截钩子接管（主页/残留页由 IsCnHome 区分）
        if (_pl != PlScreen.None || _cw != CwScreen.None)
            return MlScreen.Unknown;
        var ml = base.ClassifyScreen(obs, matchContext);
        if (ml == MlScreen.MatchHub)
        {
            _pl = PlScreen.HomeMatch;
            return MlScreen.Unknown;
        }
        return ml;
    }

    /// <summary>
    /// 帧内是否有天梯巅峰赛专属证据：场次行「本周可参赛场次」或任何位置的「天梯巅峰赛」
    /// 字样（活动主页标题 教练天梯巅峰赛/模式选择页卡）。二者皆无时 PeakLadder 的
    /// ActivityHome 只可能来自「前往比赛+比赛记录」兜底——该词组与国服我的联赛主页
    /// 无法区分，不构成天梯证据。
    /// </summary>
    public static bool HasPeakLadderEvidence(OcrResult ocr)
        => ocr.ContainsJoined("本周可参赛场次") || ocr.ContainsJoined("天梯巅峰赛");

    /// <summary>当前帧是否为国服主页（两 Brain 任一判出主页页签，或 YOLO 主菜单兜底）。</summary>
    private bool IsCnHome =>
        _pl is PlScreen.HomeMatch or PlScreen.HomeRecommended ||
        _cw is CwScreen.HomeMatch or CwScreen.HomeRecommended;

    protected override string DescribeScreen(MlScreen screen)
    {
        if (IsCnHome) return _pl != PlScreen.None ? $"国服主页·{_pl}" : "国服主页·比赛页签";
        if (_pl != PlScreen.None) return _pl.ToString();
        if (_cw != CwScreen.None) return _cw.ToString();
        return screen.ToString();
    }

    /// <summary>
    /// 国服自有界面接管（先于共享决策）：主页导航 / 其他模块残留页 B 逃生 /
    /// 起始页 RT 开赛 / 疑似国服 RT 确认页的 A 无进展保险。
    /// </summary>
    protected override async Task<int?> InterceptAsync(Observation obs, MlScreen screen, int streak, bool inMatchRecently)
    {
        // ---- 国服「关键比赛」预告弹窗（按钮=确定/取消，不是国际服的 OK）----
        // 实机 2026-11-11 16:20:32 起死循环：前往比赛 → 弹窗 →「高光在确定」直接 A → 弹窗关掉
        // 回主页 → 再前往比赛 → 又弹，连 60+ 轮零场次。国服这枚弹窗的选中态是青色
        // （取消 L137~146）、白色 255 只是按钮底色，与游戏内手柄 UI 相反，PopupBrain 的
        // 「白底=焦点」规则在这里判反了：A 每次落在下方「取消」上。国际服同款弹窗有 OK 词、
        // 走 KeyMatchDialog→上移+A，国服换成 确定/取消 后两头都不接。这里阶梯换招自愈。
        if ((screen is MlScreen.HasConfirm or MlScreen.KeyMatchDialog or MlScreen.Unknown) &&
            IsCnKeyMatchPopup(obs.Ocr))
            return await HandleCnKeyMatchPopupAsync(obs);

        // 已进入比赛流程（难度页/起始页/比赛/终场）= 弹窗这一关过去了：阶梯轮次清零；
        // 超过 2 分钟没再见到该弹窗（用户手动点过/换了画面）也把阶梯收回第 1 招
        if (screen is MlScreen.DifficultyPage or MlScreen.MatchStartPage or MlScreen.InMatch or MlScreen.FullTime ||
            (_keyMatchRounds > 0 && (DateTime.Now - _lastKeyMatchPopupAt).TotalSeconds > 120))
            _keyMatchRounds = 0;

        // ---- 国服主页：饱和归位导航进「我的联赛」（任意焦点/页签状态皆可达，到头多按无害）----
        if (IsCnHome || screen == MlScreen.MainMenu)
        {
            if (++_homeAttempts > 4)
            {
                _log("[我的联赛] 主页导航多轮未进入我的联赛，暂停 8s 后重试（若反复出现请回报日志）", LogLevel.Error);
                _homeAttempts = 0;
                return 8000;
            }
            // 上×2 进页签行 → 左×4 到最左「推荐」→ 右×1 到「比赛」页签
            // → 下进卡行（默认焦点即天梯巅峰赛）→ 右×1 到「我的联赛」→ A 进入
            _log("[我的联赛] 主页导航：上×2 左×4 归位推荐 → 右进比赛页签 → 下（落天梯巅峰赛）→ 右→A 进我的联赛", LogLevel.Info);
            await _engine.Pad.DpadRepeat(PadDir.Up, 2);
            await _engine.Pad.DpadRepeat(PadDir.Left, 4);
            await _engine.Pad.Dpad(PadDir.Right);
            await _engine.Pad.Dpad(PadDir.Down);
            await _engine.Pad.Dpad(PadDir.Right);
            await _engine.Pad.Confirm();
            return 3200;
        }

        // ---- 其他模块残留页（开始挂机时用户停在天梯巅峰赛/周活动里）：一律 B 退回主页 ----
        if (_pl is PlScreen.ModeSelect or PlScreen.ActivityHome or PlScreen.ModeDialog ||
            _cw is CwScreen.EventsList or CwScreen.EventIntro or CwScreen.Bracket or
                    CwScreen.EventHome or CwScreen.Squad or CwScreen.SquadMenu or
                    CwScreen.EventHomeTour or CwScreen.EventSettings or
                    CwScreen.ChallengeList)
        {
            if (++_escapeAttempts > 6)
            {
                _log("[我的联赛] 残留页面多次未退出，暂停 8s 后重试（若反复出现请回报日志）", LogLevel.Error);
                _escapeAttempts = 0;
                return 8000;
            }
            _log($"[我的联赛] 其他模块残留页面（{DescribeScreen(screen)}），B 退回主页", LogLevel.Info);
            await _engine.Pad.Back();
            return 2400;
        }

        // 离开主页/残留页（进联赛或过场）：预算复位，下次回主页从零计
        _homeAttempts = 0;
        _escapeAttempts = 0;

        // ---- 国服比赛起始页：底栏印「RT 下一步」，但 A 同样生效（用户 2026-11-11 实测：
        //      国服凡是印 RT 的 下一步/确认/确定，按 A 一样推进）。所以默认按 A——与共享流程、
        //      国际服同一条路；只有同一页连续多轮按 A 画面不动（该页被网页层接管等）才换 RT ----
        if (screen == MlScreen.MatchStartPage)
        {
            if (streak < 1) return 800; // 过渡帧防抖
            bool alt = streak >= 3;
            _log(alt
                ? "[我的联赛] 起始页：A 三轮未推进，改按 RT 下一步（底栏所印键）"
                : "[我的联赛] 起始页：A 下一步开赛（底栏印 RT，A 同样生效）", LogLevel.Info);
            if (alt) await _engine.Pad.RT(); else await _engine.Pad.Confirm();
            return alt ? 3400 : 3000;
        }

        // ---- 国服「选择赛事等级」难度页接管。旧版页面（2026-10-18 实机）无「底行 取消/确认」
        //      结构、A=勾选、RT=确定；2026-11-04 实机改版为国际服同款 UI（底行 取消/确认，
        //      确认钮=A 焦点），RT 在该页无绑定。HandleCnDifficultyAsync 按底行结构分派两种配方 ----
        if (screen == MlScreen.DifficultyPage)
            return await HandleCnDifficultyAsync(obs, streak);
        _diffAttempts = 0;

        // ---- 国服确认页兜底：A 是主路径（用户 2026-11-11 实测：国服印 RT 的 下一步/确认/确定
        //      按 A 一样推进），只有 A 连续 10 轮都推不动时才试底栏所印的 RT——少数网页层页面
        //      确实只认真实键位。正常 A 推进的页面每页 1~2 轮即过，到不了 10 轮；赛后奖励弹窗链
        //      虽会累计轮数，但 RT 在弹窗上无绑定，按了无害。
        //      难度页已由上方分支全量接管（含确认与 B 退出自愈），不进本兜底 ----
        if (screen == MlScreen.HasConfirm &&
            streak >= 10 && (streak - 10) % 5 == 0)
        {
            _log($"[我的联赛] {DescribeScreen(screen)} 连续 {streak} 轮 A 无进展，试按底栏所印 RT", LogLevel.Warn);
            await _engine.Pad.RT();
            return 3000;
        }

        // ---- 死页逃生：A/RT 均无进展 30 轮（≈2 分钟）→ B 退出该页自愈 ----
        if (screen == MlScreen.HasConfirm && streak >= 30 && (streak - 30) % 5 == 0)
        {
            _log("[我的联赛] 确认页 A/RT 均无进展，B 退出自愈", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }

        return null;
    }

    // ---------------- 国服「关键比赛」预告弹窗（A 落在取消上，见 InterceptAsync 分支注释）----------------

    /// <summary>
    /// 国服「关键比赛」预告弹窗判定（实机 2026-11-11 帧）：正文固定两段
    /// 「即将进行的是关键比赛」/「下一场比赛有固定的赛事等级。你将获得额外的我的联赛积分和经验值。」，
    /// 按钮是「确定 / 取消」。主页与赛程表从不出现这两句，固定文案即可安全区分；
    /// 整句漏读时用「关键比赛 + 中部/下部有 确定/取消 按钮词」兜底。调用方只在
    /// HasConfirm / KeyMatchDialog / Unknown 帧上调用，主页帧不受影响。
    /// </summary>
    public static bool IsCnKeyMatchPopup(OcrResult ocr)
    {
        if (!ocr.ContainsJoined("关键比赛") && !ocr.ContainsJoined("重要比赛")) return false;
        if (ocr.ContainsJoined("即将进行的是关键比赛") ||
            ocr.ContainsJoined("下一场比赛有固定的赛事等级"))
            return true;
        return ocr.Words.Any(w => w.CenterY is > 0.30 and < 0.92 && w.CenterX is > 0.10 and < 0.90 &&
            (w.NormText.Contains("确定") || w.NormText.Contains("取消")));
    }

    /// <summary>弹窗「确定」按钮词（y 0.30~0.92、x 0.10~0.90 的按钮带；同词多枚取字高最大者）。</summary>
    private static OcrWord? FindKeyMatchConfirmWord(OcrResult ocr)
        => ocr.Words
            .Where(w => w.CenterY is > 0.30 and < 0.92 && w.CenterX is > 0.10 and < 0.90)
            .Where(w =>
            {
                string t = w.NormText.Replace(" ", "");
                return t.Contains("确定") || t.Contains("確認") || t.Contains("确认") ||
                       t.Equals("OK", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(w => w.H)
            .FirstOrDefault();

    /// <summary>
    /// 关键比赛弹窗阶梯处置：每轮只做一招，逐级换手段直到开赛（进难度页/起始页由
    /// InterceptAsync 顶部清零）。实机证据：A 直接按在「取消」上、弹窗关掉回主页，B 退出联赛
    /// 重进后焦点回默认位仍是取消——所以单靠 A/B 永远过不去，必须换输入方式与焦点位置：
    /// ①上×1+A（把光标挪到上方「确定」）②直接 A（不挪光标，原样再试）
    /// ③RT（底栏所印键，A 真不吃的少数网页层页面）④鼠标点「确定」（网页层只吃鼠标的先例）
    /// ⑤下×1+A（反向布局兜底）⑥上×2+A（三键布局饱和到最上）⑦B 关弹窗
    /// ⑧B×2 退出联赛由主页重进 ⑨九轮全败 → 停止并提示手动开赛，绝不无限空按。
    /// </summary>
    private async Task<int> HandleCnKeyMatchPopupAsync(Observation obs)
    {
        int round = ++_keyMatchRounds;
        _lastKeyMatchPopupAt = DateTime.Now;
        var buttons = PopupBrain.FindButtons(obs.Frame, obs.Ocr);
        if (round == 1)
            _log($"[我的联赛] 关键比赛弹窗（国服）：按钮 {PopupBrain.Describe(buttons)}——" +
                 "难点：国服这枚弹窗的选中态是青色（下方「取消」L≈140），白色 255 的「确定」只是底色，A 每次落在取消上", LogLevel.Warn);

        switch (round)
        {
            case 1:
                _log("[我的联赛] 关键比赛弹窗 ①：上×1 把光标挪到上方「确定」→ A", LogLevel.Warn);
                await _engine.Pad.Dpad(PadDir.Up);
                await _engine.Pad.Confirm();
                return 2600;

            case 2:
                _log("[我的联赛] 关键比赛弹窗 ②：直接 A（不挪光标，原样再试）", LogLevel.Warn);
                await _engine.Pad.Confirm();
                return 2600;

            case 3:
                _log("[我的联赛] 关键比赛弹窗 ③：A 两轮未推进，改按 RT（底栏所印键，网页层兜底）", LogLevel.Warn);
                await _engine.Pad.RT();
                return 2600;

            case 4:
            {
                var word = FindKeyMatchConfirmWord(obs.Ocr);
                double nx = word?.CenterX ?? 0.5;
                double ny = word?.CenterY ?? 0.59;
                _log($"[我的联赛] 关键比赛弹窗 ④：RT 未推进，改鼠标点{(word is null ? "兜底坐标" : $"「{word.NormText}」")}" +
                     $"（{nx:F3},{ny:F3}）；鼠标点击需本程序以管理员运行才被国服接收", LogLevel.Warn);
                await ClickAtAsync(nx, ny);
                return 2600;
            }

            case 5:
                _log("[我的联赛] 关键比赛弹窗 ⑤：下×1+A（反向布局兜底：确定在下、取消在上）", LogLevel.Warn);
                await _engine.Pad.Dpad(PadDir.Down);
                await _engine.Pad.Confirm();
                return 2600;

            case 6:
                _log("[我的联赛] 关键比赛弹窗 ⑥：上×2+A（三键布局饱和推到最上）", LogLevel.Warn);
                await _engine.Pad.DpadRepeat(PadDir.Up, 2);
                await _engine.Pad.Confirm();
                return 2600;

            case 7:
                _log("[我的联赛] 关键比赛弹窗 ⑦：B 收掉弹窗回主页，重新前往比赛", LogLevel.Warn);
                await _engine.Pad.Back();
                return 2600;

            case 8:
                _log("[我的联赛] 关键比赛弹窗 ⑧：B×2 退出联赛由主页重进（光标回默认位，主页导航会重新进联赛）", LogLevel.Warn);
                await _engine.Pad.Back();
                await _engine.Pad.Back();
                return 3200;

            default:
                _log("[我的联赛] 关键比赛弹窗：A/上移/RT/鼠标/下移/B 重进全部无效（9 轮），停止挂机——" +
                     "关键比赛弹窗只吃某一种输入，请在游戏里手动点一次「确定」开赛后重新开始挂机", LogLevel.Error);
                _engine.StopWithError("国服关键比赛弹窗点不动，请手动开赛后重新开始挂机");
                return 3500;
        }
    }

    /// <summary>后台鼠标点击（国服游戏为提权进程，须本程序以管理员运行 PostMessage 才被处理）。</summary>
    private async Task ClickAtAsync(double nx, double ny)
    {
        var win = _engine.Window;
        if (win is null)
        {
            _log("[我的联赛] 游戏窗口句柄缺失，鼠标点击无法执行", LogLevel.Error);
            return;
        }
        var (w, h) = EfootballBot.Core.System.GameWindow.GetClientSize(win.Handle);
        if (w <= 0 || h <= 0) (w, h) = (1280, 720);
        await _engine.Pad.HumanWait(150, 320, CancellationToken.None); // 拟人预停顿
        await _mouse.ClickNormalizedAsync(win.Handle, nx, ny, w, h, downHoldMs: 70);
    }

    /// <summary>
    /// 国服确认键：底栏常印「RT 确定 / RT 下一步」，但 A 同样生效（用户 2026-11-11 实测：
    /// 国服凡印 RT 的 下一步/确认/确定，按 A 一样推进）。默认按 A——与国际服、共享流程同路；
    /// 只有同一页连续多轮（旧式难度页 ≥4 轮）按 A 画面不动时，才换 RT（底栏所印键）兜底。
    /// </summary>
    private Task CnConfirmAsync() => _diffAttempts >= 4 ? _engine.Pad.RT() : _engine.Pad.Confirm();

    /// <summary>
    /// 国服难度页处置。2026-11-04 实机：国服难度页已改版为国际服同款 UI——大标题
    /// 「选择赛事等级」+ 底行「取消 | 确认」（确认钮蓝框 = A 键焦点），旧「列表游走 +
    /// RT 确定」配方在该页 RT 无绑定，永远开不了赛。检测到底行取消/确认结构 → 走
    /// <see cref="HandleCnModernDifficultyAsync"/>（国际服多轮配方）；检测不到 → 保留
    /// 旧配方兜底（防 OCR 漏读底行词回退到 2026-10-18 行为）。
    /// </summary>
    private async Task<int> HandleCnDifficultyAsync(Observation obs, int streak)
    {
        if (streak < 1)
        {
            _diffAttempts = 0; // 页面刚出现（或重开后）：预算清零
            _diffRound = 0;
            return 800;        // 淡入防抖，等下一轮再读勾
        }

        if (HasBottomConfirmRow(obs.Ocr))
            return await HandleCnModernDifficultyAsync(obs);

        if (++_diffAttempts > 6)
        {
            _log("[我的联赛] 难度页多次确认未生效，B 返回主页重走「前往比赛」", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }

        string wanted = LeagueConfig.Difficulty;
        var ocr = obs.Ocr;

        // 关键比赛：难度由游戏指定（受限页只出高档位），按默认直接确认，绝不导航改选
        if (MyLeagueBrain.RespectGameDifficulty(_nextMatchIsKey, ocr))
        {
            _log("[我的联赛] 关键比赛：难度由游戏指定，直接确认开赛（不改选）", LogLevel.Info);
            await CnConfirmAsync();
            return 3200;
        }

        string? selected = MyLeagueBrain.DetectSelectedDifficulty(obs.Frame, ocr);
        var plan = PlanCnDifficulty(selected, wanted);
        if (plan is null)
        {
            // 目标难度名不合法（配置异常）：按游戏默认直接确认，绝不盲导航
            _log($"[我的联赛] 难度「{wanted}」不在难度表，按游戏默认确认开赛", LogLevel.Warn);
            await CnConfirmAsync();
            return 3200;
        }

        _log($"[我的联赛] 难度页：{plan.Summary}：上×{plan.UpStrokes} 归位 → 下×{plan.DownStrokes}" +
             (plan.CheckFirst ? " → A 勾选" : "") + " → 确认（A，底栏印 RT 也吃 A）", LogLevel.Info);
        await _engine.Pad.DpadRepeat(PadDir.Up, plan.UpStrokes);
        await _engine.Pad.DpadRepeat(PadDir.Down, plan.DownStrokes);
        if (plan.CheckFirst) await _engine.Pad.Confirm(); // A 勾选光标行（该页 A=勾选，实机 2026-10-18 日志证据）
        await CnConfirmAsync();
        return 3500;
    }

    /// <summary>
    /// 新式难度页底行证据：「取消」「确认」两词同在页面底部（y &gt; 0.85）且页内读出难度名。
    /// 难度词证据防止兑换积分弹窗/购买弹窗（同样含取消+确认）误判；购买弹窗另有结构词
    /// 分类器前置拦截，这里是双保险。
    /// </summary>
    public static bool HasBottomConfirmRow(OcrResult ocr)
    {
        if (!ocr.ContainsJoined("取消") || !ocr.ContainsJoined("确认")) return false;
        bool bottom = ocr.Words.Any(w =>
            (w.NormText.Contains("取消") || w.NormText.Contains("确认")) && w.CenterY > 0.85);
        return bottom && MyLeagueBrain.DetectDifficultyOptions(ocr).Count > 0;
    }

    /// <summary>
    /// 新式难度页（国际服同款 UI）处置：配方与国际服 <see cref="MyLeagueBrain.DifficultyLegPlan"/>
    /// 一致——第 1 轮纯 A（吃定「确认已聚焦」的常见情形；列表行上 A 是空操作无副作用），
    /// 第 2 轮起 上×9 饱和回顶 → 下×10 饱和落底行 → 右×N（取消→确认）→ A，偶数轮 右×2 防吞键；
    /// 第 4/7 轮 B 关对话框回主页重进，第 9 轮后停止挂机。蓝勾检测只做告警不改导航
    /// （难度在建库时已定档，页面内列表 A 不改选——国服改版后与国际服同守则）。
    /// </summary>
    private async Task<int> HandleCnModernDifficultyAsync(Observation obs)
    {
        string wanted = LeagueConfig.Difficulty;
        var checkedDiff = MyLeagueBrain.DetectCheckedDifficulty(obs.Frame, obs.Ocr);
        if (checkedDiff is not null && checkedDiff != wanted)
            _log($"[我的联赛] 注意：蓝勾在「{checkedDiff}」而配置要「{wanted}」——新式难度页列表按 A 不改选，按当前勾选确认", LogLevel.Warn);

        int round = ++_diffRound;
        if (round > 9)
        {
            _log("[我的联赛] 新式难度页连续 9 轮确认无效（疑似有识别不到的弹窗遮挡），停止挂机", LogLevel.Error);
            _engine.StopWithError("难度页反复确认无效，请手动查看游戏画面是否有弹窗");
            return 3500;
        }
        var leg = MyLeagueBrain.DifficultyLegPlan(round);
        if (leg is null) // 第 4/7 轮：B 关对话框回联赛主页，正常流程会重新前往比赛重进
        {
            _log($"[我的联赛] 新式难度页第 {round} 轮仍未确认：B 退出对话框重进（光标复位）", LogLevel.Warn);
            await _engine.Pad.Back();
            return 3500;
        }

        string desc = leg.Value is { Downs: 0, Rights: 0 }
            ? "只按 A（确认钮可能已聚焦）"
            : $"{(leg.Value.UpFirst ? "上×9 " : "")}下×{leg.Value.Downs} 右×{leg.Value.Rights} A";
        _log($"[我的联赛] 新式难度页第 {round} 轮确认（蓝勾={checkedDiff ?? "未检出"}）：{desc}", LogLevel.Info);
        if (leg.Value.UpFirst)
            await _engine.Pad.DpadRepeat(PadDir.Up, 9);
        await _engine.Pad.DpadRepeat(PadDir.Down, leg.Value.Downs);
        await _engine.Pad.DpadRepeat(PadDir.Right, leg.Value.Rights);
        await _engine.Pad.Confirm();
        return 3500;
    }

    /// <summary>一次国服难度页落位规划：饱和上移行数 / 落位后下移行数 / 是否需 A 勾选。</summary>
    public sealed record CnDifficultyPlan(int UpStrokes, int DownStrokes, bool CheckFirst, string Summary);

    /// <summary>
    /// 国服难度页落位规划（纯函数）。光标起始位置未知 → 一律「上×难度表长度」饱和归位首行
    /// （列表到头即停不回绕，与全库饱和归位约定一致），再按难度表行号（而非当帧 OCR 检出的
    /// 子集——漏读一行就会错位一行）下移落位。当前已勾目标 → 落位后直接 RT（CheckFirst=false，
    /// 无需再勾）；当前未读出/不在难度表 → 同样落位到目标行再 RT（RT 若按光标行开赛则正中
    /// 目标，若按已勾行开赛则等于游戏默认，两种实现下都不劣于盲确认）；目标名不在难度表 →
    /// null（绝不导航）。
    /// </summary>
    public static CnDifficultyPlan? PlanCnDifficulty(string? selected, string wanted)
    {
        int to = Array.IndexOf(MyLeagueBrain.Difficulties, wanted);
        if (to < 0) return null;
        bool check = selected is not null && selected != wanted &&
                     Array.IndexOf(MyLeagueBrain.Difficulties, selected) >= 0;
        string why = check ? $"{selected} → {wanted}（第 {to + 1} 行）"
            : selected is null ? $"当前难度未读出，落位 {wanted}（第 {to + 1} 行）后确认"
            : selected == wanted ? $"已是 {wanted}（第 {to + 1} 行），落位后确认"
            : $"当前 {selected} 不在难度表，落位 {wanted}（第 {to + 1} 行）后确认";
        return new CnDifficultyPlan(MyLeagueBrain.Difficulties.Length, to, check, why);
    }
}
