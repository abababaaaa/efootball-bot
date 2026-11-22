using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;

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
/// 整帧像素探针（共享层）：分类器需要「画面长什么样」的三个廉价判据。
/// 从 我的联赛 场景里搬出来——国服天梯/AI天梯 都在跨模块调它，
/// 现在谁都可以直接调 <see cref="ScreenProbe"/>，不必认识任何场景类。
/// 全部为纯函数，无状态，可单测。
/// </summary>
public static class ScreenProbe
{
    /// <summary>
    /// 右上角（AI 徽标区，x≥0.80 / y 0.03~0.18）内容探针：≥10% 采样点非黑（任一通道 &gt;40）
    /// 即视为有画面。实机像素网格：比赛帧该区为球场纹理（非黑），菜单帧为纯黑 (0,0,0)。
    /// 每 3 像素步进采样，~2000 次比较，开销可忽略。
    /// </summary>
    public static bool CornerHasContent(FrameData f)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return false;
        int x0 = (int)(w * 0.80);
        int y0 = (int)(h * 0.03), y1 = (int)(h * 0.18);
        int samples = 0, lit = 0;
        for (int y = y0; y < y1; y += 3)
        {
            for (int x = x0; x < w; x += 3)
            {
                int i = (y * w + x) * 4;
                samples++;
                if (bgra[i] > 40 || bgra[i + 1] > 40 || bgra[i + 2] > 40) lit++;
            }
        }
        return samples > 0 && lit * 10 >= samples; // ≥10% 非黑
    }

    /// <summary>
    /// 「我的联赛」说明页探针：中央区域（15%~85%）大面积亮色（RGB 均 &gt;180 的采样 ≥60%）。
    /// 我的联赛各页均为深色主题，说明页是整幅白底文档——强特征；比赛画面（球场/夜场）
    /// 达不到该阈值，且有比分框/角区判据先行。实机 bot-20261011-160559 预览：说明页白底
    /// 黑字，OCR 30 词 / btn_back 0.67。
    /// </summary>
    public static bool IsMostlyLightPage(FrameData f)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return false;
        int samples = 0, lit = 0;
        for (int y = (int)(h * 0.15); y < h * 0.85; y += 4)
        {
            for (int x = (int)(w * 0.15); x < w * 0.85; x += 4)
            {
                int i = (y * w + x) * 4;
                samples++;
                if (bgra[i] > 180 && bgra[i + 1] > 180 && bgra[i + 2] > 180) lit++;
            }
        }
        return samples > 0 && lit * 10 >= samples * 6; // ≥60% 亮色
    }

    /// <summary>
    /// 「画面还是球场」探针：中央区域（15%~85%）草地绿像素占比 ≥30%。
    /// 用途：比赛画面/进球庆祝 CG/回放/镜头切换都是满屏草地绿的 3D 场景，必须优先判成
    /// 「比赛进行中」，不许被菜单类判定（MatchHub / LeaguePage）抢走——2026-11-20 实机
    /// 00:34:35：进球后庆祝 CG 被 IsMatchHub 判成比赛页，场次跟踪器单帧当成「回到菜单」关场，
    /// 一场才走了 21 秒的「比赛」凭空计入完成场次（用户反馈「进球后 CG 误判成比赛结束」）。
    /// 实机标定（同一口径，tests\EfootballBot.Tests\TestData）：比赛 0.819、赛中弹窗 0.773、
    /// 国服跳过横幅 0.978~0.981、赛前起始页 0.377；全部菜单/主页/终场页 ≤0.037
    /// （matchhub_user_1 0.037、比赛页新 UI 0.000、终场页 0.003）——30% 阈值两边都留了一档余量。
    /// 与 <see cref="IsMostlyLightPage"/> 同为纯函数，可单测。
    /// </summary>
    public static bool LooksLikePitch(FrameData f)
    {
        var bgra = f.Bgra;
        int w = f.Width, h = f.Height;
        if (w < 100 || h < 100 || bgra.Length < w * h * 4) return false;
        int samples = 0, green = 0;
        for (int y = (int)(h * 0.15); y < h * 0.85; y += 4)
        {
            for (int x = (int)(w * 0.15); x < w * 0.85; x += 4)
            {
                int i = (y * w + x) * 4;
                samples++;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                // 草地绿：G 明显高于 R/B，且有亮度（深色菜单底的偏色不算）
                if (g > 60 && g > r * 1.25 && g > b * 1.25) green++;
            }
        }
        if (samples > 0 && green * 10 >= samples * 3) return true; // ≥30% 亮草地绿
        return LooksLikeDarkPitch(bgra, w, h);
    }

    /// <summary>
    /// 暗光球场兜底：夜场/球场入场 CG 的草地被压得很暗（实机 2026-11-20 00:52 那一屏，草地均值
    /// 只有 RGB(18,21,11)），绝对亮度判据全灭，但**绿通道仍然压过红与蓝**。改看「下半屏
    /// （y 60%~95%）里 G≥R 且 G-B≥5 的像素占比 ≥60%」——只看色相关系不看亮度，暗场也认得出。
    /// 实机标定（tests\EfootballBot.Tests\TestData 全部 187 张）：事故帧 0.983、比赛帧 0.990、
    /// 赛前起始页 0.887；菜单/主页/终场页 0.000~0.025；187 张里只有事故帧是「靠这条新增」。
    /// </summary>
    private static bool LooksLikeDarkPitch(byte[] bgra, int w, int h)
    {
        int samples = 0, greenish = 0;
        for (int y = (int)(h * 0.60); y < h * 0.95; y += 4)
        {
            for (int x = (int)(w * 0.10); x < w * 0.90; x += 4)
            {
                int i = (y * w + x) * 4;
                samples++;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                if (g >= r && g - b >= 5) greenish++;
            }
        }
        return samples > 0 && greenish * 10 >= samples * 6; // ≥60% 下半屏偏绿
    }

    /// <summary>区域内绿色像素计数（横幅绿勾核验等共用，转发 <see cref="RegionProbe"/>）。</summary>
    public static int CountGreen(FrameData f, double x0, double x1, double y0, double y1)
        => RegionProbe.CountGreenRef(f, x0, x1, y0, y1);
}




