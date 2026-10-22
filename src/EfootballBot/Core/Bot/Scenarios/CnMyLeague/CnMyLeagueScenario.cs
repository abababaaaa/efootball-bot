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

    /// <summary>难度页确认预算：连续多轮未离开难度页 → B 退出重走「前往比赛」（页面重开光标归位）。</summary>
    private int _diffAttempts;

    /// <summary>最近一轮国服界面判定（ClassifyScreen 产出，InterceptAsync 消费；单线程无竞争）。</summary>
    private PlScreen _pl = PlScreen.None;
    private CwScreen _cw = CwScreen.None;

    public CnMyLeagueScenario(BotEngine engine, Action<string, LogLevel> log)
        : base(engine, log)
    {
    }

    public override string Name => "我的联赛（国服）";

    /// <summary>赛后活动任务页仅国际服独享：国服不识别不接管。</summary>
    protected override bool SupportsPostMatchActivity => false;

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

        // ---- 国服特有：比赛起始页确认键是 RT 而非 A（国际服为 A，按 A 永远开不了赛）----
        if (screen == MlScreen.MatchStartPage)
        {
            if (streak < 1) return 800; // 过渡帧防抖
            _log("[我的联赛] 起始页：RT 下一步开赛（国服确认键为 RT）", LogLevel.Info);
            await _engine.Pad.RT();
            return 3400;
        }

        // ---- 国服「选择比赛等级」难度页全量接管（2026-10-18 实机）：布局与国际服页不同——
        //      没有「底行 取消/确认」结构，光标在 7 行难度列表内游走，A=勾选光标行、RT=确定。
        //      国际服基类按「默认光标在底行确认」导航（上×k A 下×k 右×1 A），落点全在列表里，
        //      把勾反复打在 业余 行——实机日志证据：首轮读 当前=新手，此后永远 业余（上一轮
        //      计划的末次 A 按在了业余行）。此处接管后基类难度页处理对国服不可达 ----
        if (screen == MlScreen.DifficultyPage)
            return await HandleCnDifficultyAsync(obs, streak);
        _diffAttempts = 0;

        // ---- 国服 RT 确认页保险：推进页 A 连续 10 轮无进展改试 RT（天梯对阵表「RT 下一步」
        //      同型页面偶见于国服各流程）。正常 A 推进的页面每页 1~2 轮即过，到不了 10 轮；
        //      赛后奖励弹窗链虽会累计轮数，但 RT 在弹窗上无绑定，按了无害。
        //      难度页已由上方分支全量接管（含 RT 确认与 B 退出自愈），不进本保险 ----
        if (screen == MlScreen.HasConfirm &&
            streak >= 10 && (streak - 10) % 5 == 0)
        {
            _log($"[我的联赛] {DescribeScreen(screen)} 连续 {streak} 轮 A 无进展（疑似国服 RT 确认页），尝试 RT", LogLevel.Warn);
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

    /// <summary>
    /// 国服难度页处置（守则同基类：读当前选中与配置比对，读不到/已一致绝不乱按方向）：
    /// 需改选/需落位 → <see cref="PlanCnDifficulty"/> 规划（上×7 饱和归位首行 → 下×(k-1)
    /// 按难度表行号精确落位 → A 勾选 → RT 确定）；关键比赛不改选，直接 RT。
    /// 连续 6 轮仍未离开难度页 → B 返回主页重走「前往比赛」（页面重开后光标归位）。
    /// </summary>
    private async Task<int> HandleCnDifficultyAsync(Observation obs, int streak)
    {
        if (streak < 1)
        {
            _diffAttempts = 0; // 页面刚出现（或重开后）：预算清零
            return 800;        // 淡入防抖，等下一轮再读勾
        }
        if (++_diffAttempts > 6)
        {
            _log("[我的联赛] 难度页多次确认未生效，B 返回主页重走「前往比赛」", LogLevel.Warn);
            await _engine.Pad.Back();
            return 2400;
        }

        string wanted = LeagueConfig.Difficulty;
        var ocr = obs.Ocr;

        // 关键比赛：难度由游戏指定（受限页只出高档位），按默认直接 RT 确认，绝不导航改选
        if (MyLeagueBrain.RespectGameDifficulty(_nextMatchIsKey, ocr))
        {
            _log("[我的联赛] 关键比赛：难度由游戏指定，RT 直接确认开赛（不改选）", LogLevel.Info);
            await _engine.Pad.RT();
            return 3200;
        }

        string? selected = MyLeagueBrain.DetectSelectedDifficulty(obs.Frame, ocr);
        var plan = PlanCnDifficulty(selected, wanted);
        if (plan is null)
        {
            // 目标难度名不合法（配置异常）：按游戏默认直接确认，绝不盲导航
            _log($"[我的联赛] 难度「{wanted}」不在难度表，按游戏默认 RT 确认开赛", LogLevel.Warn);
            await _engine.Pad.RT();
            return 3200;
        }

        _log($"[我的联赛] 难度页：{plan.Summary}：上×{plan.UpStrokes} 归位 → 下×{plan.DownStrokes}" +
             (plan.CheckFirst ? " → A 勾选" : "") + " → RT 确定", LogLevel.Info);
        await _engine.Pad.DpadRepeat(PadDir.Up, plan.UpStrokes);
        await _engine.Pad.DpadRepeat(PadDir.Down, plan.DownStrokes);
        if (plan.CheckFirst) await _engine.Pad.Confirm(); // A 勾选光标行（该页 A=勾选，实机 2026-10-18 日志证据）
        await _engine.Pad.RT();                            // 国服确认键
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
