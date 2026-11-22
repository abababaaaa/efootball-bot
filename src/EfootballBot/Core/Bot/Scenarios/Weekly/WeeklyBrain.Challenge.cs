using EfootballBot.Core.Bot.Scenarios.MyLeague;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Vision;
using EfootballBot.Core.Vision.Yolo;
using EfootballBot.Core.Bot.Match;

// ===========================================================================
// 模块层 · 周活动（Core/Bot/Scenarios）· WeeklyBrain 判定
// 本文件是 partial 拆分出来的「一个小活动」：只放它自己的界面判定 / 决策 / 子流程，
// 改这一个活动时不要动别的活动文件；共用逻辑留 `WeeklyBrain.cs`。
// 子活动分工见 docs/MODULES.md「周活动子活动分工」。
// 架构锁与其余约束同一份：不跨模块引用、场次记账与挂机节奏只走 MatchPlay。
// ===========================================================================

namespace EfootballBot.Core.Bot.Scenarios.Weekly;

public static partial class WeeklyBrain
{
    // 本文件小活动：挑战活动 —— 解锁弹窗、挑战清单、卡扫描、锁/勾探针、卡号消失判定。

    /// <summary>
    /// 挑战解锁弹窗「挑战N已解锁」判定（用户 2026-11-08 国际服实机帧）：
    /// 标题行固定文案「已解锁」（挑战序号每周变，不作判定点）+ 屏中部白色 OK 按钮
    /// （或固定副标题「测试自己的技巧」）。
    /// 弹窗盖在挑战清单上，背后「挑战清单」标题/卡号/底栏「返回」全在，单看清单特征必被
    /// 误判成可操作清单——必须先判本弹窗，按 A 清掉再回归清单扫描。
    /// OK 按钮限 y&lt;0.85：底栏「B 返回」蓝框会被同款词/框吃掉，不能当弹窗证据。
    /// </summary>
    public static bool IsChallengeUnlockPopup(OcrResult ocr)
    {
        var ok = ocr.FindButton("OK");
        bool centeredOk = ok is not null && ok.CenterY < 0.85;
        if (ocr.ContainsJoined("已解锁") && (centeredOk || ocr.ContainsJoined("测试自己的技巧")))
            return true;
        // 兜底：盖在挑战清单上的其它单按钮 OK 弹窗（挑战完成/奖励到账/条件达成，同款版式）。
        // 这类弹窗和清单共用一套盖层样式，同样吃掉方向键并污染卡位探针——清单本体底栏只有
        // 「B 返回」，屏幕中部不会出现 OK，所以「清单特征 + 屏中部 OK」必是弹窗，按 A 清掉无害。
        return centeredOk && (ocr.ContainsJoined("挑战清单") || HasChallengeListCardNumbers(ocr));
    }

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
}
