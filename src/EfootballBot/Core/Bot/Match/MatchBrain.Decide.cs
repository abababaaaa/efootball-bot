using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

// ===========================================================================
// 架构锁 · 共享层 Core/Bot/Match（v7.2）
// 本层职责：所有模块共用的能力 —— 界面分类、共享决策、赛中匹配 MatchPlay、流程记忆、像素探针、弹窗判定。
// 硬约束（构建期 tools/arch-guard/arch-guard.ps1 强制，违反即编译失败）：
//   · 本层不许 using / 引用 Core/Bot/Scenarios.*（共享层绝不能反向依赖模块）
//   · 场次记账（MatchesDone/KeyMatches）与比赛内挂机节奏只在本层实现，模块不许自己抄一份
// 新增共享能力：本层加文件；需要模块配合的用接口（如 IModuleLifecycle）反向交给模块。
// 除非用户明确要求更换写法，否则按本层约定写。
// ===========================================================================

namespace EfootballBot.Core.Bot.Match;

/// <summary>
/// 共享决策大脑（决策部分）：界面 + OCR + 连续轮数 → 手柄动作。
/// 各模块（国服/国际服、天梯/周活动/我的联赛/种马）共用这一份；模块只在自己的
/// 状态机之前拦截「本模块自有界面」，其余全部交这里——改一次全模块生效。
/// </summary>
public static partial class MatchBrain
{
    /// <summary>
    /// 按当前界面 + OCR + 连续轮数决策。<paramref name="inMatchRecently"/> 来自流程帧记忆
    /// （最近刚见过比赛特征）：为 true 时无特征界面按比赛过场处理，延长 B 逃生耐心。
    /// <paramref name="frame"/> 用于「跳过比赛✅」横幅的绿勾像素核验（同位置还有无勾的
    /// 目标提示 toast，见 InMatch 分支），调用方尽量传入。
    /// <paramref name="allowMatchSkip"/> 为 false 时禁用「跳过比赛」全链路（横幅不按暂停、
    /// 暂停菜单不点对局获胜）。2026-10-31 曾按「跳过的对局不计积分」对挑战赛整体禁用；
    /// 2026-11-06 用户决定挑战赛也放开，改由调用方按"本场已试过一次"动态关闭
    /// （见 CnWeeklyScenario._skipBlockedThisMatch），保留这个开关作为最后的安全阀。
    /// </summary>
    public static MatchAction Decide(MatchScreen screen, OcrResult ocr, int screenStreak,
        bool inMatchRecently = false, FrameData? frame = null, bool allowMatchSkip = true)
    {
        switch (screen)
        {
            case MatchScreen.MainMenu:
            {
                // 复合导航：Dpad 下×2（新 UI：第 1 下离开页签行、第 2 下进底部卡片行，
                // 用户 2026-10-19 实测）→ 左 ×6（推到最左"比赛"，到头多按无害）→ A 进入。
                // 超过 StuckStreak 只放慢继续导航；到 NavEscapeStreak 仍无效说明有识别不到的盖层/
                // 焦点异常，按 B 清一次盖层（主菜单 B 最坏情况是弹退出确认窗 → HasConfirm 上移+确认
                // 退出 → 标题画面 X 重进，仍可自愈），此后每 5 轮一次——好过静默死等。
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return PeriodicBackAction(screenStreak, NavEscapeStreak, "主菜单导航失效，B 清盖层重试");
                return new MatchAction(MatchActionKind.PadEnterMatch,
                    screenStreak >= StuckStreak ? 5000 : 3000, "主菜单→比赛卡片");
            }
            case MatchScreen.MatchHub:
            {
                // 右移选中「我的联赛」磁贴：进入比赛页后默认焦点在最左卡（新 UI 2026-10-21 实拍：
                // 卡排 锦标赛/活动/eFootball™联赛/我的联赛）。
                // 焦点确认认「我的联赛」词、y 不限——新 UI 没有旧版预览区（旧判据 y<0.6 恒假会
                // 无限右移卡死）：未聚焦时该卡被屏幕右缘裁掉只读出碎片（我/自），聚焦后卡排
                // 滚入、标签完整可读。误读提前按 A 的最坏情况是进错卡详情页，其分类不再是
                // MatchHub，B 逃生自愈。焦点词整排漏读时 ≥NavEscapeStreak 按 B 回主菜单重导航。
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return PeriodicBackAction(screenStreak, NavEscapeStreak, "比赛页导航失效，B 回主菜单重导航");
                bool focused = ocr.Lines.Any(l => l.Text.Contains("我的联赛"));
                if (focused)
                    return new MatchAction(MatchActionKind.PadConfirm,
                        screenStreak >= StuckStreak ? 5000 : 2500, "比赛页→我的联赛");
                // 焦点词读不到就一直右移 → 到最右也不换动作，只有 20 轮后的 B 逃生兜着，
                // 用户看到的是「比赛页来回晃、永远不开赛」（识别走查 2026-11-18 的主要僵局之一）：
                // 新 UI 未聚焦时磁贴标签被右缘裁成碎片（我/自），OCR 读不出整词。
                // 连续 6 轮（≈10s）仍读不到 → 按 A 试进当前焦点卡；进错板块的分类不会是
                // MatchHub，B 逃生自愈，最坏是绕一次，好过无限右移。
                if (screenStreak >= MatchHubFocusBlindStreak)
                    return new MatchAction(MatchActionKind.PadConfirm, 2500, "比赛页焦点词读不到，按 A 试进当前卡");
                return new MatchAction(MatchActionKind.PadMoveRight,
                    screenStreak >= StuckStreak ? 5000 : 1500, "比赛页右移→我的联赛");
            }
            case MatchScreen.ItemList:
            {
                // 物品列表：A 选中当前焦点行；多轮无效（≥NavEscapeStreak）B 退出由购买状态机重导航
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return PeriodicBackAction(screenStreak, NavEscapeStreak, "物品列表多轮无进展，B 退出重进");
                return new MatchAction(MatchActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "物品列表焦点行");
            }
            case MatchScreen.HasConfirm:
            {
                // 带取消类按钮的弹窗（关键比赛 P3：OK 在上、取消在下且是默认焦点；购买弹窗
                // 同型；跳过比赛确认弹窗默认焦点在关闭）：盲按 A 正好按在取消/关闭上把弹窗
                // 关掉。★统一规则（用户 2026-10-18）：弹窗出现 取消/关闭 类字眼一律先上移
                // 把光标挪到 下一步/确定/进入 等推进键上再按 A——单按钮弹窗上移无害，双按钮
                // 弹窗恰好从取消/关闭挪到推进键。难度选择页除外（其确认键就是默认焦点，上移
                // 反而进选项列表改难度）；启动设定窗除外（按 A 保留设置继续）。
                // 超过退避轮数只放慢不停手：弹窗链的第 9+ 页仍需要 A 推进（见 StuckStreak 注释）。
                // 到 ConfirmEscapeStreak 仍无进展（A 永不生效的 RT 型页）：B 退出该页自愈。
                if (screenStreak >= ConfirmEscapeStreak && (screenStreak - ConfirmEscapeStreak) % 5 == 0)
                    return PeriodicBackAction(screenStreak, ConfirmEscapeStreak, "弹窗久按无效，B 逃生");
                bool slow = screenStreak >= StuckStreak;
                // 弹窗按键规则（用户 2026-11-10）：单按钮直接 A；多按钮看白底高光那枚是不是
                // 确认/进行/OK/下一步 这类推进键，是 → 直接 A，不是 → 按上下关系挪一格再 A。
                // 难度选择页/启动设定窗仍走旧判定（它们的确认键本来就是默认焦点，挪格反而进列表）。
                if (!IsDifficultyChoice(ocr) && !IsGameSettingsDialog(ocr))
                {
                    var popup = PopupBrain.Decide(frame, ocr, out var popupNote);
                    // 画面不变 → 换招：挪格失效就退回「直接 A」（见 ConfirmRepositionRounds 的说明）
                    if (popup is PopupAction.UpThenConfirm or PopupAction.DownThenConfirm &&
                        screenStreak >= ConfirmRepositionRounds)
                        return new MatchAction(MatchActionKind.PadConfirm, slow ? 5000 : 2000,
                            $"高光挪格 {ConfirmRepositionRounds} 轮以上画面未变，改用直接 A（{popupNote}）");
                    switch (popup)
                    {
                        case PopupAction.DownThenConfirm:
                            return new MatchAction(MatchActionKind.PadDownConfirm, slow ? 5000 : 2000, popupNote);
                        case PopupAction.UpThenConfirm:
                            return new MatchAction(MatchActionKind.PadUpConfirm, slow ? 5000 : 2000, popupNote);
                        case PopupAction.PressConfirm:
                            return new MatchAction(MatchActionKind.PadConfirm, slow ? 5000 : 2000, popupNote);
                    }
                }
                if ((ocr.ContainsJoined("取消") || ocr.ContainsJoined("关闭")) &&
                    !IsDifficultyChoice(ocr) && !IsGameSettingsDialog(ocr))
                    return new MatchAction(MatchActionKind.PadUpConfirm, slow ? 5000 : 2000, "弹窗确认（上移避开取消/关闭）");
                return new MatchAction(MatchActionKind.PadConfirm, slow ? 5000 : 2000, "确定/下一步");
            }
            case MatchScreen.LeaguePage:
            {
                // 「前往比赛」= 联赛主页主按钮；「下个赛季」= 赛季结束替换词（实机 next_season：
                // 前往比赛位置变为"下个赛季"，另有"前往下个赛季"，含"下个赛季"即覆盖）
                // 「对阵AI类别/对阵玩家类别」= eFootball™联赛类别选择页（实机 2026-11-22：
                // 我的联赛从比赛页按 A 进到该页后，OCR 只有「eFootball™联赛|对阵AI类别」，
                // 旧词表无匹配 → 一直 Wait 卡死。本模块无前置 Brain，此页落到 LeaguePage，
                // 补上类别词即可按 A 进入；AI天梯有自己的 AiLadderBrain 先认，不受此影响。）
                var go = FindLine(ocr, "下一步", "开始比赛", "开赛", "继续", "前往比赛",
                    "进入比赛", "下个赛季", "开始",
                    "对阵AI类别", "对阵玩家类别", "AI类别", "玩家类别");
                if (go is not null)
                    return new MatchAction(MatchActionKind.PadConfirm, 2500, go);
                // 关键词里没有、但底栏确实印着一枚推进键（新版换了文案/整行被并读成本页
                // 识别不出的词）：看见按钮就按，别干等到 B 逃生——这一步就是「不自动开始比赛」
                // 最常见的僵局（主页停在原地，A 一次也没发出去）。
                if (FindBottomPushWord(ocr) is { } push)
                    return new MatchAction(MatchActionKind.PadConfirm, 2500, $"主页底部推进键「{push}」");
                // 无任何可推进关键词（赛程表/联赛子页等，实机 2026-10-10 21:18 卡死 4 分钟）：
                // 连续多轮无关键词 → 按 B 返回上一页自愈，绝不无限等待
                return screenStreak >= StuckStreak
                    ? new MatchAction(MatchActionKind.GamepadBack, 3000, "联赛页无关键词，B 返回自愈")
                    : MatchAction.Wait(2500);
            }
            case MatchScreen.LeagueSquadPage:
            {
                // 首发站位页：B 返回联赛主页，由正常流程继续（前往比赛→难度→开赛）。
                // 满级换人巡检激活时不走此分支（场景层 hook 先行接管并在该页直接开跑）。
                return new MatchAction(MatchActionKind.GamepadBack, 2500, "首发站位页→B 返回联赛主页");
            }
            case MatchScreen.FullTime:
            {
                // 终场：脱离比赛状态，立即按 A（下一步）。结算/里程碑多页链同理退避只放慢
                return new MatchAction(MatchActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2000, "全场结束→下一步");
            }
            case MatchScreen.KeyMatchDialog:
            {
                // 关键比赛弹窗：光标上移一格点 OK（实机 P2/P3：OK 上方/下方默认位不对齐）。
                // 上移+A 是唯一关法，退避只放慢不停手
                return new MatchAction(MatchActionKind.PadUpConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "关键比赛→OK");
            }
            case MatchScreen.MatchStartPage:
            {
                // 起始页：按 A（下一步）开赛；比赛画面出现后由比分框时钟进入 InMatch（自动点击）
                return new MatchAction(MatchActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 3000, "起始页→下一步开赛");
            }
            case MatchScreen.TitleScreen:
            {
                // 标题画面：进入键是手柄 X（A 无效），按 X 进入主菜单。
                // 场景层的 30s 宽限（区分加载画面）期间不按键，宽限后必须持续按 X——
                // 退避停手会让真标题（掉线/游戏重启）永久卡死
                return new MatchAction(MatchActionKind.PadXPress,
                    screenStreak >= StuckStreak ? 5000 : 3000, "标题画面→X");
            }
            case MatchScreen.LeagueInfoPage:
            {
                // 误入「我的联赛」说明页：B 返回联赛主页。连测 2 轮（streak≥1）再退，
                // 防界面切换的白屏过渡帧误判后误按 B 退出联赛。
                if (screenStreak < 1) return MatchAction.Wait(1200);
                return new MatchAction(MatchActionKind.GamepadBack, 2500, "说明页→B 返回主页");
            }
            case MatchScreen.MatchHistory:
            {
                // 误入比赛历史页：B 回主页（主页焦点随之回到「比赛历史」面板，主流程会再按 A
                // 开赛；周活动模块另有焦点归位档位处理）。连测 1 轮再退，防过渡帧误判。
                if (screenStreak < 1) return MatchAction.Wait(1200);
                return new MatchAction(MatchActionKind.GamepadBack, 2500, "比赛历史页→B 返回主页");
            }
            case MatchScreen.PurchaseDialog:
            {
                // 购买弹窗：焦点默认在「取消」→ 上移一格点上方带数字（积分价）的确认按钮。
                // 上移+A 是买法；上移与 A 之间需留延时让游戏刷新焦点（见各模块 PadUpConfirm 执行）。
                return new MatchAction(MatchActionKind.PadUpConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "购买确认（上移→数字按钮）");
            }
            case MatchScreen.PointsPage:
            {
                // 积分页：进入「我的联赛物品」（右选项）。导航序列由场景层校验重试；
                // ≥NavEscapeStreak 按 B 回退重新导航（此前永久等待）
                if (screenStreak >= NavEscapeStreak && (screenStreak - NavEscapeStreak) % 5 == 0)
                    return PeriodicBackAction(screenStreak, NavEscapeStreak, "积分页导航失效，B 回退重导航");
                return new MatchAction(MatchActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2500, "积分页→我的联赛物品");
            }
            case MatchScreen.InMatch:
            {
                // 比赛中超时兜底：远超一场比赛时长仍停留（重播暂停等）才 B 逃生
                if (screenStreak >= InMatchEscapeStreak)
                    return new MatchAction(MatchActionKind.GamepadBack, 3000, "比赛超时 B 逃生");
                // 右上角「跳过比赛✅」横幅（国服 2026-10-17 实机）：按暂停键打开暂停菜单。
                // 能否真跳由菜单里的「对局获胜」小字二次把关（见 CnMatchPause 分支）。
                // ★同位置还会弹「跳过比赛 / 累计进球N/3个或以上」挑战目标提示 toast（用户
                //   2026-10-17 实机）——那只是目标提醒、不可跳，横幅与 toast 的区别是右端
                //   绿勾，必须绿勾像素核验通过（+排除 累计进球 词）才按暂停，否则空开菜单
                if (allowMatchSkip && ocr.ContainsJoined("跳过比赛") && !ocr.ContainsJoined("累计进球") &&
                    SkipBannerHasCheck(frame))
                    return new MatchAction(MatchActionKind.PadStartPress, 3000, "跳过比赛✅→暂停菜单");
                // 过场动画（进球回放 / 庆祝 / 入场）：屏幕出现「跳过」提示就快节奏连按 A。
                // 用户 2026-11-13 国际服反馈：一场要 20 分钟、动画基本都看完了、把挂机节奏
                // 改成 1~2 秒也没用——旧逻辑始终按挂机节奏（1~4 秒）点 A，过场提示在两次
                // 点击之间一闪就过，等于整段播完。这里把提示出现时的节奏压到 300ms，
                // 提示消失立即回到原节奏（见 IsSkipOverlay 的排除项，不碰国服「跳过比赛」链路）。
                if (IsSkipOverlay(ocr))
                    return new MatchAction(MatchActionKind.PadConfirm, 300, "过场回放：快按 A 跳过");
                // 比赛中覆盖层上的推进键（下一步/继续/确认/OK）：新版把回放/中场提示做成
                // 带「下一步」的小弹窗，旧逻辑只在 Unknown 分支认这些词、节奏 2.5s，
                // 用户报的「比赛中下一步不会点击」正是这一层——按 A 也快一拍（400ms）
                if (IsInMatchAdvance(ocr))
                    return new MatchAction(MatchActionKind.PadConfirm, 400, "比赛中推进键：快按 A");
                // 入场与过场动画跳过（用户明确要求）：
                // 比赛上下文下无从比板时钟 = 过场/入场分镜（首发阵容、猜硬币、入场动画、进球回放），
                // 必须以快节奏连按 A 瞬间切断所有分镜、推进到开球。
                // 只有 Live Match（HasScoreboard=true，左上角时钟常在）才落到下面的 MatchIdleTap
                // 拟人化慢节奏，防止比赛中乱传球。
                if (!HasScoreboard(ocr))
                    return new MatchAction(MatchActionKind.PadConfirm, 350, "无时钟过场/入场：快按 A 跳过");
                // 比赛中挂机：按 A + 用户设定的随机间隔（设置→挂机节奏），
                // 拟人化避免全程零输入；A 在比赛中是传球/逼抢，不会暂停或退出
                return new MatchAction(MatchActionKind.MatchIdleTap, 0, "比赛中随机点击");
            }
            case MatchScreen.CnMatchPause:
            {
                // 跳过确认弹窗（盖在暂停菜单上：确认跳过/关闭，焦点默认在关闭）。必须先于
                // 对局获胜判定——弹窗文案「以当前比分跳过，对局获胜」也含对局获胜，盲按 A
                // 会按在关闭上永远进不去。上移光标点「确认跳过」
                if (ocr.ContainsJoined("确认跳过"))
                    return allowMatchSkip
                        ? new MatchAction(MatchActionKind.PadUpConfirm, 2600, "跳过比赛→确认跳过")
                        : new MatchAction(MatchActionKind.GamepadBack, 2600, "积分制禁跳，B 关确认弹窗返回比赛");
                // 暂停菜单：默认光标就在「跳过比赛」上，仅当其下有绿色小字「对局获胜」才可跳
                // （用户 2026-10-17 实机规则：不是这几个字就返回比赛并继续挂机）
                if (allowMatchSkip && ocr.ContainsJoined("对局获胜"))
                    return new MatchAction(MatchActionKind.PadConfirm, 2600, "跳过比赛（对局获胜）");
                return new MatchAction(MatchActionKind.GamepadBack, 2600, "不可跳过，B 返回比赛继续");
            }
            case MatchScreen.BootSplash:
            {
                // eFootball 2027 标题页（掉线/重启回落，防掉线恢复入口）：前几轮 下→右→A
                // （用户实机登录序列），之后改持续按 A（页面提示按「确认」按钮）。
                // 转圈加载帧同判此处，加载期按键无效但无害。★绝不放弃——这是真标题
                // （掉线）恢复的唯一入口，退避停手会让挂机永久卡死（同 TitleScreen 持续按 X 先例）
                return screenStreak < BootLoginNavRounds
                    ? new MatchAction(MatchActionKind.PadLoginNav, 3500, "标题页→下右A 登录")
                    : new MatchAction(MatchActionKind.PadConfirm, 4500, "标题页→A 确认登录");
            }
            case MatchScreen.GachaPage:
            {
                // 抽取界面：绝不消费金币——按 B 退出该页（右上角即 B 关闭）。退避只放慢
                return new MatchAction(MatchActionKind.GamepadBack,
                    screenStreak >= StuckStreak ? 5000 : 2600, "抽取界面→B 退出");
            }
            case MatchScreen.PaymentConfirmDialog:
            {
                // 付款确认弹窗：光标默认在「取消」——直接按 A 即取消付款。
                // ★绝不上移：上方是 eFootball 金币付款键（与购买弹窗方向相反）
                return new MatchAction(MatchActionKind.PadConfirm, 2600, "付款确认→A 取消");
            }
            case MatchScreen.CampaignGridPage:
            {
                // New Playing Style 活动奖励网格页（登录后弹窗链成员）：底部「继续」即默认
                // 焦点（白底高亮）——直接 A 推进领奖，绝不上移（左下角 Y=详情，无关）。
                // 退避只放慢不停手（弹窗链页通用）；久按无效周期 B 逃生（A 被吞时自愈）
                if (screenStreak >= ConfirmEscapeStreak && (screenStreak - ConfirmEscapeStreak) % 5 == 0)
                    return PeriodicBackAction(screenStreak, ConfirmEscapeStreak, "活动奖励页久按无效，B 逃生");
                return new MatchAction(MatchActionKind.PadConfirm,
                    screenStreak >= StuckStreak ? 5000 : 2200, "活动奖励页→A 继续");
            }
            case MatchScreen.NotLoggedIn:
            {
                // 未登录页（实机 2026-10-21 用户视频逐帧）：默认焦点在顶部页签「游戏主菜单」，
                // 不是右侧按钮——直接 A 无效；先按右会切到「学院」页签（错误页面）。
                // 正确序列：下（落到「友谊赛」卡）→右（落到「登录」键）→A。
                // ★绝不按 B/逃生（此页 B 无效，会空转卡死）。
                // ★焦点探针（见 DetectNotLoggedInFocus）保证重试幂等：登录失败后焦点仍在
                // 「登录」时只按 A；若焦点落到「退出游戏」（聚焦键变白）则上移一格再 A。
                // 绝不盲目重跑下→右→A——第二次的「下」会落到退出游戏键上，A 即退出游戏。
                var focus = DetectNotLoggedInFocus(frame);
                if (focus == LoginButtonFocus.ExitGame)
                    return new MatchAction(MatchActionKind.PadUpConfirm, 2600, "未登录→焦点在退出游戏，上移登录");
                if (focus == LoginButtonFocus.Login)
                    return new MatchAction(MatchActionKind.PadConfirm,
                        screenStreak >= StuckStreak ? 5000 : 2600, "未登录→A 登录（焦点已在登录键）");
                return new MatchAction(MatchActionKind.PadLoginNav,
                    screenStreak >= StuckStreak ? 5000 : 3500, "未登录→下右A 登录");
            }
            case MatchScreen.AnnouncementPage:
            {
                // 登录后公告白页（eFootball™积分官方网站问题，实机 2026-10-21）：页面右上角
                // 明示「B 关闭」，无 A 推进键。退避只放慢不停手（B 偶发被吞）
                return new MatchAction(MatchActionKind.GamepadBack,
                    screenStreak >= StuckStreak ? 5000 : 2400, "公告页→B 关闭");
            }
            default:
            {
                // 比赛上下文中的弹窗（用户实测 2026-10-10：右上角通知/满级提示等带
                // 「下一步/OK/确认」按钮）→ 直接按 A 推进，不等待
                // 2026-11-18：词表扩到 InMatchPushWords + 整词 OK，节奏 2500→1500ms。
                // 覆盖层只闪一瞬，旧节奏下这一拍经常落在两次点击之间（"下一步不会点击"）。
                if (inMatchRecently && IsInMatchAdvance(ocr))
                    return new MatchAction(MatchActionKind.PadConfirm, 1500, "比赛弹窗→下一步");
                // 比赛/过场/加载：连续多轮无特征时按 B 逃生。
                // 完全无感知（OCR 0 词，典型：捕获黑屏/驱动异常）时绝不盲按 B
                // （2026-10-10 15:48 实机：黑帧下盲按 B 9 次），只等待。
                // 比赛上下文（最近见过比赛特征）：多为镜头切换丢时钟，延长逃生耐心防铲球。
                // 加载/过场的「水印页」（实机 2026-11-20 00:51：国服开赛加载停在球场全景，OCR 只剩
                // 「实况足球在线 | X | 11.1.0.1…」）不吃任何键，按 A/B 只会把还没走完的开赛流程取消掉：
                // 那一晚在加载页上按了 1 次 A 试探 + 15 次 B 逃生，最后被输入自检判成「手柄已死」停机。
                // 这类页面一律等它自己走完（WatermarkWaitGraceStreak 轮后仍不动才交回常规逃生）。
                if (screenStreak <= WatermarkWaitGraceStreak && InputLivenessProbe.IsWatermarkPage(ocr))
                    return MatchAction.Wait(screenStreak >= UnknownProbeAStreak ? 6000 : 3000);
                // 过场回放（带「跳过」提示的加载/回放帧）：快按 A 跳过，别等到 4 秒的逃生节奏
                if (inMatchRecently && IsSkipOverlay(ocr))
                    return new MatchAction(MatchActionKind.PadConfirm, 350, "过场回放：快按 A 跳过");
                // 开赛过渡期避免死等 4 秒：比赛上下文下画面认不出且词少（入场、分镜切换），
                // 快节奏按 A 跳过入场。与 InMatch 分支的过场快跳同一意图，但不碰 B（B 是铲球键）。
                if (inMatchRecently && screenStreak < 8 && ocr.Words.Count <= 5)
                    return new MatchAction(MatchActionKind.PadConfirm, 400, "开赛过场过渡：按 A 跳过");
                int escapeAt = inMatchRecently ? UnknownEscapeStreakInMatch : UnknownEscapeStreak;
                // 认不出的普通界面（非比赛上下文）：等 UnknownProbeAStreak 轮还认不出，就按一次 A
                // 试探。此前只等到 B 逃生，若 B 这条链路也被吞（国服网页层常见），画面就永远不动；
                // A 是各页共有的"选中/推进"，先动一下比空等更接近人手，误进由 B 逃生收尾。
                if (!inMatchRecently && screenStreak >= UnknownProbeAStreak &&
                    screenStreak < escapeAt && ocr.Words.Count > 0)
                    return new MatchAction(MatchActionKind.PadConfirm, 2000, "认不出的界面：按 A 试探");
                if (screenStreak >= escapeAt && ocr.Words.Count > 0)
                    // 用周期换招替代裸 B：非比赛 6 轮起、比赛上下文 30 轮起，每 2 轮一次 B，
                    // 第 3 次（非比赛约 10 轮）升级为「上→A→B」。水印/加载页已在前面 return，不会误触发。
                    return PeriodicBackAction(screenStreak, escapeAt, "B 键逃生", period: 2);
                return MatchAction.Wait(4000);
            }
        }
    }

    /// <summary>
    /// 周期性 B 逃生动作：每 Period(5) 轮按一次 B；连续按 B 失败（界面/分类一直没变，
    /// 表现为 screenStreak 持续增长）达到 BackFailEscalations 次后，升级为「上 → A → B」换招
    /// ——先上移把焦点从「取消」移到确认键（直接 A 会落在取消上继续死循环），再 A 确认、最后 B 退出。
    /// 之后每 3 次 B 再换一次，避免一直重复换招。
    /// 纯函数、复用 screenStreak，不引入 OCR 复核开销。
    /// </summary>
    private static MatchAction PeriodicBackAction(int screenStreak, int threshold, string backTarget, int period = 5)
    {
        int k = (screenStreak - threshold) / Math.Max(1, period); // 第几次周期 B（0 起）
        bool escalate = k >= BackFailEscalations - 1 && (k - (BackFailEscalations - 1)) % BackFailEscalations == 0;
        return escalate
            ? new MatchAction(MatchActionKind.GamepadBackAfterConfirm, 3000, backTarget + "（B 多次无效，上→A→B 换招）")
            : new MatchAction(MatchActionKind.GamepadBack, 2400, backTarget);
    }

    /// <summary>
    /// 落在右上角横幅区（中心 x&gt;0.85 / y&lt;0.15）的 btn_confirm 检测数。
    /// 「跳过比赛✅」横幅右端的绿勾会被 YOLO 当成确认按钮（2026-11-06 实机 0.84~0.89），
    /// 而真正的确认按钮永远在画面中部/底部——按位置剔除，避免它把整帧从 InMatch 抢成 HasConfirm。
    /// </summary>
    internal static int BannerCornerConfirms(IReadOnlyList<YoloDetection> dets)
        => dets.Count(d => d.ClassName == "btn_confirm" && d.Score >= MinScore &&
                           d.CenterX > 0.85 && d.CenterY < 0.15);
    /// <summary>
    /// 「跳过比赛」横幅右端绿勾核验（≈(0.95,0.05)，实机 2026-10-17 用户截图）：绿像素 ≥30。
    /// ★同位置会弹「跳过比赛 / 累计进球N/3个或以上」挑战目标提示 toast——外观与横幅几乎
    /// 一样但没有绿勾，只是目标提醒、不可跳。frame 为 null（旧调用/部分单测）时只按词判
    /// （toast 由 累计进球 词排除）。
    /// </summary>
    public static bool SkipBannerHasCheck(FrameData? frame)
    {
        if (frame is null) return true;
        return CountGreen(frame, 0.930, 0.990, 0.015, 0.095) >= 30;
    }

    /// <summary>区域内绿色像素计数（共享实现见 <see cref="RegionProbe.CountGreen"/>）。</summary>
    private static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.CountGreenRef(f, x0, x1, y0, y1);
    /// <summary>未登录页右侧按钮列的焦点位置（见 <see cref="DetectNotLoggedInFocus"/>）。</summary>
    public enum LoginButtonFocus
    {
        /// <summary>两键都未聚焦（默认焦点在顶部页签「游戏主菜单」）。</summary>
        None,
        Login,
        ExitGame,
    }

    /// <summary>
    /// 未登录页焦点探针（实机 2026-10-21 用户视频逐帧）：右侧「登录/退出游戏」两键，
    /// 被聚焦的键整块变白（未聚焦为 #333 深灰），而页面默认焦点在顶部页签行——
    /// 这正是「直接按 A 无效、先按右会切到学院页签」的原因。
    /// 按行统计右半区（x 0.52~0.77 / y 0.47~0.70 覆盖两键）平均亮度，取最长亮行带：
    /// 亮带中心在按钮对上半 = 登录、下半 = 退出游戏、无亮带 = 默认。
    /// 用途：让 NotLoggedIn 的导航幂等——登录失败重试时按焦点状态选择 A / 上移+A /
    /// 下→右→A，避免盲目重跑序列时「下」落到退出游戏上被 A 触发。
    /// frame 为 null（部分单测）时按默认处理。
    /// </summary>
    public static LoginButtonFocus DetectNotLoggedInFocus(FrameData? frame)
    {
        if (frame is null) return LoginButtonFocus.None;
        var bgra = frame.Bgra;
        int w = frame.Width, h = frame.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return LoginButtonFocus.None;
        int x0 = (int)(w * 0.52), x1 = (int)(w * 0.77);
        int y0 = (int)(h * 0.47), y1 = (int)(h * 0.70);
        int span = Math.Max(1, x1 - x0);
        int bestStart = -1, bestLen = 0, runStart = -1;
        for (int y = y0; y < y1; y++)
        {
            long sum = 0;
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                sum += (bgra[i] * 114 + bgra[i + 1] * 587 + bgra[i + 2] * 299) / 1000;
            }
            bool bright = sum / span >= 140;
            if (bright)
            {
                if (runStart < 0) runStart = y;
                int len = y - runStart + 1;
                if (len > bestLen) { bestLen = len; bestStart = runStart; }
            }
            else
            {
                runStart = -1;
            }
        }
        // 聚焦按钮高约 5% 帧高；亮行太短多半是杂光/其他白色元素，按默认处理
        if (bestLen < (y1 - y0) / 10) return LoginButtonFocus.None;
        double centerY = (bestStart + bestLen / 2.0) / h;
        return centerY < 0.58 ? LoginButtonFocus.Login : LoginButtonFocus.ExitGame;
    }
}




