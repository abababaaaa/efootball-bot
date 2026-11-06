namespace EfootballBot.Core.Bot;

/// <summary>
/// 观察循环分段耗时统计（P1-14）：取帧 / 指纹 / OCR / YOLO / 手柄动作 / 整轮。
///
/// 为什么要有它：这一版的性能优化（哈希只算一次、模型复用、检测降采样）如果没有量化口径，
/// 改完只能靠"感觉快了"。这里用 EWMA（α=0.2）做滚动平均，每 60 秒打一条日志并同步到
/// 侧栏，让"谁慢"一眼可见——旧实现日志里只有「OCR N 词」，看不出单项耗时。
///
/// 口径：
/// - 取帧 = WaitForFreshFrameAsync（含捕获陈旧时的重建等待）
/// - 指纹 = 静帧去重的全帧哈希
/// - OCR = 真正跑推理的时间（命中缓存记 0）
/// - YOLO = 目标检测（未启用模型时恒 0）
/// - 动作 = 场景在一轮里花在 Pad（手柄提交）上的累计时间
/// - 整轮 = 从开始观察到场景返回下一轮延时
/// - 其余 = 整轮 - 以上各项（场景自身判定 + 场景内的固定等待）
/// </summary>
public sealed class PerfTrace
{
    private const double Alpha = 0.2;

    /// <summary>滚动平均（毫秒）。</summary>
    public double FrameMs { get; private set; }
    public double HashMs { get; private set; }
    public double OcrMs { get; private set; }
    public double YoloMs { get; private set; }
    public double ActionMs { get; private set; }
    public double RoundMs { get; private set; }
    public double IdleMs { get; private set; }

    /// <summary>累计采样轮数（0 = 尚无数据）。</summary>
    public int Rounds { get; private set; }

    public void Record(long frameMs, long hashMs, long ocrMs, long yoloMs, long actionMs, long roundMs)
    {
        double idle = roundMs - frameMs - hashMs - ocrMs - yoloMs - actionMs;
        if (idle < 0) idle = 0; // 各项由不同 Stopwatch 取整，理论上可能轻微超出，钳到 0

        if (Rounds == 0)
        {
            FrameMs = frameMs; HashMs = hashMs; OcrMs = ocrMs;
            YoloMs = yoloMs; ActionMs = actionMs; RoundMs = roundMs; IdleMs = idle;
        }
        else
        {
            FrameMs = Mix(FrameMs, frameMs);
            HashMs = Mix(HashMs, hashMs);
            OcrMs = Mix(OcrMs, ocrMs);
            YoloMs = Mix(YoloMs, yoloMs);
            ActionMs = Mix(ActionMs, actionMs);
            RoundMs = Mix(RoundMs, roundMs);
            IdleMs = Mix(IdleMs, idle);
        }
        Rounds++;
    }

    /// <summary>清空（每次开始挂机重置，避免上一场的数据混进来）。</summary>
    public void Reset()
    {
        Rounds = 0;
        FrameMs = HashMs = OcrMs = YoloMs = ActionMs = RoundMs = IdleMs = 0;
    }

    private static double Mix(double avg, double sample) => avg + Alpha * (sample - avg);

    /// <summary>一行可读摘要（日志与侧栏共用）。</summary>
    public string Describe()
        => Rounds == 0
            ? "尚无数据"
            : $"轮 {RoundMs:F0}ms｜取帧 {FrameMs:F0} / 指纹 {HashMs:F0} / OCR {OcrMs:F0} / " +
              $"YOLO {YoloMs:F0} / 动作 {ActionMs:F0} / 其余 {IdleMs:F0}";
}
