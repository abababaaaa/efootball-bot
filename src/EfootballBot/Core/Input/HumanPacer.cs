namespace EfootballBot.Core.Input;

/// <summary>
/// 拟人节奏器：把固定按键时长/间隔乘性随机化为对数正态分布——
/// 采样值 = base × exp(N(0, σ))，中位数恰为 base、右偏长尾，
/// 是真人反应时间与操作间隔的经典分布（均匀随机在统计直方图上一眼假）。
/// 另以小概率插入 0.3~0.9 秒的「犹豫」停顿，模拟真人被手机/弹窗打断的瞬间。
/// </summary>
public sealed class HumanPacer
{
    /// <summary>默认离散度：σ=0.32 时单键 130ms 的实际分布约 70~270ms，覆盖真人典型区间。</summary>
    public const double DefaultSigma = 0.32;
    /// <summary>每次按键插入「犹豫」的概率（约每 50 次操作一次）。</summary>
    public const double DefaultHesitationProbability = 0.02;

    /// <summary>
    /// 相邻采样之间的「惯性」φ（AR(1) 自相关系数）。真实操作是成串的——
    /// 手顺的时候连续几下都快、卡顿时连续几下都慢，而独立同分布的对数正态虽然
    /// 单点分布正确，但在「序列」维度上一眼可辨（自相关恒为 0）。
    /// z_t = φ·z_{t-1} + √(1-φ²)·ε 保持单位方差不变：只增加序列相关性，
    /// 不改变单点分布（原有的中位数/长尾/滤波阈值全部不变）。
    /// </summary>
    public const double DefaultMomentum = 0.35;

    private readonly double _sigma;
    private readonly double _hesitationP;
    private readonly double _momentum;
    private readonly Random _rnd;

    private double _lastZ;
    private bool _hasZ;

    public HumanPacer(double sigma = DefaultSigma, double hesitationProbability = DefaultHesitationProbability,
        Random? rnd = null, double momentum = DefaultMomentum)
    {
        _sigma = Math.Clamp(sigma, 0.05, 1.0);
        _hesitationP = Math.Clamp(hesitationProbability, 0.0, 1.0);
        _momentum = Math.Clamp(momentum, 0.0, 0.95);
        _rnd = rnd ?? new Random();
    }

    /// <summary>
    /// 以 baseMs 为中位数采样一个拟人延迟。下限取 floorMs 与 baseMs 的较小者
    /// （保证慢节奏操作如 8s 等待不会被强行压到 40ms），上限同理。
    /// </summary>
    public int Sample(int baseMs, int floorMs = 40, int capMs = 4000)
    {
        if (baseMs <= 0) return baseMs;
        int ms = (int)Math.Round(baseMs * Math.Exp(NextZ() * _sigma));
        return Math.Clamp(ms, Math.Min(floorMs, baseMs), Math.Max(capMs, baseMs));
    }

    /// <summary>在 [minMs, maxMs] 内取值：以几何中位数 √(min·max) 为中心对数正态采样，裁剪到区间。</summary>
    public int SampleRange(int minMs, int maxMs)
    {
        int lo = Math.Min(minMs, maxMs), hi = Math.Max(minMs, maxMs);
        if (hi <= lo) return lo;
        double mid = Math.Sqrt((double)lo * hi);
        int ms = (int)Math.Round(mid * Math.Exp(NextZ() * _sigma));
        return Math.Clamp(ms, lo, hi);
    }

    /// <summary>本次操作前是否插入一次「犹豫」（真人被打断的停顿）。</summary>
    public bool RollHesitation() => _rnd.NextDouble() < _hesitationP;

    /// <summary>「犹豫」停顿时长。</summary>
    public int HesitationMs() => SampleRange(300, 900);

    /// <summary>标准正态采样（Box–Muller）。</summary>
    private double Normal()
    {
        double u1 = 1.0 - _rnd.NextDouble(); // 避开 log(0)
        double u2 = _rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// 带惯性的标准正态采样：AR(1) 递推，方差保持为 1（φ=0 时退化为独立同分布）。
    /// </summary>
    private double NextZ()
    {
        double z = Normal();
        if (_momentum > 0 && _hasZ)
            z = _momentum * _lastZ + Math.Sqrt(1.0 - _momentum * _momentum) * z;
        _lastZ = z;
        _hasZ = true;
        return z;
    }
}
