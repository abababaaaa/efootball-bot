using EfootballBot.Core.Config;

namespace EfootballBot.Core.Input;

/// <summary>手柄语义动作：全部对应 eFootball 国际服默认键位（下一步=A，返回=B，方向=方向键）。</summary>
public sealed class Pad
{
    private readonly IVirtualPad _vg;
    private readonly TimingConfig _t;
    private readonly Random _rnd = new();
    private readonly HumanPacer? _pace;

    public Pad(IVirtualPad vg, TimingConfig timing)
    {
        _vg = vg;
        _t = timing;
        _pace = timing.HumanPacingEnabled ? new HumanPacer() : null;
    }

    private int Jitter(int baseMs)
    {
        if (_pace is not null) return _pace.Sample(baseMs);
        int j = Math.Max(0, Math.Min(200, _t.HumanJitterMs));
        int d = j == 0 ? 0 : _rnd.Next(-j, j + 1);
        return Math.Max(40, baseMs + d);
    }

    /// <summary>小概率插入一次「犹豫」停顿（真人被手机/弹窗打断的瞬间），仅高频语义键。</summary>
    private async Task MaybeHesitate()
    {
        if (_pace is null || !_pace.RollHesitation()) return;
        await Task.Delay(_pace.HesitationMs());
    }

    /// <summary>下一步 / 确认（手柄 A）。</summary>
    public async Task Confirm()
    {
        await MaybeHesitate();
        await _vg.TapAsync(PadButton.A, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    }

    /// <summary>返回（手柄 B）。</summary>
    public async Task Back()
    {
        await MaybeHesitate();
        await _vg.TapAsync(PadButton.B, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    }

    /// <summary>按任意指定按钮（供声明式导航图使用）。</summary>
    public async Task Press(PadButton b)
    {
        await MaybeHesitate();
        await _vg.TapAsync(b, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    }

    public async Task Dpad(PadDir dir)
    {
        await MaybeHesitate();
        await _vg.TapDpadAsync(dir, Jitter(_t.DpadHoldMs), Jitter(_t.TapGapMs));
    }

    public Task X() => _vg.TapAsync(PadButton.X, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    public Task Y() => _vg.TapAsync(PadButton.Y, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    public Task LB() => _vg.TapAsync(PadButton.LeftShoulder, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    public Task RB() => _vg.TapAsync(PadButton.RightShoulder, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    public Task R3() => _vg.TapAsync(PadButton.RightThumb, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    public Task L3() => _vg.TapAsync(PadButton.LeftThumb, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));
    public Task Start() => _vg.TapAsync(PadButton.Start, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));

    /// <summary>右扳机 RT（个别赛前确认页右下角提示「RT 确定」）。</summary>
    public async Task RT()
    {
        _vg.SetRightTrigger(255);
        await Task.Delay(Jitter(_t.TapHoldMs));
        _vg.SetRightTrigger(0);
        await Task.Delay(Jitter(_t.TapGapMs));
    }

    /// <summary>右摇杆轻推（联赛主页右侧“使用的物品”翻页）。</summary>
    public Task RsFlick(PadDir dir) => _vg.FlickRightStickAsync(dir, Jitter(160), Jitter(200));

    public Task DpadRepeat(PadDir dir, int count)
        => RepeatAsync(count, () => Dpad(dir));

    /// <summary>
    /// 快速连按方向键（换人巡检等状态机的批量移光标用）：保持/间隔减半、跳过拟人犹豫。
    /// 机械导航不承担拟人语义——真人推方向键本就是连续快按；单步 Dpad 仍走完整拟人节奏。
    /// </summary>
    public async Task DpadRepeatFast(PadDir dir, int count)
    {
        int hold = Jitter(Math.Max(60, _t.DpadHoldMs / 2));
        int gap = Jitter(Math.Max(80, _t.TapGapMs / 2));
        for (int i = 0; i < count; i++)
            await _vg.TapDpadAsync(dir, hold, gap);
    }

    /// <summary>结算 / 奖励页：缓慢连按 A。</summary>
    public async Task ConfirmMany(int count, CancellationToken ct)
    {
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Confirm();
            await Task.Delay(Jitter(260), ct);
        }
    }

    public static async Task RepeatAsync(int count, Func<Task> action)
    {
        for (int i = 0; i < count; i++) await action();
    }

    public Task Wait(int ms, CancellationToken ct) => Task.Delay(Jitter(ms), ct);

    /// <summary>
    /// 拟人随机等待（闭区间 [minMs, maxMs]），用于非必须立即的连点场景
    /// （比赛中挂机按 A、赛后弹窗确认），避免固定节奏被识别为脚本。
    /// 拟人节奏开启时取对数正态簇状分布（真人操作成簇、有疏密），
    /// 关闭时退回均匀分布。
    /// </summary>
    public Task HumanWait(int minMs, int maxMs, CancellationToken ct)
    {
        int lo = Math.Clamp(Math.Min(minMs, maxMs), 0, 600_000);
        int hi = Math.Clamp(Math.Max(minMs, maxMs), lo, 600_000);
        int ms = _pace is not null ? _pace.SampleRange(lo, hi) : _rnd.Next(lo, hi + 1);
        return Task.Delay(ms, ct);
    }
}
