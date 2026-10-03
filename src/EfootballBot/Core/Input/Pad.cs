using EfootballBot.Core.Config;

namespace EfootballBot.Core.Input;

/// <summary>手柄语义动作：全部对应 eFootball 国际服默认键位（下一步=A，返回=B，方向=方向键）。</summary>
public sealed class Pad
{
    private readonly VirtualGamepad360 _vg;
    private readonly TimingConfig _t;
    private readonly Random _rnd = new();

    public Pad(VirtualGamepad360 vg, TimingConfig timing)
    {
        _vg = vg;
        _t = timing;
    }

    private int Jitter(int baseMs)
    {
        int j = Math.Max(0, Math.Min(200, _t.HumanJitterMs));
        int d = j == 0 ? 0 : _rnd.Next(-j, j + 1);
        return Math.Max(40, baseMs + d);
    }

    /// <summary>下一步 / 确认（手柄 A）。</summary>
    public Task Confirm() => _vg.TapAsync(PadButton.A, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));

    /// <summary>返回（手柄 B）。</summary>
    public Task Back() => _vg.TapAsync(PadButton.B, Jitter(_t.TapHoldMs), Jitter(_t.TapGapMs));

    public Task Dpad(PadDir dir) => _vg.TapDpadAsync(dir, Jitter(_t.DpadHoldMs), Jitter(_t.TapGapMs));

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
    /// </summary>
    public Task HumanWait(int minMs, int maxMs, CancellationToken ct)
    {
        int lo = Math.Clamp(Math.Min(minMs, maxMs), 0, 600_000);
        int hi = Math.Clamp(Math.Max(minMs, maxMs), lo, 600_000);
        return Task.Delay(_rnd.Next(lo, hi + 1), ct);
    }
}
