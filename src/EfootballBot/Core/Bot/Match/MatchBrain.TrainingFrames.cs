namespace EfootballBot.Core.Bot.Match;

using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

public static partial class MatchBrain
{
    /// <summary>
    /// 训练素材主动收集（fork 侧，v8.1.1）：识别「模型优化最需要的几类主要页面」，
    /// 由 BotEngine 观察层在每帧调用，命中就把当前画面转储到 data\suspicious\日期\ 当训练样本。
    /// 只产出 tag + 说明文字，不读回、不改任何判定/动作——开启与否都不影响挂机行为。
    ///
    /// 为什么要主动抓：现有可疑帧收集全是「出问题才抓」（卡住/读不全/进错卡），
    /// 而模型优化真正缺的是「常见页面的正常帧」——补正样本（真主菜单导航条）
    /// 和困难负样本（周活动卡片页曾误检 main_menu、锦标赛卡片页曾误判联赛页）。
    ///
    /// 判据全部复用本类已有的证据方法（HasMainMenuNavBar / IsMatchHub / 排除词表），
    /// 与模型权重解耦：YOLO 开关或新旧模型都能跑——OCR 词命中就抓。
    /// 优先级：弹窗盖主菜单 &gt; 真主菜单 &gt; 周活动卡片 &gt; 锦标赛卡片 &gt; 比赛选择页，
    /// 每帧至多一类（tag 唯一，节流按 tag 分开）。
    /// </summary>
    /// <param name="ocr">本帧 OCR 结果</param>
    /// <param name="dets">本帧 YOLO 检测（可为 null/空，纯 OCR 页面也能抓）</param>
    /// <returns>(tag, reason)；不属于任何主要页面返回 null</returns>
    public static (string Tag, string Reason)? CollectTrainingFrame(
        OcrResult ocr, IReadOnlyList<YoloDetection>? dets)
    {
        dets ??= Array.Empty<YoloDetection>();
        int count(string name) => dets.Count(d => d.ClassName == name && d.Score >= MinScore);

        bool navBar = HasMainMenuNavBar(ocr);
        bool hasConfirm = count("btn_confirm") >= 1;
        bool hasBack = count("btn_back") >= 1;

        // ① 弹窗盖住真主菜单：导航条可见 + 确认按钮 / 弹窗标题词同屏。
        //    这是 v8.0.15 修的困难场景——重训模型更保守时 main_menu 掉到 0.0x，
        //    只有导航条 OCR 稳定读全。抓这类帧补「半遮挡主菜单」样本。
        bool popupWord = ocr.ContainsJoined("覆盖目前的物品") || ocr.ContainsJoined("关键比赛") ||
                         ocr.ContainsJoined("确认") || ocr.ContainsJoined("使用物品");
        if (navBar && (hasConfirm || popupWord))
            return ("train_popup_over_menu", "弹窗盖主菜单：导航条 + 确认按钮/弹窗词同屏（补遮挡主菜单样本）");

        // ② 真主菜单导航条（干净、无弹窗）——最稀缺的正样本（当前训练集 main_menu 仅 9 框）。
        if (navBar)
            return ("train_main_menu", "真主菜单导航条：游戏主菜单/商店/学院 三词同屏（补 main_menu 正样本）");

        // ③ 周活动卡片页：胶囊词「对阵AI / 对阵玩家」(排除类别页) 或 Team Effort / 赛事等级 / 巡回活动。
        //    这是 v8.0.13 重训的困难负样本来源——旧模型把它误检成 main_menu（0.72~0.92）。
        bool weeklyCapsule = (ocr.ContainsJoined("对阵AI") && !ocr.ContainsJoined("对阵AI类别")) ||
                             (ocr.ContainsJoined("对阵玩家") && !ocr.ContainsJoined("对阵玩家类别"));
        if (weeklyCapsule || ocr.ContainsJoined("Team Effort") || ocr.ContainsJoined("赛事等级") ||
            ocr.ContainsJoined("巡回活动") || ocr.ContainsJoined("结束倒数"))
            return ("train_weekly_cards", "周活动卡片页：胶囊/Team Effort/赛事等级词（补 main_menu 困难负样本）");

        // ④ 锦标赛卡片页：标题「锦标赛」+ 卡上「查看」/ Round N Ranking，无赛段词——
        //    2026-11-23 用户实机截图那类页，旧逻辑误判 LeaguePage 去点「查看」误进锦标赛。
        bool tournamentTitle = ocr.ContainsJoined("锦标赛") || ocr.ContainsJoined("Championship");
        bool tournamentCard = ocr.ContainsJoined("查看") && (ocr.ContainsJoined("Ranking") || ocr.ContainsJoined("已结束"));
        if (tournamentTitle && tournamentCard)
            return ("train_tournament_cards", "锦标赛卡片页：锦标赛标题 + 查看/Ranking 卡片（补误判联赛页负样本）");

        // ⑤ 比赛选择页（磁贴横排）：复用 IsMatchHub 的磁贴词判据，布局漂移/卡排滚动的高发场景。
        if (IsMatchHub(ocr))
            return ("train_match_hub", "比赛选择页：锦标赛/活动/我的联赛磁贴词同屏（补磁贴定位样本）");

        // ⑥ 联赛主页「前往比赛」：联赛流程的核心正样本（btn_confirm / 主页词）。
        //    放最后——避免抢走上面更特殊的页面（弹窗/周活动可能也带「前往比赛」）。
        if (hasConfirm && (ocr.ContainsJoined("前往比赛") || ocr.ContainsJoined("下个赛季")))
            return ("train_league_home", "联赛主页：前往比赛/下个赛季 + 确认按钮（补联赛流程正样本）");

        return null;
    }
}
