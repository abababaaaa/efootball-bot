using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

// 拟人节奏器：对数正态分布采样（纯逻辑，注入 Random 确定性验证）
public class HumanPacerTests
{
    [Fact]
    public void Sample_MedianNearBase()
    {
        var p = new HumanPacer(rnd: new Random(42));
        var samples = Enumerable.Range(0, 4000).Select(_ => p.Sample(130)).ToList();

        // 对数正态中位数 = base：±15% 内应含约一半样本
        double median = samples.OrderBy(x => x).ElementAt(samples.Count / 2);
        Assert.InRange(median, 130 * 0.85, 130 * 1.15);

        // 全部落在合理范围（右偏长尾但被 cap 约束）
        Assert.All(samples, s => Assert.InRange(s, 40, 4000));
    }

    [Fact]
    public void Sample_RightSkewed_LongTailExists()
    {
        var p = new HumanPacer(rnd: new Random(7));
        var samples = Enumerable.Range(0, 4000).Select(_ => p.Sample(130)).ToList();

        // 右偏：均值明显高于中位数，且存在显著高于 base 的长尾值
        double mean = samples.Average();
        double median = samples.OrderBy(x => x).ElementAt(samples.Count / 2);
        Assert.True(mean > median * 1.03, $"mean={mean:F1} median={median}");
        Assert.Contains(samples, s => s > 130 * 1.8);
    }

    [Fact]
    public void Sample_NeverBelowFloor()
    {
        var p = new HumanPacer(sigma: 1.0, rnd: new Random(1)); // 大 σ 放大越界概率
        for (int i = 0; i < 5000; i++)
            Assert.InRange(p.Sample(120, floorMs: 80), 80, int.MaxValue);
    }

    [Fact]
    public void Sample_SlowBase_NotClampedToFloor()
    {
        // 慢节奏等待（如 5s）不会被下限强行压小
        var p = new HumanPacer(rnd: new Random(3));
        int v = p.Sample(5000, floorMs: 40);
        Assert.InRange(v, 5000 * 3 / 4, 5000 * 5 / 4); // σ=0.32 单样本合理带宽
    }

    [Fact]
    public void SampleRange_AlwaysWithinBounds()
    {
        var p = new HumanPacer(sigma: 0.8, rnd: new Random(11)); // 极端 σ
        for (int i = 0; i < 5000; i++)
        {
            int v = p.SampleRange(1000, 4000);
            Assert.InRange(v, 1000, 4000);
        }
    }

    [Fact]
    public void SampleRange_ClustersTowardGeometricMedian()
    {
        var p = new HumanPacer(rnd: new Random(5));
        var samples = Enumerable.Range(0, 4000).Select(_ => p.SampleRange(1000, 4000)).ToList();
        // 几何中位数 √(1000·4000)=2000：中位数应显著偏向该值而非区间中点 2500
        double median = samples.OrderBy(x => x).ElementAt(samples.Count / 2);
        Assert.InRange(median, 1700, 2300);
    }

    [Fact]
    public void RollHesitation_RateMatchesProbability()
    {
        var p = new HumanPacer(hesitationProbability: 0.02, rnd: new Random(9));
        int hits = Enumerable.Range(0, 20_000).Count(_ => p.RollHesitation());
        double rate = hits / 20_000.0;
        Assert.InRange(rate, 0.01, 0.03);
    }

    [Fact]
    public void HesitationMs_WithinHumanRange()
    {
        var p = new HumanPacer(rnd: new Random(2));
        for (int i = 0; i < 500; i++)
            Assert.InRange(p.HesitationMs(), 300, 900);
    }
}
