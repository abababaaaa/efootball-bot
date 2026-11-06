using EfootballBot.Core.Input;
using EfootballBot.Core.Vision;

namespace EfootballBot.Core.Bot;

/// <summary>
/// 手柄输入活性探针（P0-1）：判断"游戏到底有没有收到我们的按键"。
///
/// 背景（docs/superpowers/specs/2026-10-04-input-liveness-probe-design.md）：掌机睡眠唤醒后
/// Steam Input 转发层偶发卡死——设备全部"在线"、bot 照常按导航序列，但游戏一个键都收不到，
/// 旧实现只能在页面等待超时后抛一个技术栈。此前的设计文档只做"回主页时按方向键看高亮是否移动"，
/// 落地时改成更稳的口径：
///
///   · 触发条件不靠像素（菜单背景有动画，像素永远在变），而靠**OCR 文字指纹**：
///     同一段可读文字连续 45 秒一字不差、期间还按过 3 次键 → 这不是"正在玩游戏"，是"卡住了"。
///     比赛中的比分/计时每秒都在变，天然不会触发（比赛不会误判）。
///   · 探测动作 = 依次按 下 / A / B，每次之后强制重跑一次 OCR：任何一次文字变了就说明
///     游戏确实收到了输入，判活；三次都没变才判死。
///
/// 判死后由引擎按设计文档给一句可操作的提示并停止（继续盲按只会让用户以为程序坏了）。
/// </summary>
public static class InputLivenessProbe
{
    /// <summary>文字指纹最短长度：太短的页面（"加载中"之类）不参与判定，避免误报。</summary>
    public const int MinSignatureChars = 12;

    /// <summary>同屏文字保持不变多久才值得探测（毫秒）。</summary>
    public const int StuckMs = 45_000;

    /// <summary>这期间至少要按下这么多次键，才说明"我们确实在操作但它没反应"。</summary>
    public const int MinActionsDuringStuck = 3;

    /// <summary>
    /// 页面文字指纹：按阅读顺序拼接的规范化词文本（去空白/标点）。
    /// 与像素无关——菜单背景动画、粒子特效不会影响它；页面内容一变（哪怕只多一个按钮）就会变。
    /// 截断到 600 字符控制比较成本（整页最多百余词，正常不会到上限）。
    /// </summary>
    public static string TextSignature(OcrResult ocr)
    {
        if (ocr.Words.Count == 0) return "";
        var sb = new global::System.Text.StringBuilder(256);
        foreach (var w in ocr.MergedWords)
        {
            sb.Append(w.NormText);
            if (sb.Length >= 600) break;
        }
        return sb.Length > 600 ? sb.ToString(0, 600) : sb.ToString();
    }

    /// <summary>页面文字够不够"可读"（太短视为加载页/黑屏，不参与活性判定）。</summary>
    public static bool IsReadable(string signature) => signature.Length >= MinSignatureChars;

    /// <summary>探测结论：是否活着 + 每一步的观测文本（日志/排障用）。</summary>
    public readonly record struct Result(bool Alive, string Detail);

    /// <summary>
    /// 依次按 下 / A / B 并各等一次强制 OCR：任意一步页面文字变了 = 输入活着。
    /// 只按这三下是因为它们本来就是"卡住时 bot 会按的键"，不会引入额外的副作用；
    /// 每一步之后都强制重跑 OCR（绕过 900ms 节流），否则读到的还是按键前的缓存结果。
    /// </summary>
    public static async Task<Result> ProbeAsync(Observation current,
        Func<CancellationToken, Task<Observation>> observe, Pad pad, IReadOnlyList<PadButton>? buttons,
        CancellationToken ct)
    {
        var seq = buttons ?? new[] { PadButton.DpadDown, PadButton.A, PadButton.B };
        // 基线直接用调用方刚拿到的那一帧：OCR 一帧要 ~1s，没必要为了读基线再跑一遍
        string before = TextSignature(current.Ocr);
        var steps = new global::System.Text.StringBuilder();
        foreach (var b in seq)
        {
            ct.ThrowIfCancellationRequested();
            if (b == PadButton.DpadDown) await pad.Dpad(PadDir.Down);
            else if (b == PadButton.A) await pad.Confirm();
            else if (b == PadButton.B) await pad.Back();
            else await pad.Press(b);

            await Task.Delay(700, ct);
            var now = TextSignature((await observe(ct)).Ocr);
            bool changed = now != before;
            steps.Append(changed ? $"{b}:变化 " : $"{b}:无变化 ");
            if (changed) return new Result(true, steps.ToString());
        }
        return new Result(false, steps.ToString());
    }

    /// <summary>判死时给用户的提示（与设计文档同一口径：说清楚"下一步做什么"，不打堆栈）。</summary>
    public const string DeadMessage =
        "游戏没有收到任何手柄输入：同一页面文字卡住超过 45 秒，期间按了方向键 / A / B 都没有任何反应。" +
        "挂机已停止，避免继续盲按。\n" +
        "常见原因：掌机/笔记本睡眠唤醒后 Steam Input 转发层卡死。\n" +
        "处理办法：完全退出 Steam（托盘右键→退出）后重新打开游戏再试，必要时重启电脑；" +
        "也可在 Steam 库右键 eFootball → 属性 → 控制器，停用 Steam Input。";
}

/// <summary>手柄输入无响应（不是程序 bug，是系统输入链路已死）。引擎捕获后给友好提示并停止。</summary>
public sealed class InputNotRespondingException : Exception
{
    public InputNotRespondingException(string message) : base(message) { }
}
