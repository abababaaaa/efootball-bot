using EfootballBot.Core.Bot.Scenarios.MyLeague;
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
    /// 判定顺序：盖层弹窗门控 → RS 菜单（盖在阵容页上）→ 阵容页 → 活动设置面板（盖在巡回赛
    /// 主页右半）→ 巡回赛型活动主页 → 杯赛型活动主页 → 对阵表 → 活动介绍 → 活动列表
    /// → 主页（比赛页签 → 推荐页签）。
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

        // 盖层弹窗门控（同国际服周活动实机教训）：主页词透过暗化背景仍会被 OCR 读到，
        // 误判回菜单页会把无效按键当"未生效"反复重按。弹窗自带推进词（下一步/确认/整词OK，
        // 或 YOLO 检出确认按钮）且背景有周活动界面证据 → 交共享流程按 A 清弹窗。
        // 推进词必须在屏幕中部（y&lt;0.82）：对阵表底栏「RT 下一步」、活动主页底栏等
        // 自带按钮词在 y≈0.93，不限 y 会把干净的对阵表/主页全门控成弹窗（对阵表会因此
        // 被判 None → 共享流程 Unknown → B 逃生退回介绍页，死循环）。
        bool popupOnTop = CenterPopupWord(ocr) ||
            dets.Any(d => d.ClassName == "btn_confirm" && d.Score >= MyLeagueBrain.MinScore && d.CenterY < 0.82);
        if (popupOnTop && (squadBody || eventHomeBody || tourHomeBody || settingsBody ||
                           bracketBody || listBody || introBody))
            return CwScreen.None;

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
}
