namespace EfootballBot.Core.Input;

/// <summary>
/// 虚拟手柄的后端抽象。实现：<see cref="HidMaestroPad"/>（HIDMaestro 用户态驱动，
/// 纯 HID，唯一后端；ViGEm 两档已随 3.0 移除）。
/// 语义动作（Tap/Flick）以默认接口成员实现在原语之上。
/// </summary>
public interface IVirtualPad : IDisposable
{
    /// <summary>后端种类。</summary>
    PadBackendKind Kind { get; }

    /// <summary>手柄在系统中的序号（0-3），-1 表示尚未取得（纯 HID 后端恒为 -1，无 XInput 槽位概念）。</summary>
    int UserIndex { get; }

    bool IsConnected { get; }

    /// <summary>创建并上线虚拟手柄（含驱动缺失时的报错）。</summary>
    void Connect();

    /// <summary>断开虚拟手柄但保留后端（释放 XInput 槽位）。断开后可再次 Connect。</summary>
    void Disconnect();

    void SetButton(PadButton btn, bool pressed);
    void SetDpad(PadDir? dir);
    void SetLeftStick(short x, short y);
    void SetRightStick(short x, short y);
    void SetLeftTrigger(byte value);
    void SetRightTrigger(byte value);

    /// <summary>全键释放并提交一次空报告。</summary>
    void ResetAll();

    async Task TapAsync(PadButton button, int holdMs, int postDelayMs)
    {
        SetButton(button, true);
        await Task.Delay(holdMs);
        SetButton(button, false);
        await Task.Delay(postDelayMs);
    }

    Task TapAsync(PadButton button) => TapAsync(button, 120, 180);

    async Task TapDpadAsync(PadDir dir, int holdMs, int postDelayMs)
    {
        SetDpad(dir);
        await Task.Delay(holdMs);
        SetDpad(null);
        await Task.Delay(postDelayMs);
    }

    Task TapDpadAsync(PadDir dir) => TapDpadAsync(dir, 120, 200);

    async Task FlickLeftStickAsync(PadDir dir, int holdMs, int postDelayMs)
    {
        const short v = 26000;
        (short x, short y) = dir switch
        {
            PadDir.Up => ((short)0, v),
            PadDir.Down => ((short)0, (short)-v),
            PadDir.Left => ((short)-v, (short)0),
            PadDir.Right => (v, (short)0),
            _ => ((short)0, (short)0),
        };
        SetLeftStick(x, y);
        await Task.Delay(holdMs);
        SetLeftStick(0, 0);
        await Task.Delay(postDelayMs);
    }

    Task FlickLeftStickAsync(PadDir dir) => FlickLeftStickAsync(dir, 160, 200);

    async Task FlickRightStickAsync(PadDir dir, int holdMs, int postDelayMs)
    {
        const short v = 26000;
        (short x, short y) = dir switch
        {
            PadDir.Up => ((short)0, v),
            PadDir.Down => ((short)0, (short)-v),
            PadDir.Left => ((short)-v, (short)0),
            PadDir.Right => (v, (short)0),
            _ => ((short)0, (short)0),
        };
        SetRightStick(x, y);
        await Task.Delay(holdMs);
        SetRightStick(0, 0);
        await Task.Delay(postDelayMs);
    }

    Task FlickRightStickAsync(PadDir dir) => FlickRightStickAsync(dir, 160, 200);
}
