using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;

namespace EfootballBot.Core.Bot.Scenarios.MyLeague;

/// <summary>我的联赛界面状态（由 YOLO 检测特征判定）。</summary>
public enum MlScreen
{
    Unknown,    // 比赛画面/过场/加载/详情页等无特征界面
    MainMenu,   // 游戏主菜单（顶部导航条可见）
    ItemList,   // 物品列表页（多行 item_row + 返回按钮）
    HasConfirm, // 存在确定/下一步按钮的页面（弹窗、比赛日结果等）
    LeaguePage, // 我的联赛内部页面（仅返回按钮可见）
}

public enum MlActionKind { ClickMyLeague, ClickItem, ClickPoint, GamepadBack, Wait }

/// <summary>一次决策结果：坐标为归一化（0~1）点击点；GamepadBack / Wait 不使用坐标。</summary>
public sealed record MlAction(MlActionKind Kind, double X, double Y, int WaitMs, string Target = "")
{
    public static MlAction Wait(int ms) => new(MlActionKind.Wait, 0, 0, ms);
}

/// <summary>
/// 我的联赛决策大脑：感知（YOLO 6 类 + OCR）→ 界面分类 → 动作。纯函数，可单测。
/// 动作原则：能点按钮就点（后台鼠标先移光标再点击），看不懂就等，
/// 卡住退避等待，无特征界面连续多轮按 B 逃生（跳过过场/退出意外弹窗）。
/// </summary>
public static class MyLeagueBrain
{
    /// <summary>状态判定/点击目标的最低检测置信度。</summary>
    public const float MinScore = 0.45f;

    /// <summary>同一界面连续该轮数后停止点击类动作，退避等待。</summary>
    public const int StuckStreak = 8;

    /// <summary>无特征界面（比赛/过场）连续该轮数后按 B 逃生。</summary>
    public const int UnknownEscapeStreak = 6;

    public static MlScreen Classify(IReadOnlyList<YoloDetection> dets)
    {
        int Count(string name) => dets.Count(d => d.ClassName == name && d.Score >= MinScore);

        if (Count("main_menu") >= 1) return MlScreen.MainMenu;
        if (Count("item_row") >= 2 && Count("btn_back") >= 1) return MlScreen.ItemList;
        if (Count("btn_confirm") >= 1) return MlScreen.HasConfirm;
        if (Count("btn_back") >= 1) return MlScreen.LeaguePage;
        return MlScreen.Unknown;
    }

    public static MlAction Decide(MlScreen screen, IReadOnlyList<YoloDetection> dets, OcrResult ocr, int screenStreak)
    {
        switch (screen)
        {
            case MlScreen.MainMenu:
            {
                if (screenStreak >= StuckStreak) return MlAction.Wait(5000);
                var entry = FindLine(ocr, "我的联赛");
                return entry is not null
                    ? new MlAction(MlActionKind.ClickMyLeague, entry.Value.X, entry.Value.Y, 2500, "我的联赛入口")
                    : MlAction.Wait(1500);
            }
            case MlScreen.ItemList:
            {
                if (screenStreak >= StuckStreak) return MlAction.Wait(8000);
                var row = Best(dets, "item_row");
                return row is not null
                    ? new MlAction(MlActionKind.ClickItem, row.Value.X, row.Value.Y, 2500, "物品行")
                    : MlAction.Wait(2000);
            }
            case MlScreen.HasConfirm:
            {
                if (screenStreak >= StuckStreak) return MlAction.Wait(5000);
                var btn = Best(dets, "btn_confirm");
                return btn is not null
                    ? new MlAction(MlActionKind.ClickPoint, btn.Value.X, btn.Value.Y, 2000, "确定按钮")
                    : MlAction.Wait(1500);
            }
            case MlScreen.LeaguePage:
            {
                if (screenStreak >= StuckStreak) return MlAction.Wait(8000);
                var go = FindLine(ocr, "下一步", "开始比赛", "继续");
                return go is not null
                    ? new MlAction(MlActionKind.ClickPoint, go.Value.X, go.Value.Y, 2500, go.Value.Text)
                    : MlAction.Wait(2000);
            }
            default:
                // 比赛/过场/加载：连续多轮无特征时按 B 逃生
                return screenStreak >= UnknownEscapeStreak
                    ? new MlAction(MlActionKind.GamepadBack, 0, 0, 3000, "B 键逃生")
                    : MlAction.Wait(4000);
        }
    }

    /// <summary>取指定类中置信度最高的检测框中心（归一化）。</summary>
    private static (double X, double Y)? Best(IReadOnlyList<YoloDetection> dets, string name)
    {
        YoloDetection? best = null;
        foreach (var d in dets)
            if (d.ClassName == name && d.Score >= MinScore && (best is null || d.Score > best.Score))
                best = d;
        return best is null ? null : (best.X + best.W / 2, best.Y + best.H / 2);
    }

    /// <summary>在 OCR 排版行里找包含任一关键词的行，返回行中心（归一化）与命中词。</summary>
    private static (double X, double Y, string Text)? FindLine(OcrResult ocr, params string[] keywords)
    {
        foreach (var line in ocr.Lines)
        {
            var text = line.Text.Replace(" ", "");
            foreach (var kw in keywords)
                if (text.Contains(kw))
                    return (line.X + line.W / 2, line.Y + line.H / 2, kw);
        }
        return null;
    }
}
