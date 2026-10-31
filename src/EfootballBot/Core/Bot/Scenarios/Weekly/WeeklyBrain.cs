using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

/// <summary>周活动菜单侧界面（WeeklyBrain 判定）。None = 非周活动界面，交给 MyLeagueBrain 分类。</summary>
public enum WkScreen
{
    /// <summary>非周活动界面（主菜单/比赛中/共用弹窗等）。</summary>
    None,
    /// <summary>活动列表页（对阵AI / 对阵玩家 两个页签）。</summary>
    EventsList,
    /// <summary>活动介绍页（巡回活动大图，光标默认在「进入」）。</summary>
    EventIntro,
    /// <summary>进入活动时的弹窗（「设置专用比赛计划」等，光标默认在确认）。</summary>
    EntryPopup,
    /// <summary>活动主页（左上「前往比赛」+ 右栏活动积分/主要奖励进度）。</summary>
    EventHome,
    /// <summary>活动主页左列「比赛历史」面板点开的赛事列表页（标题「比赛历史」在页首，
    /// 列表 + 底栏「B 返回」）。开赛前如果没有把焦点归位到「前往比赛」，A 会点开该页。</summary>
    MatchHistory,
    /// <summary>活动设置浮层（按 X 呼出，右半屏：赛事等级/自动控制/弹性自动控制）。</summary>
    EventSettings,
    /// <summary>左列「建议」被 A 点开后的建议面板（浅色大卡盖住主页中部，右上角「B 关闭」）。
    /// 列表视图标题「建议列表」；点开一条后是详情视图（只剩关闭标题 + 分析师署名）。</summary>
    AdvicePanel,
    /// <summary>挑战活动「挑战清单」弹窗（进入挑战活动后出现，01/02… 编号卡纵排，
    /// 已完成卡右上有勾、未解锁卡右下有锁）。挑战子流程的菜单侧。</summary>
    ChallengeList,
    /// <summary>单场周活动「选择赛事等级」整页（2026-10-30 用户截图 + issue #1）：页首大标题
    /// + 左列难度行（新手~传奇，每屏 6 行需下滚）+ 右栏该等级奖励（主要奖励/等级奖励）。
    /// 进入活动卡后先弹「请选择赛事等级」说明弹窗（OK），再在本页选等级开赛。</summary>
    EventDifficulty,
}

/// <summary>挑战清单里一张可见卡的解析结果。Index 为卡上大字编号（1 起）；
/// Slot 为屏幕卡位（0=左卡 1=右卡，翻页时同一卡位的卡会换）；Locked/Checked 为该卡位的像素探针。</summary>
public sealed record WkChallengeCard(int Index, int Slot, bool Locked, bool Checked);

/// <summary>活动设置浮层的解析结果。null = 该项 OCR 未读出（保守：读不到就不动）。</summary>
public sealed record WkActivitySettings(string? Level, bool? AutoControl, bool? FlexibleAutoControl)
{
    public bool AllReadable => Level is not null && AutoControl is not null && FlexibleAutoControl is not null;
}

/// <summary>
/// 周活动决策大脑：菜单侧界面分类 + 活动设置解析 + 完成态探针。纯函数，可单测。
/// 判定只用固定 UI 词（对阵AI/前往比赛/活动设置/自动控制…）与固定几何区；
/// 活动名/卡片图片/积分数字每周轮换，一律不作判定点（可变内容禁令）。
/// 比赛中与共用界面（InMatch/FullTime/弹窗推进等）全部复用 MyLeagueBrain。
/// </summary>
public static class WeeklyBrain
{
    // ---------------- 界面分类 ----------------

    /// <summary>
    /// 周活动界面分类。先于 MyLeagueBrain.Classify 执行——活动主页也含「前往比赛」，
    /// 不先判会被误归 LeaguePage（与关键比赛弹窗被 MatchHub 兜底抢判同一坑）。
    /// 判定顺序：挑战清单 → 建议面板 → 比赛历史 → 盖层弹窗 → 活动设置 → 入场弹窗 →
    /// 活动主页 → 活动列表 → 活动介绍。
    /// </summary>
    public static WkScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr,
        bool topRightHasContent = false, bool matchContext = false, bool mostlyLightPage = false)
    {
        // 挑战清单弹窗（挑战活动进入后的卡片列表）：标题「挑战清单」为强特征，最先判定。
        // 无标题兜底：卡位行两枚 H≥0.08 的大字卡号（普通文字 H≈0.04，只有卡编号这么高）
        // 分布在左右两卡位 + 底栏「返回」词。不先判会被共享分类兜成 LeaguePage（页面只有
        // btn_back，无可推进词 → 死等/瞎退）。标题词漏读时卡号特征防僵局。
        if (ocr.ContainsJoined("挑战清单") || HasChallengeListCardNumbers(ocr))
            return WkScreen.ChallengeList;

        // 建议面板（活动主页左列「建议」被 A 点开，实机 2026-10-28 反馈包）：浅色大卡盖住
        // 主页中部，但左列「前往比赛/比赛历史」「建议 有未读建议」与右栏「胜场积分」全部
        // 仍然可见 → 旧逻辑一直被 eventHomeBody 抢判成 EventHome，随后按 A（面板吃掉，或
        // 打开一条建议详情）和按 X（无活动设置）全部无效；「多次未生效」的 B 逃生也只把
        // 详情退成列表、退不回主页 → 三十轮原地打转、永远进不了比赛。
        // ★必须先于「盖层弹窗」——面板右上角「B 关闭」蓝色按钮会被 YOLO 判成 btn_confirm，
        // 落到弹窗门控会变 EntryPopup 继续盲按 A。
        if (IsAdvicePanel(ocr))
            return WkScreen.AdvicePanel;

        // 比赛历史页（活动主页左列「比赛历史」面板点开后）：页首大标题「比赛历史」+ 底栏「返回」。
        // ★必须在任何弹窗/主页判定之前——该页只有列表行与返回，没有推进词，落到共享分类会
        // 被 YOLO 误检的 btn_confirm（实机 2026-10-26 反馈包：0.63 @列表行）判成 HasConfirm，
        // 共享决策在不可推进的列表页上盲按 A 三十轮才 B 逃生 → 回主页又按 A 再进历史，死循环。
        // 活动主页的「比赛历史」是左列面板标签（y≈0.38），不在页首区，用 y&lt;0.22 区分；
        // 再用「无前往比赛」硬否决——主页恒有左上「前往比赛」，历史页没有，
        // 防活动改版把历史面板标签挪高后把主页误判成历史页（那会 B 退回列表死循环）。
        if (!ocr.ContainsJoined("前往比赛") && ocr.ContainsJoined("返回") &&
            ocr.Words.Any(w => w.CenterY < 0.22 && w.NormText.Contains("比赛历史")))
            return WkScreen.MatchHistory;

        // 单场周活动「选择赛事等级」整页（2026-10-30 用户三张截图 + GitHub issue #1 手机照：
        // 该页 wk=None、ml=DifficultyPage，周活动状态机无人接管 → 日志只刷「界面：DifficultyPage」
        // 原地干等，选不了难度）。★先于盖层弹窗门控——入场说明弹窗（请选择赛事等级+OK）盖在
        // 页上时标题与左列难度词仍然可读，落 popupOnTop 门控会因缺主页词判 None；
        // ★左列难度词必须限定 x&lt;0.25：活动设置浮层的赛事等级下拉（居中对话框）标题同名，
        // 靠 x 区分避免抢 LevelDropdown 阶段的下拉样式。
        if (IsEventDifficultyPage(ocr))
            return WkScreen.EventDifficulty;

        // 盖层弹窗探测（赛后结算/奖励领取等多步弹窗，用户 2026-10-12 实机教训）：
        // 主页词透过暗化背景仍会被 OCR 读到，误判 EventHome 后按 X/前往比赛全部无效
        //（弹窗挡着，被误判"设置打不开/开赛未生效"）。弹窗自带推进词或确认按钮，
        // 而纯主页没有（前往比赛不被 YOLO 判 btn_confirm、底栏无 OK/下一步/确认，实测帧验证）。
        // 命中 → 一律先按 A 清弹窗，清完才允许开始「已回到主页」的后续判断。
        //「OK」按整词精确匹配（FindButton），防英文球员名/卡面词含 "ok" 子串误触发。
        // YOLO btn_confirm 只认屏幕中部（y<0.85）：主页底栏「B 返回」的蓝色高亮框会被
        // 判成 btn_confirm（实机 Titani 帧 0.91 @(0.12,0.93)），把干净主页误门控成
        // EntryPopup → A 在主页上=前往比赛直接开下一场（实机：终场后 8s 进下一场、
        // 完成态从未被评估）。弹窗按钮都在屏幕中部，底栏确认框不算弹窗证据。
        bool popupOnTop =
            ocr.ContainsJoined("下一步") || ocr.ContainsJoined("确认") ||
            ocr.FindButton("OK") is not null ||
            dets.Any(d => d.ClassName == "btn_confirm" && d.Score >= MyLeagueBrain.MinScore && d.CenterY < 0.85);
        bool settingsTitle = ocr.Words.Any(w => w.CenterY < 0.2 && w.Text.Replace(" ", "").Contains("活动设置"));
        bool eventHomeBody = ocr.ContainsJoined("前往比赛") &&
            (ocr.ContainsJoined("总活动积分") || ocr.ContainsJoined("胜场积分") ||
             ocr.ContainsJoined("球员加分") || ocr.ContainsJoined("主要奖励"));
        bool eventsListTabs = HasMatchTabRow(ocr);
        bool introMark = ocr.ContainsJoined("进入") && ocr.ContainsJoined("详情");
        // 介绍页不参与弹窗门控：其白色「进入」按钮本身会被 YOLO 判 btn_confirm，
        // 而介绍页无论有无弹窗按 A 都是正确动作（光标默认在进入）
        if (popupOnTop && (settingsTitle || eventHomeBody || eventsListTabs))
            return WkScreen.EntryPopup;

        // 活动设置浮层（按 X 呼出，盖在活动主页右半屏）：标题「活动设置」在顶部（y<0.2）。
        // 必须先于活动主页——浮层打开时「前往比赛」仍在左侧可见。
        // 【实测坑】活动主页底栏有「X 活动设置」按钮词（y≈0.93），不限 y 会把主页误判成设置浮层
        if (settingsTitle)
            return WkScreen.EventSettings;

        // 入场弹窗（「设置专用比赛计划」+ OK；可能连续多枚）：先于活动主页——
        // 弹窗背后「前往比赛」仍可见，不先判会被 EventHome 抢走漏点确认
        if (ocr.ContainsJoined("专用比赛计划"))
            return WkScreen.EntryPopup;

        // 活动主页：左上「前往比赛」+ 右栏固定词（总活动积分/胜场积分/球员加分/主要奖励）。
        // 我的联赛主页没有这组词，与 LeaguePage 区分开
        if (eventHomeBody)
            return WkScreen.EventHome;

        // 活动列表页：顶部「对阵AI / 对阵玩家」页签（进入比赛 hub 的活动板块后默认页签为对阵AI）
        // ★页签词必须限定在顶部页签行（实机 y≈0.08）：介绍页正文写着「这是一个对阵AI活动…」
        // （实机 2026-10-29 反馈视频，y≈0.26），不限位置会让介绍页被判成列表页 → 场景层每轮
        // 「进入槽位 → ↓ 把焦点从进入挪到详情 → ←×6 → A」打开详情面板，活动永远进不去。
        if (eventsListTabs)
            return WkScreen.EventsList;

        // 活动介绍页（巡回活动大图 / 挑战活动大图）：「进入」+「详情」两枚按钮同屏。
        // ★只用「进入」+「详情」做判据，不用「巡回活动」——列表卡上也印着「巡回活动」标签
        //（实机列表帧 y≈0.23 三张卡各一枚），单靠该词会把列表页误判成介绍页。
        if (introMark)
            return WkScreen.EventIntro;

        return WkScreen.None;
    }

    /// <summary>
    /// 顶部页签行是否有「对阵AI / 对阵玩家」（活动列表页的页签）。
    /// ★必须限定 y&lt;0.16：介绍页正文写着「这是一个对阵AI活动…」（实机 2026-10-29 反馈视频
    /// 帧 y≈0.26），全局子串匹配会把介绍页误判成活动列表，场景层随后每轮 ↓ 把焦点从「进入」
    /// 挪到「详情」并反复 A 打开详情面板，活动永远进不去（日志「进入槽位 0」连刷二十余次）。
    /// 实机列表帧页签在 y≈0.08；页签词可能被逐字拆开，故用词级各自匹配而非整句拼接。
    /// </summary>
    private static bool HasMatchTabRow(OcrResult ocr)
        => ocr.Words.Any(w => w.CenterY < 0.16 &&
               (w.NormText.Contains("对阵AI", StringComparison.OrdinalIgnoreCase) ||
                w.NormText.Contains("对阵玩家")));

    /// <summary>
    /// 「建议」面板是否盖在活动主页上。只用固定 UI 文案，建议正文/日期/球员名是可变内容，不作判定点：
    /// 列表视图＝标题「建议列表」或副标题「显示最近5场比赛的建议。」；
    /// 详情视图＝右上角「关闭」标题（y&lt;0.16，主页没有该词）+ 左列「建议」/分析师署名。
    /// </summary>
    public static bool IsAdvicePanel(OcrResult ocr)
    {
        if (ocr.ContainsJoined("建议列表") || ocr.ContainsJoined("场比赛的建议")) return true;
        bool closeHeader = ocr.Words.Any(w => w.CenterY < 0.16 && w.NormText.Contains("关闭"));
        return closeHeader && (ocr.ContainsJoined("分析师") || ocr.ContainsJoined("建议"));
    }

    /// <summary>
    /// 单场周活动「选择赛事等级」整页判定：页首（y&lt;0.18）大标题「选择赛事等级」+
    /// 左列（x&lt;0.25）至少 2 个难度名。右栏同句「请选择赛事等级」在 y≈0.31、弹窗版在
    /// y≈0.40，都不入 y&lt;0.18 标题带，不冲突（实机三帧校准：标题 @(0.18,0.11)）。
    /// 标题被 OCR 漏读/遮挡（issue #1 手机照：GUI 覆盖层盖住列表）时本判定落空，
    /// 由共享分类兜成 MlScreen.DifficultyPage——场景层对两个信号都接管。
    /// </summary>
    public static bool IsEventDifficultyPage(OcrResult ocr) =>
        ocr.Words.Any(w => w.CenterY < 0.18 && w.Text.Replace(" ", "").Contains("选择赛事等级")) &&
        MyLeagueBrain.Difficulties.Count(d => ocr.Words.Any(w =>
            w.CenterX < 0.25 && w.Text.Replace(" ", "").Contains(d))) >= 2;

    /// <summary>
    /// 检测难度列表当前聚焦行（光标）：左列难度词行带（词左 -0.28 ~ 词右 +0.06、y±0.035）
    /// 的亮像素（lum&gt;200）计数——白底高亮行 ≈1283/1285，未聚焦行 29~81，说明弹窗盖着时
    /// 全 0（实机 2026-10-30 三帧校准，阈值 400）。返回 null = 未检出（弹窗遮挡/探针失效）。
    /// </summary>
    public static string? DetectFocusedDifficulty(FrameData frame, OcrResult ocr)
    {
        var (w, h, bgra) = (frame.Width, frame.Height, frame.Bgra);
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;

        string? best = null;
        int bestBright = 0;
        foreach (var diff in MyLeagueBrain.Difficulties)
        {
            var word = ocr.Words.FirstOrDefault(x =>
                x.CenterX < 0.25 && x.Text.Replace(" ", "").Contains(diff));
            if (word is null) continue;
            int bright = 0;
            double x0 = Math.Max(0, word.CenterX - word.W / 2 - 0.28);
            double x1 = Math.Min(1, word.CenterX + word.W / 2 + 0.06);
            for (int y = (int)((word.CenterY - 0.035) * h); y < (int)((word.CenterY + 0.035) * h); y += 2)
            {
                if (y < 0 || y >= h) continue;
                for (int x = (int)(x0 * w); x < (int)(x1 * w); x += 2)
                {
                    int i = (y * w + x) * 4;
                    if (((bgra[i + 2] + bgra[i + 1] + bgra[i]) / 3) > 200) bright++;
                }
            }
            if (bright > bestBright) { bestBright = bright; best = diff; }
        }
        return bestBright >= 400 ? best : null;
    }

    /// <summary>
    /// 难度页的最终确认弹窗：「是否要选择&lt;难度名&gt;？」+ 纵排按钮 进入(上)/取消(下)，
    /// ★默认焦点在「取消」——直接按 A 会取消回列表（用户 2026-10-30 实机）。必须 ↑ 后再 A。
    /// 判定：说明句「是否要选择」；或屏中部纵排「进入」+「取消」双词（x 0.4~0.6）兜底，
    /// 防难度名被 OCR 拼进行（实机帧读成「标准是否要选择传奇奖励」整行）时漏判。
    /// </summary>
    public static bool IsDifficultyConfirmPopup(OcrResult ocr) =>
        ocr.ContainsJoined("是否要选择") ||
        (ocr.Words.Any(w => w.CenterX is > 0.4 and < 0.6 && w.CenterY is > 0.45 and < 0.75 &&
                            w.NormText.Contains("取消")) &&
         ocr.Words.Any(w => w.CenterX is > 0.4 and < 0.6 && w.CenterY is > 0.45 and < 0.62 &&
                            w.NormText.Contains("进入")));

    /// <summary>
    /// 挑战清单兜底特征：卡号带（y 0.15~0.40、x&lt;0.72）里左右两卡位各有一枚
    /// H≥0.08 的两位数字大字（挑战清单卡编号，实机 challenge_list 帧：01 H=0.125 @x0.11、
    /// 02 H=0.117 @x0.42；普通文字 H≈0.04）+ 底栏「返回」词。O/0 归一防 "O1" 漏检。
    /// </summary>
    private static bool HasChallengeListCardNumbers(OcrResult ocr)
    {
        bool left = false, right = false;
        foreach (var w in ocr.Words)
        {
            if (w.H < 0.08 || w.CenterY < 0.15 || w.CenterY > 0.40 || w.CenterX >= 0.72) continue;
            var t = w.NormText.ToUpper().Replace("O", "0");
            if (t.Length != 2 || !t.All(char.IsDigit) || t[0] == '0' && t[1] == '0') continue;
            if (w.CenterX < 0.33) left = true; else right = true;
        }
        return left && right && ocr.ContainsJoined("返回");
    }

    // ---------------- 挑战清单卡片扫描 ----------------

    /// <summary>卡位右缘 x 坐标（与活动列表卡行同几何；实机 challenge_list 帧：
    /// 卡1 右缘≈0.351、卡2 右缘≈0.637，锁定图标在卡右下角）。</summary>
    private static readonly double[] ChallengeSlotRights = { 0.352, 0.645 };

    /// <summary>
    /// 解析挑战清单当前可见卡：OCR 大字卡号给出「哪张卡在哪个卡位」，再对该卡位做
    /// 锁（右下角灰锁图标）/勾（右上角绿勾）像素探针。返回按卡号升序的可见卡列表；
    /// 翻页后同一卡位会换卡号，调用方按 Index 跟踪扫描游标。
    /// </summary>
    public static List<WkChallengeCard> ScanChallengeCards(OcrResult ocr, FrameData frame)
    {
        var list = new List<WkChallengeCard>();
        foreach (var w in ocr.Words)
        {
            if (w.H < 0.08 || w.CenterY < 0.15 || w.CenterY > 0.40 || w.CenterX >= 0.72) continue;
            var t = w.NormText.ToUpper().Replace("O", "0");
            if (t.Length != 2 || !t.All(char.IsDigit)) continue;
            int index = int.Parse(t);
            if (index < 1 || list.Any(c => c.Index == index)) continue;
            int slot = w.CenterX < 0.33 ? 0 : 1;
            list.Add(new WkChallengeCard(index, slot,
                DetectChallengeLock(frame, slot), DetectChallengeCheck(frame, slot)));
        }
        return list.OrderBy(c => c.Index).ToList();
    }

    /// <summary>
    /// 挑战清单卡右下角灰色锁图标（该卡未解锁）：灰亮像素（三通道接近、亮度 90~220）
    /// 计数 ≥25。实机 challenge_list 帧：锁区 44 灰像素，白卡同位 3（白底亮度 &gt;220 不计）。
    /// </summary>
    public static bool DetectChallengeLock(FrameData frame, int slot)
    {
        if (slot < 0 || slot >= ChallengeSlotRights.Length) return false;
        double right = ChallengeSlotRights[slot];
        return CountGray(frame, right - 0.055, right - 0.005, 0.760, 0.850) >= 25;
    }

    /// <summary>
    /// 挑战清单卡右上角完成勾（该卡已通关）：绿色像素计数 ≥40（绿底白勾同活动列表
    /// 绿勾探针）。⚠ 勾区像素特征尚未拿到实机完成态截图校准——漏检由场景层
    /// 「已通关卡记忆」兜底（进卡页读到 胜场 N/N 即记完成），误检无副作用（跳过卡而已）。
    /// </summary>
    public static bool DetectChallengeCheck(FrameData frame, int slot)
    {
        if (slot < 0 || slot >= ChallengeSlotRights.Length) return false;
        double right = ChallengeSlotRights[slot];
        return CountGreen(frame, right - 0.080, right - 0.005, 0.160, 0.280) >= 40;
    }

    /// <summary>区域内灰色像素计数（共享实现见 <see cref="RegionProbe.CountGray"/>）。</summary>
    private static int CountGray(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.CountGrayRef(f, x0, x1, y0, y1);

    /// <summary>
    /// 挑战卡详情页（活动主页样式）通关判定：右栏「胜场」计分器读满——
    /// 右上区（x&gt;0.65、y&lt;0.35）出现各位数字全等的纯数字词（"1/1" 经 Norm 去标点成 "11"，
    /// 进行中 "0/1"→"01" 首位 0 不命中；"2/2"→"22" 同理）。区域限定防误吃页面其他数字；
    /// 正常周活动主页右栏是积分值（2030/330 等非等位数），永不命中。
    /// </summary>
    public static bool ChallengeProgressDone(OcrResult ocr)
        => ocr.Words.Any(w => w.CenterX > 0.65 && w.CenterY < 0.35 && IsFullProgress(w.NormText));

    private static bool IsFullProgress(string norm)
        => norm.Length is >= 2 and <= 4 && norm.All(char.IsDigit) &&
           norm.All(c => c == norm[0]) && norm[0] != '0';

    /// <summary>
    /// 是否挑战卡详情页（「完成的条件」标题行，正常周活动主页没有该词——实机两帧对照验证）。
    /// 用于半途启动时的挑战模式识别（直接落在卡详情页，没经过挑战清单）。
    /// </summary>
    public static bool IsChallengeCardHome(OcrResult ocr) => ocr.ContainsJoined("完成的条件");

    /// <summary>挑战清单扫描中，游标卡号从屏上消失后的处置决策。</summary>
    public enum ChVanishDecision
    {
        /// <summary>消失未满两帧：翻页/进入动画丢帧，继续观察。</summary>
        WaitFrame,
        /// <summary>重扫确证后再次消失且游标已越过已通关卡：表尾，全部挑战完成。</summary>
        ConfirmTail,
        /// <summary>无法确证表尾：重新饱和左移从头扫一遍。</summary>
        Rescan,
        /// <summary>反复消失且始终无法确证：放弃本活动（拉黑槽位）。</summary>
        Abort,
    }

    /// <summary>
    /// 游标卡号消失决策（纯函数）。实机 2026-10-17 教训：刚对一张无勾无锁的卡按过 A，
    /// 详情页会被卡号兜底误判成清单、且详情页上找不到卡号——旧逻辑凭 2 帧消失直接
    /// 假报「全部挑战已完成」，整活动被跳过。因此「全完成」必须同时满足：
    /// 游标最后一次可见状态是已通关（越过已勾卡后的消失才可能是表尾）
    /// 且已经重扫确证过一次。反复消失无法确证则放弃，绝不假报完成。
    /// </summary>
    public static ChVanishDecision DecideChallengeVanish(int missingFrames, bool cursorDone, int rescans)
    {
        if (missingFrames < 2) return ChVanishDecision.WaitFrame;
        if (cursorDone && rescans >= 1) return ChVanishDecision.ConfirmTail;
        if (rescans >= 3) return ChVanishDecision.Abort;
        return ChVanishDecision.Rescan;
    }



    /// <summary>在右半屏（x&gt;0.45）找标签词下方的值词（值在标签下一行，y 偏移 0.015~0.13）。</summary>
    private static OcrWord? ValueBelow(OcrResult ocr, double labelY, Func<string, bool> match)
        => ocr.Words.FirstOrDefault(w =>
            w.CenterX > 0.45 && w.CenterY > labelY + 0.015 && w.CenterY < labelY + 0.13 &&
            match(w.Text.Replace(" ", "")));

    /// <summary>
    /// 解析活动设置浮层当前值：赛事等级（难度词）/ 自动控制（开/关）/ 弹性自动控制（开/关）。
    /// 任何一项读不到返回 null——调用方保守处理（绝不盲按，防误翻转）。
    /// </summary>
    public static WkActivitySettings ParseActivitySettings(OcrResult ocr)
    {
        // 「弹性自动控制」包含「自动控制」子串，先匹配弹性行再匹配普通行，普通行要求整行就是「自动控制」。
        // 行级匹配失败时回退词级：挑战活动设置浮层较窄，背后卡详情页的同高文字会并进同一
        // 排版行（实机 challenge_settings 帧：自动控制行读成「完成的条件自动控制」，整行
        // 精确匹配落空），而词本身仍是独立词「自动控制」。
        var flexLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "").Contains("弹性"));
        var autoLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "") == "自动控制");
        var levelLabel = ocr.Lines.FirstOrDefault(l => l.Text.Replace(" ", "").Contains("赛事等级"));
        double? autoY = autoLabel?.CenterY ?? LabelWordY(ocr, "自动控制", exact: true);
        double? flexY = flexLabel?.CenterY ?? LabelWordY(ocr, "弹性", exact: false);
        double? levelY = levelLabel?.CenterY ?? LabelWordY(ocr, "赛事等级", exact: false);

        string? level = null;
        if (levelY is not null)
        {
            var v = ValueBelow(ocr, levelY.Value, t => MyLeagueBrain.Difficulties.Any(d => t.Contains(d)));
            if (v is not null)
                level = MyLeagueBrain.Difficulties.First(d => v.Text.Replace(" ", "").Contains(d));
        }

        bool? ParseSwitch(double? labelY)
        {
            if (labelY is null) return null;
            var v = ValueBelow(ocr, labelY.Value, t => t == "开" || t == "关");
            return v is null ? null : v.Text.Replace(" ", "") == "开";
        }

        return new WkActivitySettings(level, ParseSwitch(autoY), ParseSwitch(flexY));
    }

    /// <summary>词级标签回退：找文本匹配的独立词的纵坐标。exact=true 要求整词相等
    /// （「自动控制」不得命中「弹性自动控制」词），false 为子串包含。</summary>
    private static double? LabelWordY(OcrResult ocr, string keyword, bool exact)
        => ocr.Words
            .FirstOrDefault(w => exact
                ? w.NormText == keyword
                : w.NormText.Contains(keyword))
            ?.CenterY;

    // ---------------- 完成态探针（2026-10-13 用户实机截图校准） ----------------

    /// <summary>活动卡右缘 x 坐标（槽位 0~3；实机 weekly_list_greencheck 帧：卡1 右缘≈0.351）。
    /// 第 4 卡大部分在屏幕外，勾区采样不到自然返回 false（进入后由主页完成态判定兜底）。</summary>
    private static readonly double[] CardRightEdges = { 0.352, 0.643, 0.946, 1.24 };

    /// <summary>区域内绿色像素计数（共享实现见 <see cref="RegionProbe.CountGreen"/>）。</summary>
    private static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.CountGreenRef(f, x0, x1, y0, y1);

    /// <summary>
    /// 活动列表卡右上角绿色勾（该活动主要奖励已完成，不进入）：勾形徽章固定在卡右上角。
    /// 实机 weekly_list_greencheck 帧：卡1 勾区 157 绿像素，未完成卡 0——阈值 50。
    /// </summary>
    public static bool DetectGreenCheck(FrameData frame, int slot)
    {
        if (slot < 0 || slot >= CardRightEdges.Length) return false;
        double right = CardRightEdges[slot];
        return CountGreen(frame, right - 0.065, right - 0.005, 0.150, 0.220) >= 50;
    }

    /// <summary>
    /// 活动主页右栏「主要奖励已获得」：
    /// ① OCR「获得的主要奖励」（完成态标题；进行中是「离获得主要奖励还差…」，无「的」字，
    ///   子串天然不冲突）；
    /// ② 备份：右栏主要奖励行的绿色勾（实机 weekly_reward_done 帧：勾区 100 绿像素；
    ///   注意别把左侧代币图标圈进来——图标在 x≈0.70，勾在 x≈0.94）。
    /// </summary>
    public static bool IsMainRewardDone(OcrResult ocr, FrameData frame)
        => ocr.ContainsJoined("获得的主要奖励") ||
           CountGreen(frame, 0.920, 0.965, 0.635, 0.715) >= 50;

    /// <summary>右栏是否显示「离获得主要奖励还差…」（进行中，可继续刷）——仅用于日志与守卫。</summary>
    public static bool MainRewardPending(OcrResult ocr) => ocr.ContainsJoined("离获得主要奖励");

    /// <summary>难度档位（与我的联赛「选择赛事等级」共用一套词，周活动赛事等级下拉一致）。</summary>
    public static string[] Levels => MyLeagueBrain.Difficulties;
}
