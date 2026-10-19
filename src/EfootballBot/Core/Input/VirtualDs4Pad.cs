using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// ViGEm 虚拟 DualShock4 手柄（默认后端）。DS4 是纯 HID 设备、不存在 XInput/XUSB 通道：
/// XInput-only 的应用（绝大多数 PC 游戏、Steam 的 XInput 通道）根本看不到它，
/// 从设备类别上消除输入泄漏；HID 层再叠加 HidHide 白名单后，全系统仅 eFootball 可见。
/// eFootball 侧经其 DirectInput/HID 手柄枚举路径（exe 内实锤 GameHidEnumDInput/HID.DLL）接收，
/// 无 XInput 槽位概念（UserIndex 恒 -1），不参与玩家 1 仲裁。
/// 依赖与 <see cref="VirtualGamepad360"/> 相同的 ViGEmBus 驱动，无需额外安装任何东西。
/// </summary>
public sealed class VirtualDs4Pad : IVirtualPad
{
    private ViGEmClient? _client;
    private IDualShock4Controller? _pad;
    private volatile bool _connected;

    public PadBackendKind Kind => PadBackendKind.HidDs4;

    /// <summary>纯 HID 手柄无 XInput 槽位概念，恒为 -1。</summary>
    public int UserIndex { get; private set; } = -1;
    public bool IsConnected => _connected;

    public void Connect()
    {
        if (_connected) return;
        if (!VirtualGamepad360.IsDriverInstalled())
            throw new InvalidOperationException("未检测到 ViGEmBus 驱动，请先安装 ViGEmBus Setup。");

        try
        {
            _client ??= new ViGEmClient();
            _pad = _client.CreateDualShock4Controller();
            _pad.AutoSubmitReport = false; // 手动提交，保证一次状态变化一次上报
            _pad.Connect();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"连接 ViGEm DualShock4 失败：{ex.Message}。请确认 ViGEmBus 驱动已正确安装并重启过电脑。", ex);
        }

        _connected = true;

        // 上电后先提交一次“全释放”状态，确保游戏识别到手柄
        _pad.ResetReport();
        _pad.SubmitReport();
    }

    // ---- 状态操作 ----

    /// <summary>PadButton → DS4 按方位映射：A/B/X/Y → Cross/Circle/Square/Triangle（与 Xbox 布局同位）。
    /// 十字键走 DPad，返回 null。</summary>
    internal static DualShock4Button? Map(PadButton b) => b switch
    {
        PadButton.A => DualShock4Button.Cross,
        PadButton.B => DualShock4Button.Circle,
        PadButton.X => DualShock4Button.Square,
        PadButton.Y => DualShock4Button.Triangle,
        PadButton.LeftShoulder => DualShock4Button.ShoulderLeft,
        PadButton.RightShoulder => DualShock4Button.ShoulderRight,
        PadButton.Back => DualShock4Button.Share,
        PadButton.Start => DualShock4Button.Options,
        PadButton.LeftThumb => DualShock4Button.ThumbLeft,
        PadButton.RightThumb => DualShock4Button.ThumbRight,
        _ => null,
    };

    /// <summary>摇杆 short(-32768~32767) → DS4 轴 byte(0~255)，中位 128（可单测）。</summary>
    internal static byte AxisByte(short v) => (byte)Math.Clamp((int)Math.Round(128 + v * 127.0 / short.MaxValue), 0, 255);

    internal static DualShock4DPadDirection MapDpad(PadDir? dir) => dir switch
    {
        PadDir.Up => DualShock4DPadDirection.North,
        PadDir.Down => DualShock4DPadDirection.South,
        PadDir.Left => DualShock4DPadDirection.West,
        PadDir.Right => DualShock4DPadDirection.East,
        _ => DualShock4DPadDirection.None,
    };

    public void SetButton(PadButton btn, bool pressed)
    {
        if (_pad is null) return;
        if (Map(btn) is not { } b) return; // 十字键位走 SetDpad
        _pad.SetButtonState(b, pressed);
        _pad.SubmitReport();
    }

    public void SetDpad(PadDir? dir)
    {
        if (_pad is null) return;
        _pad.SetDPadDirection(MapDpad(dir));
        _pad.SubmitReport();
    }

    public void SetLeftStick(short x, short y)
    {
        if (_pad is null) return;
        _pad.SetAxisValue(DualShock4Axis.LeftThumbX, AxisByte(x));
        _pad.SetAxisValue(DualShock4Axis.LeftThumbY, AxisByte(y));
        _pad.SubmitReport();
    }

    public void SetRightStick(short x, short y)
    {
        if (_pad is null) return;
        _pad.SetAxisValue(DualShock4Axis.RightThumbX, AxisByte(x));
        _pad.SetAxisValue(DualShock4Axis.RightThumbY, AxisByte(y));
        _pad.SubmitReport();
    }

    public void SetLeftTrigger(byte value)
    {
        if (_pad is null) return;
        _pad.SetSliderValue(DualShock4Slider.LeftTrigger, value);
        _pad.SubmitReport();
    }

    public void SetRightTrigger(byte value)
    {
        if (_pad is null) return;
        _pad.SetSliderValue(DualShock4Slider.RightTrigger, value);
        _pad.SubmitReport();
    }

    public void ResetAll()
    {
        if (_pad is null) return;
        _pad.ResetReport();
        _pad.SubmitReport();
    }

    /// <summary>
    /// 断开虚拟手柄但保留客户端（HID 设备从系统消失，其他应用立即看不到任何手柄）。
    /// 断开后可再次调用 Connect 重连。
    /// </summary>
    public void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        try { _pad?.ResetReport(); _pad?.SubmitReport(); } catch { }
        try { _pad?.Disconnect(); } catch { }
        _pad = null;
        UserIndex = -1;
    }

    public void Dispose()
    {
        Disconnect();
        try { _client?.Dispose(); } catch { }
        _client = null;
        GC.SuppressFinalize(this);
    }
}
