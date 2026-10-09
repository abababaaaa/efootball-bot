using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>我的联赛界面状态（由 YOLO 检测特征 + OCR 文字判定）。</summary>
public enum MlScreen
{
    Unknown,    // 比赛画面/过场/加载/详情页等无特征界面
    MainMenu,   // 游戏主菜单（顶部导航条可见）
    MatchHub,   // 比赛页（OCR 含"我的联赛"，选默认焦点进入）
    ItemList,   // 物品列表页（多行 item_row + 返回按钮）
    HasConfirm, // 存在确定/下一步按钮的页面（弹窗、比赛日结果等）
    LeaguePage, // 我的联赛内部页面（仅返回按钮可见）
}

public enum MlActionKind { PadEnterMatch, PadConfirm, GamepadBack, Wait }

/// <summary>
/// 一次决策结果。全部动作均为模拟手柄操作（国际服不响应后台鼠标）：
/// PadEnterMatch = 复合导航（下到卡片行 → 左到最左"比赛" → A 进入）；
/// PadConfirm = 手柄 A（确认/下一步/选中焦点项）；GamepadBack = 手柄 B（返回/逃生）。
/// </summary>
public sealed record MlAction(MlActionKind Kind, int WaitMs, string Target = "")
{
    public static MlAction Wait(int ms) => new(MlActionKind.Wait, ms);
}

/// <summary>
/// 我的联赛决策大脑：感知（YOLO 6 类 + OCR）→ 界面分类 → 动作。纯函数，可单测。
/// 动作原则：全程模拟手柄（Dpad 导航 + A 确认 + B 返回）；看不懂就等，
/// 卡住退避等待，无特征界面连续多轮按 B 逃生（跳过过场/退出意外弹窗）。
/// </summary>
public static class MyLeagueBrain
{
    /// <summary>状态判定/点击目标的最低检测置信度。</summary>
    public const float MinScore = 0.45f;

    /// <summary>同一界面连续该轮数后停止操作类动作，退避等待。</summary>
    public const int StuckStreak = 8;

    /// <summary>无特征界面（比赛/过场）连续该轮数后按 B 逃生。</summary>
    public const int UnknownEscapeStreak = 6;

    public static MlScreen Classify(IReadOnlyList<YoloDetection> dets, OcrResult ocr)
    {
        int Count(string name) => dets.Count(d => d.ClassName == name && d.Score >= MinScore);

        if (Count("main_menu") >= 1) return MlScreen.MainMenu;
        if (Count("item_row") >= 2 && Count("btn_back") >= 1) return MlScreen.ItemList;
        if (Count("btn_confirm") >= 1) return MlScreen.HasConfirm;
        if (Count("btn_back") >= 1) return MlScreen.LeaguePage;

        // 比赛页无 YOLO 训练特征，用 OCR 兜底：含"我的联赛"且非主菜单（主菜单含"退出游戏"）
        bool hasMyLeague = ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("我的联赛"));
        bool hasExit = ocr.Lines.Any(l => l.Text.Replace(" ", "").Contains("退出游戏"));
        if (hasMyLeague && !hasExit) return MlScreen.MatchHub;

        return MlScreen.Unknown;
    }

    public static MlAction Decide(MlScreen screen, OcrResult ocr, int screenStreak)
    {
        switch (screen)
        {
            case MlScreen.MainMenu:
            {
                // 复合导航：Dpad 下（确保焦点在底部卡片行）→ 左 ×6（推到最左"比赛"，到头多按无害）→ A 进入
                if (screenStreak >= StuckStreak) return MlAction.Wait(5000);
                return new MlAction(MlActionKind.PadEnterMatch, 3000, "主菜单→比赛卡片");
            }
            case MlScreen.MatchHub:
            {
                // 比赛页焦点初始位置不确定，v1 直接 A 选中默认焦点（预期"我的联赛"），卡住退避
                if (screenStreak >= StuckStreak) return MlAction.Wait(5000);
                return new MlAction(MlActionKind.PadConfirm, 2500, "比赛页默认焦点（预期我的联赛）");
            }
            case MlScreen.ItemList:
            {
                // 物品列表：A 选中当前焦点行
                if (screenStreak >= StuckStreak) return MlAction.Wait(8000);
                return new MlAction(MlActionKind.PadConfirm, 2500, "物品列表焦点行");
            }
            case MlScreen.HasConfirm:
            {
                if (screenStreak >= StuckStreak) return MlAction.Wait(5000);
                return new MlAction(MlActionKind.PadConfirm, 2000, "确定/下一步");
            }
            case MlScreen.LeaguePage:
            {
                if (screenStreak >= StuckStreak) return MlAction.Wait(8000);
                var go = FindLine(ocr, "下一步", "开始比赛", "继续");
                return go is not null
                    ? new MlAction(MlActionKind.PadConfirm, 2500, go)
                    : MlAction.Wait(2000);
            }
            default:
                // 比赛/过场/加载：连续多轮无特征时按 B 逃生
                return screenStreak >= UnknownEscapeStreak
                    ? new MlAction(MlActionKind.GamepadBack, 3000, "B 键逃生")
                    : MlAction.Wait(4000);
        }
    }

    /// <summary>在 OCR 排版行里找包含任一关键词的行，返回命中词。</summary>
    private static string? FindLine(OcrResult ocr, params string[] keywords)
    {
        foreach (var line in ocr.Lines)
        {
            var text = line.Text.Replace(" ", "");
            foreach (var kw in keywords)
                if (text.Contains(kw))
                    return kw;
        }
        return null;
    }
}
