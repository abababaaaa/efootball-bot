using EfootballBot.Core.Bot;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Graph;

/// <summary>
/// 首页节点：目标 = 游戏主菜单。
/// 信号 A：顶栏胶囊水平位置（焦点在顶栏时为纯白胶囊，焦点在下排时为浅灰胶囊）；
/// 信号 B：主菜单内容锚点（横幅设置 / 下排磁贴标签）。
/// 跑偏（商店等其他 Tab）→ 按 LB 向左返回游戏主菜单。
/// 中间流动横幅与下排角标数字完全不参与判定。
/// </summary>
public sealed class HomeNode : INavNode
{
    public int StepBudget => 3;

    // 胶囊水平中心窗口
    private const double MainMin = 0.20, MainMax = 0.36;
    private const double OtherMin = 0.38, OtherMax = 0.82;

    public NodeState Check(EfootballBot.Core.Bot.Observation obs)
        => Decide(CapsuleSignal(obs), HasMainAnchor(obs), HasOffTrackWord(obs));

    /// <summary>
    /// 纯判定：capsule 0=无效 1=主菜单窗口 2=其他 Tab 窗口。
    /// 两信号同向才下结论，冲突一律 Uncertain。
    /// </summary>
    internal static NodeState Decide(int capsule, bool mainAnchor, bool offWord)
    {
        switch (capsule)
        {
            case 1:
                return mainAnchor ? NodeState.Anchored : NodeState.Uncertain;
            case 2:
                return offWord ? NodeState.OffTrack : NodeState.Uncertain;
            default:
                return offWord ? NodeState.OffTrack : NodeState.Uncertain;
        }
    }

    public IReadOnlyList<NavAction> NextStep(EfootballBot.Core.Bot.Observation obs)
        => new[] { NavAction.Press(PadButton.LeftShoulder), NavAction.Wait(700) };

    public string Describe(EfootballBot.Core.Bot.Observation obs)
        => CapsuleSignal(obs) switch
        {
            1 => "首页 / 游戏主菜单",
            2 => "首页非主菜单 Tab（需返回主菜单）",
            _ => "首页胶囊信号无效",
        };

    // ---------------- 信号 A：顶栏胶囊 ----------------

    /// <summary>在顶栏条带内检测胶囊水平中心：0=无效 1=主菜单 2=其他 Tab。</summary>
    private static int CapsuleSignal(EfootballBot.Core.Bot.Observation obs)
    {
        var f = obs.Frame;
        if (f is null || f.Bgra.Length == 0) return 0;

        var band = new NormRect(0, 0.03, 1, 0.10);
        var (px, py, w, h) = FrameAnalyzer.PixelRect(f, band);
        var b = f.Bgra;

        // 估计条带底色：取条带右侧暗区（x 0.72~0.80）低饱和像素的平均亮度
        long bgSum = 0; int bgN = 0;
        int bgX0 = px + (int)(w * 0.72), bgX1 = px + (int)(w * 0.80);
        for (int yy = py; yy < py + h; yy += 2)
        {
            int row = yy * f.Width * 4;
            for (int xx = bgX0; xx < bgX1; xx += 2)
            {
                int i = row + xx * 4;
                ColorEx.RgbToHsv(b[i + 2], b[i + 1], b[i], out _, out double s, out double v);
                if (s < 0.35) { bgSum += (long)(v * 255); bgN++; }
            }
        }
        double bgV = bgN == 0 ? 30 : bgSum / (double)bgN;
        int thresh = (int)Math.Max(60, bgV + 35);

        // 逐列统计明显亮于底色且低饱和的像素数
        var col = new int[w];
        for (int xx = 0; xx < w; xx++)
        {
            for (int yy = 0; yy < h; yy++)
            {
                int i = (py + yy) * f.Width * 4 + (px + xx) * 4;
                ColorEx.RgbToHsv(b[i + 2], b[i + 1], b[i], out _, out double s, out double v);
                if (v * 255 >= thresh && s <= 0.35) col[xx]++;
            }
        }

        // 连续合格列（该列 ≥40% 条带高度为胶囊像素）形成的最宽段即胶囊
        int need = (int)(h * 0.4);
        double bestCx = -1; int bestLen = 0;
        int runStart = -1;
        for (int xx = 0; xx <= w; xx++)
        {
            bool on = xx < w && col[xx] >= need;
            if (on)
            {
                if (runStart < 0) runStart = xx;
            }
            else if (runStart >= 0)
            {
                int len = xx - runStart;
                if (len >= w * 0.12 && len > bestLen)
                {
                    bestLen = len;
                    bestCx = (runStart + xx) / 2.0 / w;
                }
                runStart = -1;
            }
        }

        if (bestCx < 0) return 0;
        if (bestCx >= MainMin && bestCx <= MainMax) return 1;
        if (bestCx >= OtherMin && bestCx <= OtherMax) return 2;
        return 0;
    }

    // ---------------- 信号 B：内容锚点 ----------------

    private static readonly string[] TileLabels = { "比赛", "我的球队", "合约", "任务", "每日小游戏" };

    private static bool HasMainAnchor(EfootballBot.Core.Bot.Observation obs)
    {
        // 右下「横幅设置」
        if (obs.Ocr.FindAll("横幅设置")
            .Any(w => w.CenterX > 0.65 && w.CenterY > 0.85))
            return true;

        // 下排磁贴标签命中 ≥2
        int hits = 0;
        foreach (var w in obs.Ocr.MergedWords)
        {
            if (w.CenterY < 0.75 || w.CenterY > 0.92) continue;
            string t = OcrText.Norm(w.Text);
            if (TileLabels.Any(a => t.Equals(a))) hits++;
        }
        return hits >= 2;
    }

    /// <summary>其他 Tab 强特征词（仅接受画面中下部命中，排除流动横幅误识）。</summary>
    private static readonly string[] OffTrackWords = { "立即参加活动", "efootball金币", "efootball积分" };

    private static bool HasOffTrackWord(EfootballBot.Core.Bot.Observation obs)
    {
        foreach (var kw in OffTrackWords)
            foreach (var w in obs.Ocr.FindAll(kw))
                if (w.CenterY > 0.5) return true;
        return false;
    }
}
