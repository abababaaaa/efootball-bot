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
    /// 行数自适应：普通弹窗 7 行；关键比赛变体仅 2 行（超级球星/传奇），改选/确认步数按实际行数走。
    /// </summary>
    public async Task<Bot.Observation?> HandleAsync(Bot.Observation obs, string target, CancellationToken ct)
    {
        int targetIdx = Array.IndexOf(Levels, target);
        if (targetIdx < 0)
        {
            _engine.LogInternal($"难度选择：配置难度「{target}」不在 7 档列表内，不改选直接确认", Bot.LogLevel.Warn);
            return await ConfirmAndExitAsync(obs, ct, ScanCheckedRow(obs).RowLevels.Count);
        }

        int rowCount = 0;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (checkedIdx, diag, rowLevels) = ScanCheckedRow(obs);
            rowCount = rowLevels.Count;
            _engine.LogInternal(
                $"难度选择：当前蓝勾={(checkedIdx < 0 ? "无" : Levels[checkedIdx])}，目标={Levels[targetIdx]}；{diag}",
                checkedIdx < 0 ? Bot.LogLevel.Warn : Bot.LogLevel.Info);
            if (checkedIdx == targetIdx) break;

            // 改选：Up×8 收拢首行（幂等）→ Down×目标行序 → A 勾选
            int targetRow = -1;
            for (int i = 0; i < rowLevels.Count; i++)
                if (rowLevels[i] == targetIdx) { targetRow = i; break; }
            if (targetRow < 0)
            {
                // 目标难度不在本弹窗可选行内（如关键比赛只给超级球星/传奇），不改选直接确认
                _engine.LogInternal(
                    $"目标难度「{Levels[targetIdx]}」不在本弹窗 {rowCount} 个可选行内，不改选直接确认", Bot.LogLevel.Warn);
                break;
            }
            await CollapseToFirstAsync(ct);
            for (int i = 0; i < targetRow; i++)
            {
                await _pad.Dpad(PadDir.Down);
                await _pad.Wait(260, ct);
            }
            await _pad.Confirm();
            await _pad.Wait(600, ct);

            obs = await _engine.ObserveAsync(ct);
            // 弹窗已消失（A 被当成确认而非勾选）：不能再对其他画面跑方向键导航，直接返回
            if (!obs.Is(GameScreen.DifficultySelect))
            {
                _engine.LogInternal("勾选后弹窗已关闭，难度选择结束", Bot.LogLevel.Info);
                return obs;
            }
            var (verify, _, verifyLevels) = ScanCheckedRow(obs);
            rowCount = verifyLevels.Count;
            if (verify == targetIdx) break;

            _engine.LogInternal(
                $"勾选后蓝勾在「{(verify < 0 ? "无" : Levels[verify])}」，未落到目标「{Levels[targetIdx]}」，重试",
                Bot.LogLevel.Warn);
        }

        return await ConfirmAndExitAsync(obs, ct, rowCount);
    }

    /// <summary>
    /// 扫描当前蓝勾所在行序号（0~6），无勾返回 -1；diag 带回行聚类诊断（便于真机排查）。
    /// 真机坑（2026-10-06）：Windows OCR 常漏识整行难度名（「传奇」行只剩积分「0L820」，
    /// 「新手/职业」也常被吞），按词匹配会整行落空 → 永远报「无勾」。
    /// 故改为：弹窗带内词按 y 聚类成行 → 筛出难度行（行文本含难度名或积分数字）→
    /// 凑齐 7 行时按 y 顺序对号入座（行序与 7 档顺序固定一致）；凑不齐回退名称匹配。
    /// 对勾 ROI 用行首固定 x 区（✓ 中心约 x0.235），不再锚定 OCR 词左边界。
    /// x 带 0.18~0.88 排除弹窗外背景词（左侧赛程表/赛事信息、右侧任务等，模态框约 x 0.17~0.81）。
    /// </summary>
    public static (int Idx, string Diag, IReadOnlyList<int> RowLevels) ScanCheckedRow(Bot.Observation obs)
    {
        var f = obs.Frame;
        if (f is null || f.Bgra.Length == 0) return (-1, "无帧", Array.Empty<int>());

        // ① 弹窗带内词 → 按 CenterY 聚类成行（行距 ~0.063，容差 0.022）
        var words = obs.Ocr.Words
            .Where(w => w.CenterX is > 0.18 and < 0.88 && w.CenterY is > 0.26 and < 0.86)
            .OrderBy(w => w.CenterY)
            .ToList();
        if (words.Count == 0) return (-1, "弹窗带内无 OCR 词", Array.Empty<int>());

        var rows = new List<List<OcrWord>>();
        foreach (var w in words)
        {
            if (rows.Count == 0 || Math.Abs(w.CenterY - rows[^1][0].CenterY) > 0.022)
                rows.Add(new List<OcrWord> { w });
            else
                rows[^1].Add(w);
        }

        // ② 难度行 = 行拼接文本含积分数字或难度名（表头「可赚取的我的联赛积分」无数字被排除）
        var diffRows = rows
            .Select(r => r.OrderBy(w => w.X).ToList())
            .Select(sorted => (Sorted: sorted, Joined: string.Concat(sorted.Select(w => OcrText.Norm(w.Text))),
                              CY: sorted.Average(w => w.CenterY)))
            // 副标题「可赚取的我的联赛积分」（真机"赛"被误识成 0 →「…联0积分」）
            // 不是难度行：含「积分」一律剔除。7 档难度名均不含「积分」，无误伤
            .Where(t => !t.Joined.Contains("积分", StringComparison.Ordinal)
                        && (t.Joined.Any(char.IsDigit) || IndexOfLevel(t.Joined) >= 0))
            .OrderBy(t => t.CY)
            .ToList();

        // ③ 凑齐 7 行 → 按 y 顺序对号入座；
        //    2 行且行距接近（≤0.085）→ 关键比赛变体（仅超级球星/传奇）→ 按 [5,6] 对号；
        //    其余回退行文本名称匹配
        bool positional = diffRows.Count == Levels.Length;
        bool keyVariant = diffRows.Count == 2
            && diffRows[1].CY - diffRows[0].CY <= 0.085;
        int best = -1;
        double maxBlue = 0;
        var diag = new List<string>();
        var rowLevels = new List<int>();

        for (int i = 0; i < diffRows.Count; i++)
        {
            var (sorted, joined, cy) = diffRows[i];
            int idx = positional ? i
                    : keyVariant ? Levels.Length - 2 + i
                    : IndexOfLevel(joined);
            rowLevels.Add(idx);
            diag.Add($"{cy:F2}:{joined}");
            if (idx < 0) continue;
            double blue = FrameAnalyzer.CheckBlueRatioExact(f, CheckRoiAt(cy));
            if (blue > maxBlue) { maxBlue = blue; if (blue > CheckThreshold) best = idx; }
        }

        diag.Add($"blue={maxBlue:F3}");
        return (best, string.Join(" | ", diag), rowLevels);
    }

    /// <summary>对勾 ROI：行首固定 x 区（✓ 中心约 x0.235，左偏 0.212 避开焦点行蓝描边左竖线）。</summary>
    private static NormRect CheckRoiAt(double centerY) =>
        new(0.212, centerY - 0.028, 0.050, 0.056);

    /// <summary>拼接行文本 → 难度序号。OCR 拆词后由拼接命中。</summary>
    private static int IndexOfLevel(string joined)
    {
        for (int i = 0; i < Levels.Length; i++)
            if (joined.Contains(OcrText.Norm(Levels[i]), StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// 确定性确认（与当前焦点位置无关）：Up×8 收拢到首行 → Down×行数 穿过难度行落到
    /// 按钮行（取消）→ Right 到「确认」→ A。全程只在最后按 A，杜绝误改勾选。
    /// 行数自适应：普通 7 行 / 关键比赛变体 2 行；rowCount≤0（扫描失败）时按 7 行兜底。
    /// 弹窗已不在（obs 不是 DifficultySelect）时直接返回，避免方向键误操作背后的菜单。
    /// </summary>
    private async Task<Bot.Observation> ConfirmAndExitAsync(Bot.Observation obs, CancellationToken ct, int rowCount)
    {
        if (!obs.Is(GameScreen.DifficultySelect))
        {
            _engine.LogInternal("弹窗已关闭，跳过确认导航", Bot.LogLevel.Debug);
            return obs;
        }
        if (rowCount <= 0) rowCount = Levels.Length;
        await CollapseToFirstAsync(ct);
        for (int i = 0; i < rowCount; i++) // N 次 Down：首行→…→末行→按钮行
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
