using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;
// Observation 类型与 EfootballBot.Core.Observation 命名空间同名，用 Bot. 前缀消歧
using Bot = EfootballBot.Core.Bot;

namespace EfootballBot.Core.Flow;

/// <summary>
/// 「选择赛事等级」模态弹窗处理（我的联赛开赛前）。
/// 游戏内 7 档（自上而下）：新手 / 业余 / 标准 / 职业 / 顶尖球员 / 超级球星 / 传奇。
/// 规则（用户实测）：
/// ① 蓝勾已在用户所选难度 → 不改选直接确认；无勾/勾在别处 → 收拢首行 → 下移到目标行 → A 勾选；
/// ② 确认走确定性步行：Up×8 收拢到首行（无论焦点在难度行还是按钮行都幂等）→
///    Down×7 穿过 7 行落到按钮行（取消）→ Right 到「确认」→ A，与初始焦点位置无关。
///    严禁用 PressButtonAsync（其「盲按 A」兜底在焦点位于难度行上时会改勾选）。
/// ③ 确认后交回赛前流程，由原「下一步 / 开始比赛」逻辑继续。
/// </summary>
public sealed class DifficultySelector
{
    /// <summary>游戏内 7 档难度，顺序即弹窗内自上而下的行序。</summary>
    public static readonly string[] Levels =
        { "新手", "业余", "标准", "职业", "顶尖球员", "超级球星", "传奇" };

    /// <summary>对勾蓝像素占比阈值（逐像素采样，勾行实测 0.08~0.20，无勾≈0）。</summary>
    private const double CheckThreshold = 0.04;

    private readonly Bot.BotEngine _engine;
    private readonly Pad _pad;

    public DifficultySelector(Bot.BotEngine engine, Pad pad)
    {
        _engine = engine;
        _pad = pad;
    }

    /// <summary>
    /// 处理弹窗，返回确认后的新观察（弹窗应已消失）。失败返回 null。
    /// </summary>
    public async Task<Bot.Observation?> HandleAsync(Bot.Observation obs, string target, CancellationToken ct)
    {
        int targetIdx = Array.IndexOf(Levels, target);
        if (targetIdx < 0)
        {
            _engine.LogInternal($"难度选择：配置难度「{target}」不在 7 档列表内，不改选直接确认", Bot.LogLevel.Warn);
            return await ConfirmAndExitAsync(ct);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (checkedIdx, diag) = ScanCheckedRow(obs);
            _engine.LogInternal(
                $"难度选择：当前蓝勾={(checkedIdx < 0 ? "无" : Levels[checkedIdx])}，目标={Levels[targetIdx]}；{diag}",
                checkedIdx < 0 ? Bot.LogLevel.Warn : Bot.LogLevel.Info);
            if (checkedIdx == targetIdx) break;

            // 改选：Up×8 收拢首行（幂等）→ Down×目标序号 → A 勾选
            await CollapseToFirstAsync(ct);
            for (int i = 0; i < targetIdx; i++)
            {
                await _pad.Dpad(PadDir.Down);
                await _pad.Wait(260, ct);
            }
            await _pad.Confirm();
            await _pad.Wait(600, ct);

            obs = await _engine.ObserveAsync(ct);
            var (verify, _) = ScanCheckedRow(obs);
            if (verify == targetIdx) break;

            _engine.LogInternal(
                $"勾选后蓝勾在「{(verify < 0 ? "无" : Levels[verify])}」，未落到目标「{Levels[targetIdx]}」，重试",
                Bot.LogLevel.Warn);
        }

        return await ConfirmAndExitAsync(ct);
    }

    /// <summary>
    /// 扫描当前蓝勾所在行序号（0~6），无勾返回 -1；diag 带回行聚类诊断（便于真机排查）。
    /// 真机坑（2026-10-05）：Windows OCR 常把难度名拆成单字/碎词（「传奇」→「传」+「奇」，
    /// 同「巡回赛」拆词坑），按单词 Contains 匹配会整行落空 → 永远报「无勾」。
    /// 故改为：弹窗带内词按 y 聚类成行 → 行内按 x 拼接（拆开的字重新连回）→ 拼接文本匹配
    /// 难度名 → 对勾 ROI 锚定行内【最左词】（2 字/4 字词中心 x 不同，只能用左边界）。
    /// x 带 0.18~0.88 排除弹窗外背景词（左侧赛程表/赛事信息、右侧任务等，模态框约 x 0.17~0.81）。
    /// </summary>
    public static (int Idx, string Diag) ScanCheckedRow(Bot.Observation obs)
    {
        var f = obs.Frame;
        if (f is null || f.Bgra.Length == 0) return (-1, "无帧");

        // ① 弹窗带内词 → 按 CenterY 聚类成行（行距 ~0.063，容差 0.022）
        var words = obs.Ocr.Words
            .Where(w => w.CenterX is > 0.18 and < 0.88 && w.CenterY is > 0.26 and < 0.86)
            .OrderBy(w => w.CenterY)
            .ToList();
        if (words.Count == 0) return (-1, "弹窗带内无 OCR 词");

        var rows = new List<List<OcrWord>>();
        foreach (var w in words)
        {
            if (rows.Count == 0 || Math.Abs(w.CenterY - rows[^1][0].CenterY) > 0.022)
                rows.Add(new List<OcrWord> { w });
            else
                rows[^1].Add(w);
        }

        int best = -1;
        double bestBlue = CheckThreshold;
        var diag = new List<string>();

        foreach (var row in rows)
        {
            var sorted = row.OrderBy(w => w.X).ToList();
            string joined = string.Concat(sorted.Select(w => OcrText.Norm(w.Text)));
            var anchor = sorted[0]; // 行内最左词 = 难度名（或其首字）
            diag.Add($"{anchor.CenterY:F2}:{joined}");
            double blue = FrameAnalyzer.CheckBlueRatioExact(f, CheckRoi(anchor));
            if (blue > bestBlue)
            {
                int idx = IndexOfLevel(joined);
                if (idx >= 0) { bestBlue = blue; best = idx; }
            }
        }

        diag.Add($"blue={bestBlue:F3}");
        return (best, string.Join(" | ", diag));
    }

    /// <summary>对勾 ROI：锚定难度名词左边界（勾中心约在词左边界 -0.026 处，与词同高）。</summary>
    private static NormRect CheckRoi(OcrWord anchor) =>
        new(anchor.X - 0.046, anchor.CenterY - 0.026, 0.032, 0.052);

    /// <summary>拼接行文本 → 难度序号。OCR 拆词后由拼接命中。</summary>
    private static int IndexOfLevel(string joined)
    {
        for (int i = 0; i < Levels.Length; i++)
            if (joined.Contains(OcrText.Norm(Levels[i]), StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// 确定性确认（与当前焦点位置无关）：Up×8 收拢到首行 → Down×7 穿过 7 个难度行落到
    /// 按钮行（取消）→ Right 到「确认」→ A。全程只在最后按 A，杜绝误改勾选。
    /// </summary>
    private async Task<Bot.Observation> ConfirmAndExitAsync(CancellationToken ct)
    {
        await CollapseToFirstAsync(ct);
        for (int i = 0; i < Levels.Length; i++) // 7 次 Down：首行→…→传奇→按钮行
        {
            await _pad.Dpad(PadDir.Down);
            await _pad.Wait(280, ct);
        }
        await _pad.Dpad(PadDir.Right); // 取消 → 确认
        await _pad.Wait(320, ct);
        await _pad.Confirm();
        await _pad.Wait(1200, ct);

        var after = await _engine.ObserveAsync(ct);
        if (after.Is(GameScreen.DifficultySelect))
        {
            // 确认未生效：焦点应仍在「确认」上，再按一次 A 兜底
            _engine.LogInternal("确认后弹窗仍在，再按 A 兜底", Bot.LogLevel.Warn);
            await _pad.Confirm();
            await _pad.Wait(1200, ct);
            after = await _engine.ObserveAsync(ct);
        }
        _engine.LogInternal("赛事等级已确认", Bot.LogLevel.Info);
        return after;
    }

    /// <summary>Up×8 收拢焦点到首行：从行内/按钮行出发都幂等（到顶后多按无效）。</summary>
    private async Task CollapseToFirstAsync(CancellationToken ct)
    {
        for (int i = 0; i < 8; i++)
        {
            await _pad.Dpad(PadDir.Up);
            await _pad.Wait(260, ct);
        }
    }
}
