using EfootballBot.Core.Bot;
using EfootballBot.Core.Capture;
using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Action;

/// <summary>
/// 通用交互原语执行器：按钮点击 / 焦点导航 / 菜单选择。
/// 从 BotEngine 抽离，集中管理按钮发光检测与方向键移动逻辑，避免 BotEngine 与 ScenarioBase 各存一份 ButtonGlow。
/// </summary>
public sealed class ActionExecutor
{
    private readonly BotEngine _engine;
    private readonly Pad _pad;

    public ActionExecutor(BotEngine engine, Pad pad)
    {
        _engine = engine;
        _pad = pad;
    }

    /// <summary>
    /// 在垂直菜单中按 OCR 文字找到目标项：逐项下移并检测该项高亮（黄 / 亮白），命中后按 A。
    /// </summary>
    public async Task<bool> SelectMenuByTextAsync(string[] normKeywords, CancellationToken ct, int maxItems = 8)
    {
        for (int i = 0; i < maxItems; i++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await _engine.ObserveAsync(ct);
            foreach (var kw in normKeywords)
            {
                var w = o.Ocr.FindFirst(kw);
                if (w is null) continue;
                var region = new NormRect(
                    Math.Max(0, w.X - 0.04), Math.Max(0, w.Y - w.H * 0.6),
                    Math.Min(1, w.W + 0.08), w.H * 2.2);
                double yellow = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Yellow, region);
                double bright = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.Bright, region);
                if (yellow > 0.04 || bright > 0.35)
                {
                    _engine.LogInternal($"菜单选中「{kw}」（y={yellow:F2} b={bright:F2}），按 A", LogLevel.Debug);
                    await _pad.Confirm();
                    return true;
                }
            }
            await _pad.Dpad(PadDir.Down);
        }
        return false;
    }

    /// <summary>
    /// 按钮区域尺寸：根据文字大小自适应，确保覆盖整个按钮（含发光边框）。
    /// eFootball 按钮文字只占按钮中心一小部分，边框发光在文字外围，区域太小会漏检焦点。
    /// </summary>
    private static NormRect ButtonRegion(OcrWord w)
    {
        double rw = Math.Clamp(w.W * 8, 0.20, 0.42);
        double rh = Math.Clamp(w.H * 4, 0.08, 0.16);
        double x = Math.Clamp(w.CenterX - rw / 2, 0, 1 - rw);
        double y = Math.Clamp(w.CenterY - rh / 2, 0, 1 - rh);
        return new NormRect(x, y, rw, rh);
    }

    /// <summary>
    /// 检测按钮焦点发光度：eFootball 聚焦按钮边框为青色/蓝色/亮白，
    /// 需同时检查 Blue | Cyan | Bright 三色滤镜，单用 Blue 会漏检青色焦点。
    /// </summary>
    internal double ButtonGlow(FrameData frame, OcrWord w)
    {
        var r = ButtonRegion(w);
        double blue = FrameAnalyzer.ColorRatio(frame, HsvFilter.Blue, r);
        double cyan = FrameAnalyzer.ColorRatio(frame, HsvFilter.Cyan, r);
        double bright = FrameAnalyzer.ColorRatio(frame, HsvFilter.Bright, r);
        return Math.Max(blue, Math.Max(cyan, bright * 0.8));
    }

    /// <summary>
    /// 指针感知点击：找到目标按钮（OK/下一步/确定），检测其是否高亮（指针已在上面），
    /// 不在则按方向键朝它移动指针，到位后按 A。返回是否成功点击。
    /// 焦点检测使用蓝+青+亮多色滤镜，兼容 eFootball 各种按钮高亮风格。
    /// exactMatch=true 时用精确词匹配（避免 "ok" 误匹配 "Fukuoka" 等队名）。
    /// </summary>
    public async Task<bool> PressButtonAsync(string[] targetKeywords, CancellationToken ct, int maxMoves = 8, bool exactMatch = false)
    {
        // 弹窗里常见可聚焦按钮词（用于定位"当前指针在哪"）
        string[] allButtons = { "ok", "确定", "确认", "取消", "下一步", "跳过", "返回", "是", "否", "yes", "no", "cancel", "skip", "next", "back" };
        // 目标按钮被判定为已聚焦的发光度阈值
        const double TargetFocused = 0.12;
        // 找到当前焦点的发光度阈值（低于此值认为没检测到焦点）
        const double FocusFound = 0.06;

        for (int move = 0; move < maxMoves; move++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await _engine.ObserveAsync(ct);

            OcrWord? target = null;
            foreach (var kw in targetKeywords)
            {
                if (exactMatch)
                    target = o.Ocr.MergedWords.FirstOrDefault(w =>
                        OcrText.Norm(w.Text).Equals(kw, StringComparison.OrdinalIgnoreCase));
                else
                    target = o.Ocr.FindButtonWords(kw).FirstOrDefault();
                if (target is not null) break;
            }
            if (target is null) return false; // 目标都不在屏幕上

            double tGlow = ButtonGlow(o.Frame, target);

            // 目标按钮已聚焦：直接按 A
            if (tGlow >= TargetFocused)
            {
                _engine.LogInternal($"指针已在「{target.Text}」上（glow={tGlow:F2}），按 A", LogLevel.Debug);
                await _pad.Confirm();
                await Task.Delay(600, ct);
                return true;
            }

            // 找当前焦点按钮：所有候选按钮中发光度最高者
            OcrWord? focus = null;
            double focusGlow = 0;
            foreach (var kw in allButtons)
            {
                foreach (var w in o.Ocr.FindButtonWords(kw))
                {
                    double g = ButtonGlow(o.Frame, w);
                    if (g > focusGlow) { focusGlow = g; focus = w; }
                }
            }

            if (focus is not null && focusGlow >= FocusFound && focus != target)
            {
                // 找到当前焦点：朝目标按钮移动
                double dx = target.CenterX - focus.CenterX;
                double dy = target.CenterY - focus.CenterY;
                _engine.LogInternal($"移动指针：「{focus.Text}」→「{target.Text}」 dx={dx:F2} dy={dy:F2}", LogLevel.Debug);

                if (Math.Abs(dx) < 0.05 && Math.Abs(dy) < 0.06)
                {
                    // 位置已对准但发光不足：直接按 A 尝试
                    await _pad.Confirm();
                    await Task.Delay(600, ct);
                    return true;
                }
                if (Math.Abs(dx) > Math.Abs(dy))
                    await _pad.Dpad(dx > 0 ? PadDir.Right : PadDir.Left);
                else
                    await _pad.Dpad(dy > 0 ? PadDir.Down : PadDir.Up);
                await Task.Delay(400, ct);
                continue;
            }

            // 没检测到任何高亮焦点：先盲按 A（焦点可能已在目标上但发光检测漏了），
            // 若无效再按目标按钮位置做一次方向试探
            if (move == 0)
            {
                _engine.LogInternal($"未检测到焦点，先盲按 A 尝试「{target.Text}」", LogLevel.Debug);
                await _pad.Confirm();
                await Task.Delay(600, ct);
                // 重新观察：若目标按钮消失说明按 A 成功
                var o2 = await _engine.ObserveAsync(ct);
                bool stillThere = exactMatch
                    ? targetKeywords.Any(kw => o2.Ocr.MergedWords.Any(w =>
                          OcrText.Norm(w.Text).Equals(kw, StringComparison.OrdinalIgnoreCase)))
                    : targetKeywords.Any(kw => o2.Ocr.FindButtonWords(kw).Any());
                if (!stillThere) return true;
                // 按钮还在：说明 A 没点中，根据目标位置试探移动方向
                double tx = target.CenterX, ty = target.CenterY;
                _engine.LogInternal($"盲按 A 未生效，按目标位置试探移动（x={tx:F2} y={ty:F2}）", LogLevel.Debug);
                if (ty > 0.6) await _pad.Dpad(PadDir.Down);        // 按钮在下方 → 往下
                else if (ty < 0.4) await _pad.Dpad(PadDir.Up);     // 按钮在上方 → 往上
                else if (tx > 0.6) await _pad.Dpad(PadDir.Right);  // 按钮在右方 → 往右
                else await _pad.Dpad(PadDir.Left);                 // 按钮在左方 → 往左
                await Task.Delay(400, ct);
                continue;
            }

            // 后续轮次：仍无焦点，直接按 A 兜底
            _engine.LogInternal($"仍未检测到焦点，直接按 A 兜底「{target.Text}」", LogLevel.Debug);
            await _pad.Confirm();
            await Task.Delay(600, ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 二维卡片/磁贴导航：根据 OCR 坐标把焦点移动到目标卡片并按 A。
    /// 通过比较各候选卡片外扩区域的白色描边亮度找当前焦点。
    /// 选词一律用原始 Words（不能用 MergedWords 行合并词：同一 y 高度的多张卡标题会被
    /// 拼成「赛程表兑换积分」这类横跨两卡的假词，中心被左卡拖走导致只上下移动、永不右移）。
    /// </summary>
    public async Task<bool> FocusAndConfirmAsync(string[] targetKeywords, string[] allCardKeywords, CancellationToken ct, int maxMoves = 14)
    {
        for (int move = 0; move < maxMoves; move++)
        {
            ct.ThrowIfCancellationRequested();
            var o = await _engine.ObserveAsync(ct);

            OcrWord? target = null;
            foreach (var kw in targetKeywords)
            {
                target = BotEngine.FindCardWord(o.Ocr, kw);
                if (target is not null) break;
            }
            if (target is null)
            {
                await _pad.Dpad(PadDir.Right);
                await Task.Delay(400, ct);
                continue;
            }

            // 找当前焦点卡片：候选卡片中白色描边比例最高者
            OcrWord? focus = null;
            double focusScore = 0;
            foreach (var kw in allCardKeywords)
            {
                var w = BotEngine.FindCardWord(o.Ocr, kw);
                if (w is null) continue;
                var region = new NormRect(
                    Math.Max(0, w.X - 0.09), Math.Max(0, w.Y - 0.30),
                    Math.Min(1, w.W + 0.18), Math.Min(1, w.H + 0.34));
                double white = FrameAnalyzer.ColorRatio(o.Frame, HsvFilter.White, region);
                if (white > focusScore) { focusScore = white; focus = w; }
            }

            if (focus is null || focusScore < 0.015)
            {
                // 无法定位焦点：先往左上收拢
                await _pad.Dpad(PadDir.Up);
                await Task.Delay(300, ct);
                continue;
            }

            double dx = target.CenterX - focus.CenterX;
            double dy = target.CenterY - focus.CenterY;
            _engine.LogInternal($"焦点导航：当前「{focus.Text}」→ 目标「{target.Text}」 dx={dx:F2} dy={dy:F2}", LogLevel.Debug);

            if (Math.Abs(dx) < 0.06 && Math.Abs(dy) < 0.08)
            {
                await _pad.Confirm();
                return true;
            }
            // 跨列时（dx≥0.18）横向优先：哪怕 dy 更大也先对齐列，避免被纵向距离锁死在左列震荡。
            // 对齐后再处理纵向；只剩小幅横向差（0.06~0.18）时做横向微调。
            if (Math.Abs(dx) >= 0.18)
                await _pad.Dpad(dx > 0 ? PadDir.Right : PadDir.Left);
            else if (Math.Abs(dy) >= 0.08)
                await _pad.Dpad(dy > 0 ? PadDir.Down : PadDir.Up);
            else
                await _pad.Dpad(dx > 0 ? PadDir.Right : PadDir.Left);
            await Task.Delay(450, ct);
        }
        return false;
    }
}
