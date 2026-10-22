using EfootballBot.Core.Bot;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot.Scenarios;

/// <summary>赛后活动任务页（活动地图）界面类型。None = 非该模块界面。</summary>
public enum PmScreen
{
    /// <summary>不是赛后活动任务页。</summary>
    None,
    /// <summary>活动地图主界面（右上「奖励」、右下「下一步」、底部「X 详情 / Y 当前位置」）。</summary>
    Map,
    /// <summary>「获取机会交易 ×1?」确认弹窗（覆盖在地图上）。</summary>
    Popup,
}

/// <summary>
/// 赛后活动任务判定（纯函数）：
/// 该页面独立出现在比赛结束后 / 比赛开始前，随服务器活动推送轮换，属全屏非弹窗。
/// 固定特征只有右上「奖励」胶囊、右下「下一步」与底部「X 详情 / Y 当前位置」——
/// 活动名、小图标、奖励文字全部可变，一律不作判定点（可变内容禁令）。
/// 操作规则（用户 2026-10-22 实机）：
///   · 勾选自动推进：每轮 Y 回当前位置 → ↑ → 下一步 → 弹窗 ↑ → 下一步，共 x 轮 → 退出；
///   · 未勾选自动推进：直接退出；
///   · 退出 = 无弹窗时 → 然后 下一步。
/// x = 左侧小图标旁边的数字（不是「总积分 1/9」里的分母/总数）。
/// </summary>
public static class PostMatchActivityBrain
{
    /// <summary>「机会交易」弹窗强特征：标题/正文固定出现该词或「地图前进步数」。</summary>
    public static bool IsPopup(OcrResult ocr) =>
        ocr.ContainsJoined("机会交易") || ocr.ContainsJoined("前进步数");

    /// <summary>
    /// 活动地图主界面：「当前位置」为全游戏唯一词（底部 Y 键说明），再叠加「下一步」或
    /// 右上「奖励」任一固定元素。弹窗先行判定——弹窗背景暗化后仍可能读到地图词。
    /// </summary>
    public static bool IsMap(OcrResult ocr) =>
        ocr.ContainsJoined("当前位置") &&
        (ocr.ContainsJoined("下一步") || ocr.ContainsJoined("奖励"));

    public static PmScreen Classify(OcrResult ocr)
    {
        if (IsPopup(ocr)) return PmScreen.Popup;
        if (IsMap(ocr)) return PmScreen.Map;
        return PmScreen.None;
    }

    /// <summary>
    /// 读取左侧小图标旁的数字 = 本次可推进次数 x。
    /// 先定位「总积分」行的纵坐标，取其上方 0.02~0.16 那一行的纯数字词（图标计数行）；
    /// 找不到标签时退回固定区域（左上 x&lt;0.35、y 0.17~0.34）。O/0 归一、只接受 1~99。
    /// 读不到/超出范围返回 null——调用方保守直接退出，绝不盲猜次数（p3 数字为易误判项）。
    /// </summary>
    public static int? ReadAdvanceCount(OcrResult ocr)
    {
        double? totalY = ocr.Words
            .FirstOrDefault(w => w.NormText.Contains("总积分"))?.CenterY;

        int? best = null;
        double bestH = 0;
        foreach (var w in ocr.Words)
        {
            if (w.CenterX >= 0.35) continue;
            if (totalY is { } ty)
            {
                if (w.CenterY <= ty - 0.16 || w.CenterY >= ty - 0.02) continue;
            }
            else if (w.CenterY < 0.17 || w.CenterY > 0.34) continue;

            if (TryParseCount(w.NormText, out int v) && w.H > bestH)
            {
                bestH = w.H;
                best = v;
            }
        }
        return best;
    }

    /// <summary>
    /// 全帧 OCR 读不到时（实机：「C'」把数字并进图标），裁切左上计数区放大 8 倍再 OCR。
    /// 只在该页首次进入时执行一次，额外一次小图推理（约数百毫秒）。
    /// </summary>
    public static async Task<int?> ReadAdvanceCountAsync(BotEngine engine, Observation obs,
        CancellationToken ct = default)
    {
        var direct = ReadAdvanceCount(obs.Ocr);
        if (direct is not null) return direct;
        try
        {
            var crop = ImageScaling.CropUpscale(obs.Frame, 0.03, 0.20, 0.22, 0.33, 8);
            var res = await engine.Ocr.RecognizeAsync(crop, ct);
            int? best = null;
            double bestH = 0;
            foreach (var w in res.Words)
                if (TryParseCount(w.NormText, out int v) && w.H > bestH)
                {
                    bestH = w.H;
                    best = v;
                }
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从词文本提取 1~2 位纯数字（O/0 归一，忽略图标字符）。</summary>
    private static bool TryParseCount(string norm, out int value)
    {
        value = 0;
        var t = norm.ToUpperInvariant().Replace('O', '0');
        var digits = new string(t.Where(char.IsDigit).ToArray());
        if (digits.Length is 0 or > 2) return false;
        return int.TryParse(digits, out value) && value is >= 1 and <= 99;
    }
}

/// <summary>状态机对手柄的最小依赖（测试用录制实现替换；生产经 <see cref="EnginePmActivityPad"/> 走真实手柄）。</summary>
internal interface IPmActivityPad
{
    Task Y();
    Task Dpad(PadDir dir);
    Task Confirm();
}

/// <summary><see cref="IPmActivityPad"/> 的生产实现：转发到引擎手柄。</summary>
internal sealed class EnginePmActivityPad(Pad pad) : IPmActivityPad
{
    public Task Y() => pad.Y();
    public Task Dpad(PadDir dir) => pad.Dpad(dir);
    public Task Confirm() => pad.Confirm();
}

/// <summary>
/// 赛后活动任务状态机（跨帧）。地图/弹窗可能连续出现多帧，动作按「一帧一步」推进：
/// 地图 →（自动）Y 回当前位置仅一次 → 循环 [↑+下一步 → 弹窗 ↑+下一步] × x → 退出 [→ 然后 下一步]。
/// 未开启自动推进则直接退出。屏幕消失 3 帧后复位，避免偶发 OCR 漏读把流程打断。
/// </summary>
public sealed class PostMatchActivityController
{
    private enum Phase
    {
        Idle,
        /// <summary>地图上等待执行下一次「↑ + 下一步」（首次先按一次 Y）。</summary>
        Advance,
        /// <summary>已按 ↑+下一步，等待「机会交易」弹窗出现。</summary>
        AwaitPopup,
        /// <summary>退出序列（→ 然后 下一步）尚待执行。</summary>
        Exit,
        /// <summary>本轮退出动作已发出，等待离屏。</summary>
        Done,
    }

    private Phase _phase = Phase.Idle;
    private int _remaining;
    private int _steps;
    private int _popupMiss;
    private int _missing;
    private int _exitRetries;
    /// <summary>Y（回当前位置）只在首次进入时按一次，无论后续推进多少轮（用户 2026-10-22 修正）。</summary>
    private bool _yPressed;

    /// <summary>是否正在处理该界面（供调用方日志/统计用）。</summary>
    public bool Active => _phase != Phase.Idle;

    /// <summary>
    /// 每轮调用：命中活动地图/弹窗时执行一步并返回下一轮延时；
    /// 未命中时返回 null（屏幕消失 3 帧后自动复位）。
    /// 推进次数 x 只在首次进入地图时读一次（裁切 OCR 有额外开销）。
    /// </summary>
    public Task<int?> StepAsync(BotEngine engine, Observation obs, bool autoAdvance,
        Action<string, LogLevel> log, string module, CancellationToken ct = default)
        => StepAsyncCore(obs.Ocr, autoAdvance,
            countReader: token => PostMatchActivityBrain.ReadAdvanceCountAsync(engine, obs, token),
            pad: new EnginePmActivityPad(engine.Pad), log, module, ct);

    /// <summary>核心状态机（OCR/手柄全部参数化，可单测）。countReader 仅在首次进入且开启自动推进时调用。</summary>
    internal async Task<int?> StepAsyncCore(OcrResult ocr, bool autoAdvance,
        Func<CancellationToken, Task<int?>>? countReader, IPmActivityPad pad,
        Action<string, LogLevel> log, string module, CancellationToken ct = default)
    {
        var screen = PostMatchActivityBrain.Classify(ocr);
        if (screen == PmScreen.None)
        {
            if (_phase != Phase.Idle && ++_missing >= 3) Reset();
            return null;
        }
        _missing = 0;

        // ---- 新一轮：首次进入地图/弹窗 ----
        if (_phase == Phase.Idle)
        {
            if (screen == PmScreen.Popup)
            {
                // 半途接管恰好落在弹窗上：当作需要清掉的弹窗处理（上移避开取消），清完走退出
                _phase = Phase.AwaitPopup;
                log($"[{module}] 赛后活动：检测到「机会交易」弹窗，上移确认", LogLevel.Info);
                await pad.Dpad(PadDir.Up);
                await pad.Confirm();
                _phase = Phase.Exit;
                return 2200;
            }

            if (!autoAdvance)
            {
                log($"[{module}] 赛后活动地图：未开启自动推进，直接退出（→ 下一步）", LogLevel.Info);
                _phase = Phase.Exit;
            }
            else
            {
                int? x = countReader is not null ? await countReader(ct) : null;
                if (x is null or <= 0)
                {
                    log($"[{module}] 赛后活动地图：左侧推进次数读取失败（易误判项），保守直接退出", LogLevel.Warn);
                    _phase = Phase.Exit;
                }
                else
                {
                    _remaining = x.Value;
                    _steps = 0;
                    _popupMiss = 0;
                    _exitRetries = 0;
                    _yPressed = false;
                    _phase = Phase.Advance;
                    log($"[{module}] 赛后活动地图：自动推进 {_remaining} 次（先按一次 Y 回到初始位置）", LogLevel.Success);
                }
            }
        }

        switch (_phase)
        {
            case Phase.Advance:
                if (_remaining > 0 && _steps < _remaining + 2)
                {
                    _popupMiss = 0;
                    if (!_yPressed)
                    {
                        _yPressed = true;
                        log($"[{module}] 赛后活动：先按 Y 回到当前位置（整个流程仅此一次）", LogLevel.Info);
                        await pad.Y();
                        return 1500;
                    }
                    _steps++;
                    log($"[{module}] 赛后活动：推进第 {_steps}/{_remaining} 次（↑ → 下一步）", LogLevel.Info);
                    await pad.Dpad(PadDir.Up);
                    await pad.Confirm();
                    _phase = Phase.AwaitPopup;
                    return 2200;
                }
                return await ExitAsync(pad, log, module);

            case Phase.AwaitPopup:
                if (screen == PmScreen.Popup)
                {
                    log($"[{module}] 赛后活动：「机会交易」弹窗：上移选获取 → 下一步", LogLevel.Info);
                    await pad.Dpad(PadDir.Up);
                    await pad.Confirm();
                    _remaining--;
                    _phase = Phase.Advance;
                    return 2200;
                }
                // 按了下一步却没等到弹窗（次数读多/该格无交易）：短暂重试后视为本步已消费
                if (++_popupMiss <= 1) return 1200;
                _remaining--;
                _phase = Phase.Advance;
                return 1000;

            case Phase.Exit:
                return await ExitAsync(pad, log, module);

            case Phase.Done:
            default:
                // 退出动作已发出：等待画面切走。长时间仍在地图（→/下一步 被吞）重试退出
                if (++_exitRetries > 6)
                {
                    log($"[{module}] 赛后活动地图：退出动作未生效，重试（→ 下一步）", LogLevel.Warn);
                    return await ExitAsync(pad, log, module);
                }
                return 1500;
        }
    }

    private async Task<int> ExitAsync(IPmActivityPad pad, Action<string, LogLevel> log, string module)
    {
        log($"[{module}] 赛后活动地图：退出（→ 然后 下一步）", LogLevel.Info);
        await pad.Dpad(PadDir.Right);
        await pad.Confirm();
        _phase = Phase.Done;
        _exitRetries = 0;
        return 2200;
    }

    private void Reset()
    {
        _phase = Phase.Idle;
        _remaining = 0;
        _steps = 0;
        _popupMiss = 0;
        _missing = 0;
        _exitRetries = 0;
        _yPressed = false;
    }
}
