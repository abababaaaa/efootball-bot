using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// 工程化改动的契约测试：影子手柄、拟人节奏惯性、作息随机休息、配置版本迁移。
/// 这几项都是「默认行为不能变」的功能开关——默认值下必须与旧行为逐字一致。
/// </summary>
public class EngineeringTests
{
    /// <summary>测试用虚拟手柄：只记录收到的原语调用。</summary>
    private sealed class RecorderPad : IVirtualPad
    {
        public List<string> Calls { get; } = new();
        public PadBackendKind Kind => PadBackendKind.Vigem;
        public int UserIndex => 0;
        public bool IsConnected => true;
        public void Connect() => Calls.Add("connect");
        public void Disconnect() => Calls.Add("disconnect");
        public void SetButton(PadButton btn, bool pressed) => Calls.Add($"btn:{btn}:{pressed}");
        public void SetDpad(PadDir? dir) => Calls.Add($"dpad:{dir}");
        public void SetLeftStick(short x, short y) => Calls.Add($"ls:{x},{y}");
        public void SetRightStick(short x, short y) => Calls.Add($"rs:{x},{y}");
        public void SetLeftTrigger(byte v) => Calls.Add($"lt:{v}");
        public void SetRightTrigger(byte v) => Calls.Add($"rt:{v}");
        public void ResetAll() => Calls.Add("reset");
        public void Dispose() { }
    }

    [Fact]
    public void DryRunPad_SwallowsAllInput_ButKeepsLifecycle()
    {
        var inner = new RecorderPad();
        var pad = new DryRunPad(inner);

        pad.Connect();
        pad.SetButton(PadButton.A, true);
        pad.SetButton(PadButton.A, false);
        pad.SetDpad(PadDir.Down);
        pad.SetDpad(null);
        pad.SetLeftStick(100, 0);
        pad.SetRightStick(0, -100);
        pad.SetLeftTrigger(200);
        pad.SetRightTrigger(255);
        pad.Disconnect();

        // 生命周期照常透传（捕获/slot 检测逻辑要与真实运行一致）
        Assert.Contains("connect", inner.Calls);
        Assert.Contains("disconnect", inner.Calls);
        // 一切输入都不得到达真实手柄
        Assert.DoesNotContain(inner.Calls, c => c.StartsWith("btn:") || c.StartsWith("dpad:")
            || c.StartsWith("ls:") || c.StartsWith("rs:") || c.StartsWith("lt:") || c.StartsWith("rt:"));
        // 释放事件不计数，按下/方向/轴/扳机各计一次
        Assert.Equal(6, pad.SuppressedActions);
    }

    [Fact]
    public void HumanPacer_Momentum_KeepsMedian_AndAddsAutocorrelation()
    {
        var rnd = new Random(20261021);
        var pacer = new HumanPacer(sigma: HumanPacer.DefaultSigma, rnd: rnd,
            momentum: HumanPacer.DefaultMomentum);
        var logRatios = new List<double>();
        for (int i = 0; i < 4000; i++)
            logRatios.Add(Math.Log(pacer.Sample(1000, floorMs: 40, capMs: 4000) / 1000.0));

        // 中位数不变（惯性只加序列相关，不改单点分布）
        var sorted = logRatios.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        Assert.InRange(median, -0.05, 0.05);

        // 滞后 1 自相关显著为正（φ=0.35 的 AR(1)）
        double mean = logRatios.Average();
        double num = 0, den = 0;
        for (int i = 0; i < logRatios.Count; i++)
        {
            double d = logRatios[i] - mean;
            den += d * d;
            if (i + 1 < logRatios.Count) num += d * (logRatios[i + 1] - mean);
        }
        double acf1 = num / den;
        Assert.True(acf1 > 0.2, $"滞后 1 自相关应≈0.35，实测 {acf1:F3}");

        // φ=0 时必须退回独立同分布（旧行为可完全复现）
        var iid = new HumanPacer(sigma: HumanPacer.DefaultSigma, rnd: new Random(1), momentum: 0);
        double n2 = 0, d2 = 0, m2 = 0;
        var z = Enumerable.Range(0, 4000).Select(_ => Math.Log(iid.Sample(1000) / 1000.0)).ToList();
        m2 = z.Average();
        for (int i = 0; i < z.Count; i++)
        {
            double d = z[i] - m2;
            d2 += d * d;
            if (i + 1 < z.Count) n2 += d * (z[i + 1] - m2);
        }
        Assert.InRange(n2 / d2, -0.1, 0.1);
    }

    [Fact]
    public void SessionScheduler_RestDayChance100_NeverRuns()
    {
        var cfg = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "00:00",
            EndTime = "23:59",
            DailyCapHours = 0,
            DriftMinutes = 0,
            RestDayChancePercent = 100,
        };
        var s = new SessionScheduler(cfg, (_, _) => { }, new Random(7));
        Assert.False(s.ShouldRunNow(DateTime.Today.AddHours(12)));
        Assert.False(s.ShouldRunNow(DateTime.Today.AddHours(13)));
    }

    [Fact]
    public void SessionScheduler_RestDayChance0_RunsInWindow()
    {
        var cfg = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "00:00",
            EndTime = "23:59",
            DailyCapHours = 0,
            DriftMinutes = 0,
            RestDayChancePercent = 0,
        };
        var s = new SessionScheduler(cfg, (_, _) => { }, new Random(7));
        Assert.True(s.ShouldRunNow(DateTime.Today.AddHours(12)));
    }

    [Fact]
    public void AppConfig_Migrate_UpgradesLegacyWithoutTouchingValues()
    {
        var cfg = new AppConfig
        {
            SchemaVersion = 1,
            Theme = "Light",
            AutoCloseWhenDone = true,
            DryRun = true,
            Notify = new NotifyConfig { WebhookUrl = "https://example.invalid/hook" },
        };
        cfg.Migrate();
        Assert.Equal(AppConfig.CurrentSchemaVersion, cfg.SchemaVersion);
        // 迁移只做结构补全，绝不重置用户取值
        Assert.Equal("Light", cfg.Theme);
        Assert.True(cfg.AutoCloseWhenDone);
        Assert.True(cfg.DryRun);
        Assert.Equal("https://example.invalid/hook", cfg.Notify.WebhookUrl);

        // 幂等：再跑一次不改变任何东西
        int before = cfg.SchemaVersion;
        cfg.Migrate();
        Assert.Equal(before, cfg.SchemaVersion);
    }
}
