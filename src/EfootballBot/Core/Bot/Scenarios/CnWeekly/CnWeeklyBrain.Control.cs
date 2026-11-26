using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Bot.Match;

namespace EfootballBot.Core.Bot.Scenarios.CnWeekly;

// 国服周活动（晋级挑战 / 巡回赛 / 冠军杯）右上角那枚「手柄 | 小人」分段控件的核验。
//
// 语义（用户 2026-11-25 定）：
//   · 手柄图标 = 玩家自主操控：AI 不接管，bot 的输入被玩家通道消费；
//   · 小人图标 = AI 代理操作：托管给 AI。
// 这类"进去就托管打"的页面必须保证高光在小人上，否则要主动点一下小人把它切过去。
//
// 版式实机标定（2026-11-25 用户截图，1280×720 内容帧）：
//   整枚胶囊 x 0.670~0.752 / y 0.013~0.078，中间一条斜线分隔；
//   左半 = 手柄、右半 = 小人；**被选中的那半是纯白（亮度 >190），未选中是灰（≈128）**。
//   实测「手柄被选中」帧：左半 >190 像素占比 0.206、右半 0.043（4.8 倍差）。
// 坐标不写死：实机 FrameData 是整窗帧（顶部多 30~32px 标题栏，见 MatchBrain.SkipBannerHasCheck），
// 所以先在帧顶那条带里自己找胶囊的亮描边包围盒，再按包围盒中分比较两侧亮度——与帧几何无关。
public static partial class CnWeeklyBrain
{
    /// <summary>右上角「控制方式」分段控件的当前高光。</summary>
    public enum ControlMode
    {
        /// <summary>没看见这枚控件（不在带控件的页面 / 画面被遮挡）。</summary>
        Unknown,
        /// <summary>高光在左半「手柄」= 玩家自主操控（要切走）。</summary>
        PlayerPad,
        /// <summary>高光在右半「小人」= AI 代理操作（目标状态）。</summary>
        AiAgent,
    }

    /// <summary>「小人（AI 代理）」图标中心的**内容坐标**（点它切到 AI 代理）。</summary>
    public const double AiAgentIconX = 0.734;
    public const double AiAgentIconY = 0.045;

    /// <summary>
    /// 这一帧要不要核验右上角控制方式：所有国服活动菜单页（<paramref name="cw"/> 认出来即可）
    /// 与联赛主页（<paramref name="ml"/> = LeaguePage）都要过这一关；
    /// 弹窗覆盖层（cw 被门控成 None 且 ml = HasConfirm/Unknown）不过——鼠标点过去会点穿弹窗。
    /// ★不按具体 CwScreen 枚举白名单：2026-11-25 实机「晋级挑战」卡页因底栏「重置」被判成
    /// <see cref="CwScreen.Bracket"/>，白名单直接漏掉了它，页面被一路推进到开赛。
    /// </summary>
    public static bool IsControlProbePage(CwScreen cw, MatchScreen ml)
        => cw != CwScreen.None || ml == MatchScreen.LeaguePage;

    /// <summary>判定为「这半被选中」的最低白像素占比（实测选中 0.206 / 未选中 0.043）。</summary>
    private const double ControlLumaShare = 0.12;
    /// <summary>两半之间至少差这么多倍才算「一侧明显更亮」，否则按未识别处理。</summary>
    private const double ControlShareRatio = 1.6;

    /// <summary>
    /// 识别右上角「手柄 / 小人」控件的高光位置。<paramref name="f"/> 为 null 或尺寸过小时返回
    /// <see cref="ControlMode.Unknown"/>（调用方据此不做任何动作）。
    /// </summary>
    public static ControlMode DetectControlMode(FrameData? f)
    {
        if (f is null || f.Width < 320 || f.Height < 240) return ControlMode.Unknown;
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (bgra.Length < w * h * 4) return ControlMode.Unknown;

        // ① 帧顶那条带里找亮描边（>190）的包围盒。横向窗口避开右侧的 i / 主页按钮（x≥0.83），
        //    纵向留到 0.19 以兼容整窗帧的标题栏偏移（内容里的胶囊会被推到帧 y≈0.05~0.12）。
        int sx0 = (int)(w * 0.60), sx1 = (int)(w * 0.82);
        int sy0 = (int)(h * 0.004), sy1 = (int)(h * 0.19);
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, hits = 0;
        for (int y = sy0; y < sy1; y += 2)
            for (int x = sx0; x < sx1; x += 2)
            {
                int i = (y * w + x) * 4;
                if ((bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3 < 190) continue;
                hits++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        // 胶囊描边 + 两枚图标，实机命中数几百；太少说明这页没有控件（或整页偏暗）
        if (hits < 150 || maxX < 0 || maxY < 0) return ControlMode.Unknown;

        double bw = (maxX - minX) / (double)w, bh = (maxY - minY) / (double)h;
        // 胶囊本体宽 0.082 / 高 0.065。放宽到 0.05~0.14 × 0.030~0.100：
        // 太宽 = 比赛中「跳过比赛✅」横幅（宽≈0.18）或页标题；太扁 = 底栏按钮。
        if (bw < 0.05 || bw > 0.14 || bh < 0.030 || bh > 0.100) return ControlMode.Unknown;

        int split = (minX + maxX) / 2;
        double left = BrightShare(bgra, w, minX, split, minY, maxY);
        double right = BrightShare(bgra, w, split + 1, maxX + 1, minY, maxY);
        if (right >= ControlLumaShare && right > left * ControlShareRatio) return ControlMode.AiAgent;
        if (left >= ControlLumaShare && left > right * ControlShareRatio) return ControlMode.PlayerPad;
        return ControlMode.Unknown;
    }

    /// <summary>矩形内「白像素（亮度 &gt;150）」占比。</summary>
    private static double BrightShare(byte[] bgra, int w, int x0, int x1, int y0, int y1)
    {
        if (x1 <= x0 || y1 <= y0) return 0;
        int lit = 0, total = 0;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                total++;
                if ((bgra[i] + bgra[i + 1] + bgra[i + 2]) / 3 > 150) lit++;
            }
        return total == 0 ? 0 : (double)lit / total;
    }
}
