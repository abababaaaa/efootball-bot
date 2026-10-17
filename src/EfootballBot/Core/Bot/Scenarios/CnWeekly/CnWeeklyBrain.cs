using System.Text.RegularExpressions;
using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Bot.Scenarios.Weekly;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

/// <summary>国服周活动界面（CnWeeklyBrain 判定）。None = 非国服周活动界面，交给 MyLeagueBrain 分类。</summary>
public enum CwScreen
{
    /// <summary>非国服周活动界面（比赛中/终场/共用弹窗等）。</summary>
    None,
    /// <summary>国服主页·推荐页签（顶部 推荐/比赛/签约/商店 + 天梯巅峰赛/活动中心 等磁贴）。</summary>
    HomeRecommended,
    /// <summary>国服主页·比赛页签（顶部页签 + 比赛活动/天梯巅峰赛/我的联赛/好友对战 卡片）。</summary>
    HomeMatch,
    /// <summary>比赛活动列表（对阵电脑 / 对阵玩家 两个页签 + 活动卡）。</summary>
    EventsList,
    /// <summary>活动介绍页（活动大图 + 进入/详情 按钮 + B 关闭）。</summary>
    EventIntro,
    /// <summary>对阵表页（小组赛…决赛 页签 + X 重置 + RT 下一步）。</summary>
    Bracket,
    /// <summary>活动主页（左 球队阵容/比赛历史/建议 + 右 下场比赛/下个对手 + RT 教练入场）。</summary>
    EventHome,
    /// <summary>球队阵容页（球队阵容列表 + 球员加分 + B 完成 + RS 自动配置球员）。</summary>
    Squad,
    /// <summary>RS 自动配置球员菜单（无变更/重视能力值/活动加成，盖在阵容页上）。</summary>
    SquadMenu,
    /// <summary>巡回赛型活动主页（球星训练等，2026-10-17 实机：主按钮 前往比赛 + 总活动积分/
    /// 比赛历史/建议，底栏 B 返回 + X 活动设置；无杯赛型的 教练入场/球队阵容）。</summary>
    EventHomeTour,
    /// <summary>活动设置面板（巡回赛主页 X 打开的右侧盖层：选择的球队/教练模式/教练模式内控制，
    /// 底部 B 完成。主页词透过暗化背景仍可读，必须先于主页判定）。</summary>
    EventSettings,
    /// <summary>挑战型活动「挑战清单」选卡页（2026-10-17 实机 cn_challenge_list：标题 挑战清单 +
    /// 01/02… 编号卡横排一屏 3 张，已通关卡右上绿勾、未解锁卡右下灰锁，底栏 B 返回）。
    /// 挑战子流程的菜单侧，归位第一张后逐卡右扫。</summary>
    ChallengeList,
    /// <summary>「选择比赛难度」页（2026-10-17 实机 cn_tour_difficulty：前往比赛后弹出，5 张
    /// 难度卡横排 + 底栏 B 返回/RT 确定；光标所在卡白底高亮=当前所选）。与我的联赛
    /// 「选择赛事等级」不同词不同布局，须独立判定。</summary>
    Difficulty,
    /// <summary>巡回赛主页 i 图标打开的信息弹窗（2026-10-17 实机 cn_tour_reward_popup：
    /// 白底大面板，主要奖励 N/M 进度 + 活动时间/活动说明，右上 B 关闭）。主要奖励是否
    /// 拿满以此弹窗内 x/x 为准——主页勾只表示已有主要奖励可领，不代表拿满。</summary>
    RewardPopup,
}

/// <summary>
/// 国服周活动决策大脑（菜单侧）：界面分类。纯函数，可单测。
/// 判定只用固定 UI 词（对阵电脑/教练入场/前往比赛/活动设置/球队阵容列表/无变更/活动加成…）
/// 与固定几何；活动名（如「实况足球 冠军赛」「球星训练 巡回赛」）、奖励内容、对手队名每周轮换，
/// 一律不作判定点（可变内容禁令）。比赛中与共用界面（InMatch/FullTime/弹窗推进等）全部复用
/// MyLeagueBrain。国服菜单全程 OCR 判定（国服 YOLO 模型未训练，btn_confirm 仅作弹窗门控辅助证据）。
/// </summary>
public static class CnWeeklyBrain
{
    // ---------------- 界面分类 ----------------

    /// <summary>
    /// 国服周活动界面分类。先于 MyLeagueBrain.Classify 执行。
    /// 判定顺序：盖层弹窗门控 → i 信息弹窗 → 挑战清单 → 选择比赛难度 → RS 菜单
    /// （盖在阵容页上）→ 阵容页 → 活动设置面板（盖在巡回赛主页右半）→ 巡回赛型活动主页
    /// → 杯赛型活动主页 → 对阵表 → 活动介绍 → 活动列表 → 主页（比赛页签 → 推荐页签）。
    /// </summary>
    public static CwScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr)
    {
        bool squadBody = ocr.ContainsJoined("球队阵容列表") || ocr.ContainsJoined("自动配置球员");
        bool eventHomeBody = ocr.ContainsJoined("教练入场") ||
            (ocr.ContainsJoined("比赛历史") && ocr.ContainsJoined("下场比赛"));
        // 巡回赛型（2026-10-17 实机 cn_tour_event_settings.png）：主页=前往比赛+底栏「X 活动设置」；
        // 设置面板=教练模式 +（选择的球队|完全托管）。两代主页都含「比赛历史」，面板盖住主页右半，
        // 故面板与巡回赛主页都必须先于杯赛型主页判定
        bool settingsBody = ocr.ContainsJoined("教练模式") &&
            (ocr.ContainsJoined("选择的球队") || ocr.ContainsJoined("完全托管"));
        bool tourHomeBody = ocr.ContainsJoined("前往比赛") && ocr.ContainsJoined("活动设置");
        bool bracketBody = ocr.ContainsJoined("重置") || ocr.ContainsJoined("冠军杯");
        bool listBody = ocr.ContainsJoined("对阵电脑") && ocr.ContainsJoined("对阵玩家");
        bool introBody = ocr.ContainsJoined("详情") && ocr.ContainsJoined("进入");
        // 挑战清单选卡页：标题「挑战清单」强特征；标题漏读时大字卡号带+底栏返回兜底
        bool challengeBody = ocr.ContainsJoined("挑战清单") || HasChallengeListCardNumbers(ocr);
        // 选择比赛难度页：标题强特征；标题漏读时 同行 ≥3 个难度名 + 确定词 兜底
        bool difficultyBody = ocr.ContainsJoined("选择比赛难度") || HasCnDifficultyCardRow(ocr);
        // i 信息弹窗（主要奖励 x/x）：主要奖励 头 + 活动说明/活动时间（主页右栏虽有
        // 获得的主要奖励 字样透底，但无 活动说明/活动时间，不冲突）
        bool rewardPopupBody = ocr.ContainsJoined("主要奖励") &&
            (ocr.ContainsJoined("活动说明") || ocr.ContainsJoined("活动时间"));

        // 盖层弹窗门控（同国际服周活动实机教训）：主页词透过暗化背景仍会被 OCR 读到，
        // 误判回菜单页会把无效按键当"未生效"反复重按。弹窗自带推进词（下一步/确认/整词OK，
        // 或 YOLO 检出确认按钮）且背景有周活动界面证据 → 交共享流程按 A 清弹窗。
        // 推进词必须在屏幕中部（y&lt;0.82）：对阵表底栏「RT 下一步」、活动主页底栏等
        // 自带按钮词在 y≈0.93，不限 y 会把干净的对阵表/主页全门控成弹窗（对阵表会因此
        // 被判 None → 共享流程 Unknown → B 逃生退回介绍页，死循环）。
        bool popupOnTop = CenterPopupWord(ocr) ||
            dets.Any(d => d.ClassName == "btn_confirm" && d.Score >= MyLeagueBrain.MinScore && d.CenterY < 0.82);
        if (popupOnTop && (squadBody || eventHomeBody || tourHomeBody || settingsBody ||
                           bracketBody || listBody || introBody || challengeBody || difficultyBody))
            return CwScreen.None;

        // i 信息弹窗：盖在巡回赛主页上的白底大面板，先于主页判定（主页左半被盖，
        // 前往比赛 不可见，但保险起见仍先判）
        if (rewardPopupBody)
            return CwScreen.RewardPopup;

        // 挑战清单选卡页：必须先于其余判定——页面只有底栏「返回」，落 None 会被共享分类
        // 盲等/瞎退（用户 2026-10-17 实机卡死同款）。标题词漏读时卡号特征防僵局
        if (challengeBody)
            return CwScreen.ChallengeList;

        // 选择比赛难度页：底栏 B 返回/RT 确定，落 None 会被共享分类当 LeaguePage 卡死
        //（等不到推进词 → B 退回 → 前往比赛 → 本页，死循环）。标题词漏读时难度卡行兜底
        if (difficultyBody)
            return CwScreen.Difficulty;

        // RS 自动配置球员菜单：盖在阵容页上（无变更/活动加成 为菜单专属词），必须先于阵容页
        if (ocr.ContainsJoined("无变更") && ocr.ContainsJoined("活动加成"))
            return CwScreen.SquadMenu;

        // 球队阵容页：左上「球队阵容列表」或底栏「RS 自动配置球员」（球员加分两页都有，不作主判据）
        if (squadBody)
            return CwScreen.Squad;

        // 活动设置面板：盖在巡回赛主页右半，主页词透背景仍可读，先于两个主页判定
        if (settingsBody)
            return CwScreen.EventSettings;

        // 巡回赛型活动主页：主按钮「前往比赛」+ 底栏「X 活动设置」（杯赛型主页没有这两个词；
        // 我的联赛主页虽有前往比赛但无活动设置，不冲突）。先于杯赛型主页——两代都含「比赛历史」，
        // 巡回赛主页右侧若也出现「下场比赛」会误中杯赛判定
        if (tourHomeBody)
            return CwScreen.EventHomeTour;

        // 活动主页：底栏「RT 教练入场」（周活动专属词）；左栏 球队阵容/比赛历史/建议 磁贴
        // 与右栏「下场比赛」为备份（教练入场 OCR 漏读时）
        if (eventHomeBody)
            return CwScreen.EventHome;

        // 对阵表页：底栏「X 重置」独有；「冠军杯」页签徽章备份
        if (bracketBody)
            return CwScreen.Bracket;

        // 活动介绍页：进入 + 详情 按钮同屏（列表卡只有「进入」，无「详情」）
        if (introBody)
            return CwScreen.EventIntro;

        // 比赛活动列表：顶部「对阵电脑 / 对阵玩家」页签
        if (listBody)
            return CwScreen.EventsList;

        // 国服主页：顶部页签行 推荐/比赛/签约/商店（同一行，y&lt;0.15），按内容词分页签
        if (HasTopTabs(ocr))
        {
            if (ocr.ContainsJoined("好友对战") || ocr.ContainsJoined("我的联赛") || ocr.ContainsJoined("比赛活动"))
                return CwScreen.HomeMatch;
            if (ocr.ContainsJoined("活动中心") || ocr.ContainsJoined("我的球队") || ocr.ContainsJoined("设置"))
                return CwScreen.HomeRecommended;
        }

        return CwScreen.None;
    }

    /// <summary>屏幕中部（y&lt;0.82）的弹窗推进词：下一步/确认/确定，或整词 OK（防英文词含 ok 子串）。</summary>
    private static bool CenterPopupWord(OcrResult ocr)
    {
        foreach (var w in ocr.MergedWords)
        {
            if (w.CenterY >= 0.82) continue;
            string t = w.Text.Replace(" ", "");
            if (t is "下一步" or "确认" or "确定") return true;
            if (t.Equals("OK", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>顶部页签行（y&lt;0.15 的词拼接）是否同时含 推荐/签约/商店。</summary>
    public static bool HasTopTabs(OcrResult ocr)
    {
        string top = string.Join("|", ocr.Words
            .Where(w => w.CenterY < 0.15)
            .Select(w => w.Text.Replace(" ", "")));
        return top.Contains("推荐") && top.Contains("签约") && top.Contains("商店");
    }

    /// <summary>RS 菜单中「活动加成」选项行号（0 起，自上而下：无变更/重视能力值/重视能力值心有灵犀/活动加成）。
    /// 光标默认在第 1 项「无变更」（实机帧验证），按 Down×3 到「活动加成」。</summary>
    public const int EventBonusOptionIndex = 3;

    /// <summary>活动主页左栏磁贴固定行序（球队阵容/比赛历史/建议）。光标默认在第 1 项「球队阵容」，
    /// 按 A 直接进入阵容页（实机帧：球队阵容磁贴白色高亮为默认焦点）。</summary>
    public const int SquadTileIndex = 0;

    // ---------------- 巡回赛活动设置核验（只读） ----------------

    /// <summary>
    /// 活动设置面板是否已满足全自动开赛（实机 2026-10-17 cn_tour_event_settings.png）：
    /// 教练模式=开 且 教练模式内控制=完全托管。值的排版为标签行下方独立 pill 行，
    /// 「开」是单字词；OCR 把标签与值并成一行（「教练模式开」）时同样认得出（含 内控制
    /// 的是下面那行控制设置，须排除）。只读不改——面板里盲按 A 可能落在「选择的球队」上
    /// 误开换队。OCR 漏读「开」会误报未就绪：只影响提示，不触发任何按键。
    /// </summary>
    public static bool TourSettingsReady(OcrResult ocr)
    {
        if (!ocr.ContainsJoined("教练模式") || !ocr.ContainsJoined("完全托管"))
            return false;
        foreach (var w in ocr.MergedWords)
        {
            string t = OcrText.Norm(w.Text);
            if (t == "开") return true;
            if (t.Contains("教练模式") && !t.Contains("内控制") && t.Contains("开")) return true;
        }
        return false;
    }

    // ---------------- 挑战清单选卡页（2026-10-17 实机 cn_challenge_list.png） ----------------
    // 布局不固定卡数：国服当前一屏 3 张、国际服 2 张，都可能更多（用户明示 5 卡/多卡布局
    // 存在）。一律以 OCR 大字卡号自身动态定位：卡号在卡左上角，卡右缘 ≈ 下一张卡号位置
    // （最后一张用相邻间距外推），锁/勾探针在卡内动态取区，不绑定任何固定卡位坐标。

    /// <summary>卡号大字判定：H≥0.075（国服 3 卡布局实机 H≈0.09，普通文字 H≈0.05）、
    /// y 0.15~0.40、x&lt;0.95（出屏残卡的窄边碎片不取）的两位数字。O/0 归一防 "O1" 漏检。</summary>
    private static bool IsChallengeCardNumber(OcrWord w, out int index)
    {
        index = 0;
        if (w.H < 0.075 || w.CenterY < 0.15 || w.CenterY > 0.40 || w.CenterX >= 0.95) return false;
        var t = OcrText.Norm(w.Text).ToUpper().Replace("O", "0");
        if (t.Length != 2 || !t.All(char.IsDigit)) return false;
        index = int.Parse(t);
        return index >= 1;
    }

    /// <summary>卡号中心到卡左缘的内边距（实机 cn_challenge_list 帧：卡号 x=0.42、
    /// 卡右缘=0.348 → 内边距≈0.072）。同一内边距从下一张卡号反推本卡右缘。
    /// ⚠ 反推值偏大即探针扫到卡边框描边（亮卡边框有大量 90~220 灰过渡像素）误报锁，
    /// 探针右界须再内收 0.01 兜住推导误差。</summary>
    private const double ChallengeNumberInset = 0.07;

    /// <summary>
    /// 挑战清单兜底特征：卡号带里至少两枚大字卡号 + 底栏「返回」。任意卡数布局通用。
    /// </summary>
    private static bool HasChallengeListCardNumbers(OcrResult ocr)
    {
        int count = 0;
        foreach (var w in ocr.Words)
            if (IsChallengeCardNumber(w, out _)) count++;
        return count >= 2 && ocr.ContainsJoined("返回");
    }

    /// <summary>可见卡的动态几何：卡号词按 x 排序定左右次序（Slot），卡右缘取下一张卡号
    /// 减内边距，最后一张用相邻间距外推。返回 (卡号, 次序, 右缘, 左缘) 列表。</summary>
    private static List<(int Index, int Slot, double Right, double Left)> ChallengeCardBounds(
        OcrResult ocr)
    {
        var nums = new List<(int Index, double X)>();
        foreach (var w in ocr.Words)
            if (IsChallengeCardNumber(w, out int idx) && !nums.Any(n => n.Index == idx))
                nums.Add((idx, w.CenterX));
        var ordered = nums.OrderBy(n => n.X).ToList();
        var bounds = new List<(int, int, double, double)>();
        for (int i = 0; i < ordered.Count; i++)
        {
            double pitch = i + 1 < ordered.Count ? ordered[i + 1].X - ordered[i].X : pitchDefault(ordered);
            double right = i + 1 < ordered.Count
                ? ordered[i + 1].X - ChallengeNumberInset
                : ordered[i].X + pitch - ChallengeNumberInset;
            bounds.Add((ordered[i].Index, i, right, ordered[i].X - ChallengeNumberInset));
        }
        return bounds;

        double pitchDefault(List<(int Index, double X)> list)
            => list.Count >= 2 ? list[^1].X - list[^2].X : 0.30;
    }

    /// <summary>
    /// 解析挑战清单当前可见卡：OCR 大字卡号给出「哪张卡在屏幕哪个位置」，再对该卡的
    /// 动态区域做锁（右下角灰锁图标）/勾（右上角绿勾）像素探针。返回按卡号升序的可见卡
    /// 列表（Slot=屏上左右次序）；翻页后同一位置会换卡号，调用方按 Index 跟踪扫描游标。
    /// 卡记录复用国际服 WkChallengeCard（Index/Slot/Locked/Checked 同义）。
    /// </summary>
    public static List<WkChallengeCard> ScanChallengeCards(OcrResult ocr, FrameData frame)
    {
        var list = new List<WkChallengeCard>();
        foreach (var (index, slot, right, left) in ChallengeCardBounds(ocr))
        {
            // 锁：卡右下角。探针取卡右缘内侧一窄条（右界内收 0.01 兜住右缘推导误差、
            // 下界 0.85 抬离卡底边描边；宽不足时收缩到卡内），灰亮像素（三通道接近、
            // 亮度 90~220）——锁卡片深灰底（亮度&lt;90）与白卡（亮度&gt;220）都不计，
            // 只有灰锁图标命中
            double lx1 = right - 0.01, lx0 = Math.Max(left + 0.02, right - 0.12);
            bool locked = CountGray(frame, lx0, lx1, 0.700, 0.850) >= 25;
            // 勾：卡右上角完成徽章（绿底白勾）。绿像素计数 ≥40；⚠ 完成态特征尚未拿到
            // 实机截图校准——漏检由场景层「已通关卡记忆」兜底，误检无副作用（跳过卡而已）
            bool check = CountGreen(frame, Math.Max(left + 0.02, right - 0.10), right - 0.01, 0.150, 0.300) >= 40;
            list.Add(new WkChallengeCard(index, slot, locked, check));
        }
        return list.OrderBy(c => c.Index).ToList();
    }

    /// <summary>区域内灰色像素计数（R≈G≈B、亮度 90~220；2px 步进采样）。</summary>
    private static int CountGray(FrameData f, double x0, double x1, double y0, double y1)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return 0;
        int count = 0;
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
            {
                int i = (y * w + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int lum = (r + g + b) / 3;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx - mn < 30 && lum >= 90 && lum <= 220) count++;
            }
        return count;
    }

    /// <summary>区域内绿色像素计数（绿底白勾：G&gt;100 且显著高于 R/B；2px 步进采样）。</summary>
    private static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return 0;
        int count = 0;
        for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
            for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
            {
                int i = (y * w + x) * 4;
                if (bgra[i + 1] > 100 && bgra[i + 1] > bgra[i + 2] + 40 && bgra[i + 1] > bgra[i] + 40) count++;
            }
        return count;
    }

    // ---------------- 选择比赛难度页（2026-10-17 实机 cn_tour_difficulty.png） ----------------

    /// <summary>难度卡名前缀 → 规范难度名（MyLeagueBrain.Difficulties 同表）。国服卡名与国际服
    /// 用词可能有别（如 顶尖球[星/球员]），按前缀归一到规范名供配置比对。</summary>
    private static readonly (string Prefix, string Name)[] DifficultyPrefixes =
    {
        ("新手", "新手"), ("业余", "业余"), ("标准", "标准"), ("职业", "职业"),
        ("顶尖球", "顶尖球员"), ("超级球", "超级球星"), ("传奇", "传奇"),
    };

    /// <summary>OCR 词 → 规范难度名（卡头行难度名词；匹配不到返回 null）。</summary>
    public static string? MatchDifficultyWord(OcrWord w)
    {
        string t = w.Text.Replace(" ", "");
        foreach (var (prefix, name) in DifficultyPrefixes)
            if (t.Contains(prefix) && t.Length <= prefix.Length + 2) return name;
        return null;
    }

    /// <summary>难度卡行兜底特征：同一行（y 0.25~0.45，实机卡头行）≥3 个不同难度名 +
    /// 底栏「确定」。仅单难度词（球员评价等偶然出现）不构成卡行。</summary>
    private static bool HasCnDifficultyCardRow(OcrResult ocr)
    {
        var names = new HashSet<string>();
        foreach (var w in ocr.Words)
        {
            if (w.CenterY < 0.25 || w.CenterY > 0.45) continue;
            var name = MatchDifficultyWord(w);
            if (name is not null) names.Add(name);
        }
        return names.Count >= 3 && ocr.ContainsJoined("确定");
    }

    /// <summary>
    /// 检测当前选中的难度卡（用户 2026-10-17 实机 cn_tour_difficulty）：光标所在卡
    /// 白底高亮、其余卡深灰底——对每张卡的难度名词采样其下方卡体区平均亮度，
    /// 相对阈值（中位数+22，下限 112）取最亮过线者；全不过线 = 未检出（调用方按
    /// 游戏当前值直接确认，绝不盲导航）。
    /// </summary>
    public static string? DetectSelectedCnDifficulty(FrameData frame, OcrResult ocr)
    {
        var bgra = frame.Bgra;
        int w = frame.Width, h = frame.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return null;

        double RegionAvg(double x0, double x1, double y0, double y1)
        {
            long sum = 0; int n = 0;
            for (int y = Math.Max(0, (int)(h * y0)); y < Math.Min(h, (int)(h * y1)); y += 2)
                for (int x = Math.Max(0, (int)(w * x0)); x < Math.Min(w, (int)(w * x1)); x += 2)
                {
                    int i = (y * w + x) * 4;
                    sum += (bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3;
                    n++;
                }
            return n == 0 ? 0 : (double)sum / n;
        }

        var scores = new List<(string Name, double Avg)>();
        foreach (var word in ocr.Words)
        {
            var name = MatchDifficultyWord(word);
            if (name is null || scores.Any(s => s.Name == name)) continue;
            // 卡体采样：难度名下方（y +0.16~+0.26，避开描述文字行），x 取名词周围卡宽
            double avg = RegionAvg(word.CenterX - 0.06, word.CenterX + 0.06,
                                   word.CenterY + 0.16, word.CenterY + 0.26);
            scores.Add((name, avg));
        }
        if (scores.Count < 2) return null;

        var sorted = scores.Select(s => s.Avg).OrderBy(v => v).ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        double threshold = Math.Max(112, median + 22);

        var best = scores.OrderByDescending(s => s.Avg).First();
        return best.Avg >= threshold ? best.Name : null;
    }

    // ---------------- 巡回赛主要奖励核验（2026-10-17 实机 cn_tour_reward_popup） ----------------

    /// <summary>
    /// 巡回赛主页右栏「主要奖励」行右端的绿勾（≥1 个主要奖励已领取）。实机帧：勾中心
    /// ≈(0.948,0.674)，与国际服活动主页勾区同位。⚠ 勾≠拿满——主页勾表示已有主要奖励
    /// 被领过（主页 高光定向券×1 带勾时 i 弹窗里仍是 1/2），是否拿满以 i 弹窗 x/x 为准。
    /// </summary>
    public static bool MainRewardHasCheck(FrameData frame)
        => CountGreen(frame, 0.920, 0.965, 0.635, 0.715) >= 50;

    /// <summary>
    /// 解析 i 信息弹窗「主要奖励」进度（实机帧：主要奖励 头下方 金币 60/120、
    /// 高光定向券 1/2 各占一项）。进度词只认 主要奖励 头与 活动时间 头之间的
    /// N/M 纯两段词——活动时间行 2026/10/16 为三段不吃。返回 (是否读出至少一项,
    /// 是否全部拿满 cur==max)；读不出按未拿满处理（保守继续刷，绝不提前退场）。
    /// </summary>
    public static (bool Readable, bool AllDone) ParseMainRewardProgress(OcrResult ocr)
    {
        double? top = ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "") == "主要奖励")?.CenterY;
        if (top is null) return (false, false);
        double? bottom = ocr.Words.FirstOrDefault(w => w.Text.Replace(" ", "").Contains("活动时间"))?.CenterY;

        var items = new List<(int Cur, int Max)>();
        foreach (var w in ocr.Words)
        {
            if (w.CenterY <= top.Value + 0.01) continue;
            if (bottom is not null && w.CenterY >= bottom.Value - 0.01) continue;
            var m = Regex.Match(w.Text.Replace(" ", ""), @"^(\d+)/(\d+)$");
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out int cur) ||
                !int.TryParse(m.Groups[2].Value, out int max) || max <= 0) continue;
            items.Add((cur, max));
        }
        return items.Count > 0 ? (true, items.All(i => i.Cur == i.Max)) : (false, false);
    }
}
