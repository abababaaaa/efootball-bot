using System.Runtime.InteropServices;

namespace EfootballBot.Core.Input;

/// <summary>
/// 真实手柄活动监视器（共存模式传感器，Worker 进程内）。双通道互补：
/// ① 主传感：100ms 轮询全部 4 个 XInput 槽位——按钮/扳机(&gt;30%)/摇杆(出死区)任一保持激活即算活动，
///    死区阈值天然免疫摇杆漂移；覆盖 Xbox 系手柄（XInput 路径）。
/// ② 副传感：RawInput WM_INPUT 全局下沉（RIDEV_INPUTSINK + 专用消息线程），覆盖 DS4/DualSense/
///    蓝牙等不暴露 XInput 的手柄（事件边沿语义）。按设备实例路径过滤本程序自己的虚拟手柄：
///    虚拟 DS4 的 HID 集合形态为 HID\HIDCLASS\*（不含 VID），IsHidMaestroInstance 精确识别——
///    绝不能把自己的按键当作用户活动，否则挂机自触发永久让步。真实 DS4 路径带 VID_054C，不匹配。
/// 对外仅暴露 <see cref="LastActivityUtc"/>；Start/Stop 幂等；任一通道失败静默降级（另一通道仍工作）。
/// </summary>
public sealed class RealPadActivityMonitor : IDisposable
{
    private long _lastActivityTicks;            // DateTime.UtcTicks，0 = 尚无活动
    private Thread? _rawThread;
    private uint _rawThreadId;
    private nint _hwnd;
    private byte[]? _rawBuf;
    private char[]? _nameBuf;
    private bool _disposed;

    private static RealPadActivityMonitor? s_instance;

    public DateTime? LastActivityUtc
    {
        get
        {
            long t = Interlocked.Read(ref _lastActivityTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    public void Start()
    {
        if (_disposed) return;
        StartXInputPolling();
        StartRawInputSink();
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    // ---------------- ① XInput 槽位轮询 ----------------

    private void StartXInputPolling()
    {
        _ = Task.Run(async () =>
        {
            while (!_disposed)
            {
                try
                {
                    for (int slot = 0; slot < 4; slot++)
                    {
                        if (PadBackendProbe.XInputGetState(slot, out var s) != 0) continue;
                        // 持续激活语义：按住不放也算占用。摇杆死区取 XInput 默认（7849）的约 1.5 倍，
                        // 漂移摇杆（通常 <8000）不误报；扳机 77 = 30%
                        bool active = s.wButtons != 0
                            || s.bLeftTrigger > 77 || s.bRightTrigger > 77
                            || Math.Abs(s.sThumbLX) > 12000 || Math.Abs(s.sThumbLY) > 12000
                            || Math.Abs(s.sThumbRX) > 12000 || Math.Abs(s.sThumbRY) > 12000;
                        if (active) Touch();
                    }
                }
                catch { /* XInput 不可用：副传感仍工作 */ }
                await Task.Delay(100);
            }
        });
    }

    // ---------------- ② RawInput 全局下沉 ----------------

    private void StartRawInputSink()
    {
        _rawThread = new Thread(RawInputThreadProc)
        {
            Name = "RealPadRawInput",
            IsBackground = true,
        };
        _rawThread.SetApartmentState(ApartmentState.STA);
        _rawThread.Start();
    }

    private void RawInputThreadProc()
    {
        try
        {
            s_instance = this;
            _rawThreadId = GetCurrentThreadId();

            var cls = new WNDCLASSW
            {
                lpfnWndProc = s_wndProc,
                lpszClassName = WindowClassName,
                hInstance = GetModuleHandleW(null),
            };
            if (RegisterClassW(ref cls) == 0) return; // 重复注册（同进程二次 Start）也不致命：CreateWindow 仍可用已有类
            _hwnd = CreateWindowExW(0, WindowClassName, "EfootballBot pad monitor", 0,
                0, 0, 0, 0, HWND_MESSAGE, nint.Zero, nint.Zero, nint.Zero);
            if (_hwnd == nint.Zero) return;

            // UsagePage 1（Generic Desktop）：Usage 4 = Joystick、5 = Gamepad，盖住主流手柄与街机摇杆
            var rid = new RAWINPUTDEVICE[]
            {
                new() { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
                new() { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            };
            if (!RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                return;

            while (GetMessageW(out MSG msg, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch { /* 副传感失败：XInput 通道仍工作 */ }
        finally
        {
            if (s_instance == this) s_instance = null;
        }
    }

    private static readonly WNDPROC s_wndProc = WndProc;
    private const string WindowClassName = "EfootballBotPadMon";

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        const uint WM_INPUT = 0x00FF;
        const uint WM_DESTROY = 0x0002;
        if (msg == WM_INPUT && s_instance is { } mon)
            mon.HandleRawInput(lParam);
        else if (msg == WM_DESTROY)
            PostQuitMessage(0);
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private unsafe void HandleRawInput(nint lParam)
    {
        try
        {
            // 只需 RAWINPUTHEADER 里的 hDevice（设备身份），不解析输入报文——任何来自真实手柄的
            // 输入事件（含摇杆微动）都算用户活动
            uint size = 0;
            if (GetRawInputData(lParam, RID_INPUT, nint.Zero, ref size, (uint)(8 + 2 * nint.Size)) == uint.MaxValue)
                return;
            var buf = _rawBuf;
            if (buf is null || buf.Length < size) buf = _rawBuf = new byte[Math.Max(size, 256)];
            nint hDevice;
            fixed (byte* p = buf)
            {
                if (GetRawInputData(lParam, RID_INPUT, (nint)p, ref size, (uint)(8 + 2 * nint.Size)) == uint.MaxValue)
                    return;
                // RAWINPUTHEADER 布局：dwType(4) + dwSize(4) + hDevice(指针大小) → hDevice 在偏移 8
                hDevice = Marshal.ReadIntPtr((nint)p, sizeof(uint) * 2);
            }
            if (hDevice == nint.Zero) return;

            uint nameChars = 0;
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, nint.Zero, ref nameChars) == uint.MaxValue || nameChars == 0)
                return;
            var nameBuf = _nameBuf;
            if (nameBuf is null || nameBuf.Length < nameChars) nameBuf = _nameBuf = new char[Math.Max(nameChars, 260)];
            uint nameSize = (uint)nameBuf.Length;
            if (GetRawInputDeviceInfoNameW(hDevice, RIDI_DEVICENAME, nameBuf, ref nameSize) == uint.MaxValue)
                return;

            int len = Array.IndexOf(nameBuf, '\0');
            if (len < 0) len = (int)Math.Min(nameSize, nameBuf.Length);
            string instance = ToInstancePath(new string(nameBuf, 0, len));

            // 虚拟手柄过滤：HIDCLASS/HM_/HIDMAESTRO 形态只会是我们的虚拟设备（真实手柄路径必带厂商 VID）
            if (HidDeviceEnumerator.IsHidMaestroInstance(instance)) return;

            Touch();
        }
        catch { /* 单条事件解析失败不影响后续 */ }
    }

    /// <summary>RawInput 设备名（\\?\HID#VID_...#...#{GUID}）→ 设备实例路径（HID\VID_...&...\）形态。</summary>
    internal static string ToInstancePath(string rawInputName)
        => rawInputName.StartsWith(@"\\?\")
            ? rawInputName[4..].Replace('#', '\\')
            : rawInputName;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_rawThreadId != 0) PostThreadMessageW(_rawThreadId, WM_QUIT, nint.Zero, nint.Zero);
            _rawThread?.Join(1500);
        }
        catch { }
        // 窗口随线程退出销毁，注册随窗口销毁自动解除；此处兜底显式注销
        try
        {
            var rid = new RAWINPUTDEVICE[] { new() { usUsagePage = 0x01, usUsage = 0x04, dwFlags = RIDEV_REMOVE }, new() { usUsagePage = 0x01, usUsage = 0x05, dwFlags = RIDEV_REMOVE } };
            RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { }
    }

    // ---------------- Win32 ----------------

    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const int RIDEV_INPUTSINK = 0x00000100;
    private const int RIDEV_REMOVE = 0x00000001;
    private const nint HWND_MESSAGE = -3;
    private const uint WM_QUIT = 0x0012;

    private delegate nint WNDPROC(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public WNDPROC lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public int dwFlags;
        public nint hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint hRawInput, uint uiCommand, nint pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoW(nint hDevice, uint uiCommand, nint pData, ref uint pcbSize);

    /// <summary>设备名读取（同 GetRawInputDeviceInfoW 的 RIDI_DEVICENAME，char[] 形态重载）。</summary>
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoNameW(nint hDevice, uint uiCommand, char[] pData, ref uint pcbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
