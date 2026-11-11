using System.IO.MemoryMappedFiles;
using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

// 进程内代理后端：共享内存 ABI 偏移必须与 tools/xinput-proxy/pad_shared.h 一致，
// 否则代理读到的是一堆错位数据（游戏里表现为"摇杆乱跳/按键错位"这种最难查的故障）。
public class PadProxyTests
{
    private const int OffFlags = 32;
    private const int OffButtons = 40;
    private const int OffLx = 44;
    private const int OffSeq = 12;
    private const int OffProxyCalls = 64;

    private static string UniqueName() => @"Local\EfootballBot.Tests." + Guid.NewGuid().ToString("N");

    private static T ReadRaw<T>(string name, int offset, Func<MemoryMappedViewAccessor, int, T> read)
    {
        using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        using var view = mmf.CreateViewAccessor(0, SharedPadWriter.StructSize);
        return read(view, offset);
    }

    [Fact]
    public void SharedPadWriter_PublishesStateAtDocumentedOffsets()
    {
        var name = UniqueName();
        using var w = new SharedPadWriter(name);

        w.SetButton(0x1001);
        w.SetLeftStick(1234, -1234);

        Assert.Equal((ushort)0x1001, ReadRaw(name, OffButtons, (v, o) => v.ReadUInt16(o)));
        Assert.Equal((short)1234, ReadRaw(name, OffLx, (v, o) => v.ReadInt16(o)));
        // seqlock 静止时必为偶数（奇数=写入中）
        Assert.Equal(0, ReadRaw(name, OffSeq, (v, o) => v.ReadInt64(o)) & 1L);
        // 头部自描述：magic/version/structSize
        Assert.Equal(0x44415045u, ReadRaw(name, 0, (v, o) => v.ReadUInt32(o)));
        Assert.Equal(1u, ReadRaw(name, 4, (v, o) => v.ReadUInt32(o)));
        Assert.Equal((uint)SharedPadWriter.StructSize, ReadRaw(name, 8, (v, o) => v.ReadUInt32(o)));
    }

    [Fact]
    public void ProxyPad_ConnectSetsActive_DisconnectPanicsThenClears()
    {
        var name = UniqueName();
        using var pad = new ProxyPad(SharedPadWriter.TargetGlobal, name);

        pad.Connect();
        Assert.True(pad.IsConnected);
        Assert.Equal(SharedPadWriter.FlagActive, ReadRaw(name, OffFlags, (v, o) => v.ReadUInt32(o)));
        Assert.Equal(0, pad.UserIndex);
        Assert.Equal(PadBackendKind.XInputProxy, pad.Kind);

        pad.SetButton(PadButton.A, true);
        Assert.Equal((ushort)0x1000, ReadRaw(name, OffButtons, (v, o) => v.ReadUInt16(o)));
        pad.SetButton(PadButton.A, false);
        Assert.Equal((ushort)0, ReadRaw(name, OffButtons, (v, o) => v.ReadUInt16(o)));

        pad.Disconnect();
        uint flags = ReadRaw(name, OffFlags, (v, o) => v.ReadUInt32(o));
        Assert.Equal(0u, flags & SharedPadWriter.FlagActive);      // 撤接管
        Assert.NotEqual(0u, flags & SharedPadWriter.FlagPanic);    // 先全松手
        Assert.False(pad.IsConnected);
    }

    [Fact]
    public void ProxyPad_DpadIsExclusive()
    {
        var name = UniqueName();
        using var pad = new ProxyPad(SharedPadWriter.TargetGlobal, name);
        pad.Connect();

        pad.SetDpad(PadDir.Left);
        Assert.Equal((ushort)0x0004, ReadRaw(name, OffButtons, (v, o) => v.ReadUInt16(o)));

        pad.SetDpad(PadDir.Up);
        Assert.Equal((ushort)0x0001, ReadRaw(name, OffButtons, (v, o) => v.ReadUInt16(o)));  // 旧方向已清

        pad.SetDpad(null);
        Assert.Equal((ushort)0, ReadRaw(name, OffButtons, (v, o) => v.ReadUInt16(o)));
    }

    [Fact]
    public void SharedPadWriter_ProxySeenDefaultsFalse()
    {
        var name = UniqueName();
        using var w = new SharedPadWriter(name);
        Assert.False(w.ProxySeen());
        Assert.Equal(0, ReadRaw(name, OffProxyCalls, (v, o) => v.ReadInt32(o)));
    }

    [Theory]
    [InlineData(true, true, true, PadNodeKind.VirtualLive)]     // 虚拟 + 运行中
    [InlineData(true, false, true, PadNodeKind.VirtualStale)]   // 虚拟 + 残留
    [InlineData(false, true, true, PadNodeKind.Physical)]       // 真实 + 运行中
    [InlineData(false, true, false, PadNodeKind.OtherVirtual)]  // SWD/ROOT 虚拟手柄
    [InlineData(false, false, true, PadNodeKind.Inactive)]      // 被禁用的真实手柄
    public void Classify_MatchesRequirementGate(bool isVirtual, bool started, bool realBus, PadNodeKind expected)
        => Assert.Equal(expected, PadNodeHygiene.Classify(isVirtual, started, realBus));

    // 进程内代理是「测试内容」：只有测试版构建（-p:ChildSessionBeta=true）才允许选中，
    // 正式版即使配置里写着 XInputProxy 也必须归一化回 ViGEm。
    [Fact]
    public void ProxyBackend_IsTestOnly_GatedByBuildFlag()
    {
        if (PadBackendInfo.ProxyBackendEnabled)
        {
            Assert.Contains(PadBackendKind.XInputProxy, PadBackendInfo.UiOrder);
            Assert.Equal(PadBackendKind.XInputProxy, PadBackendInfo.Normalize(PadBackendKind.XInputProxy));
            Assert.Equal([PadBackendKind.XInputProxy, PadBackendKind.Vigem],
                PadBackendInfo.FallbackChain(PadBackendKind.XInputProxy));
        }
        else
        {
            Assert.DoesNotContain(PadBackendKind.XInputProxy, PadBackendInfo.UiOrder);
            Assert.Equal([PadBackendKind.Vigem], PadBackendInfo.UiOrder);
            Assert.Equal(PadBackendKind.Vigem, PadBackendInfo.Normalize(PadBackendKind.XInputProxy));
            Assert.Equal([PadBackendKind.Vigem], PadBackendInfo.FallbackChain(PadBackendKind.XInputProxy));
        }
        // 无论哪个构建，Vigem 都必须始终可选
        Assert.Contains(PadBackendKind.Vigem, PadBackendInfo.UiOrder);
        Assert.Equal(PadBackendKind.Vigem, PadBackendInfo.Normalize(PadBackendKind.Vigem));
    }

    [Fact]
    public void Gate_BlocksDeviceSurgeryWhenPhysicalPadInUse()
    {
        Assert.False(PadNodeHygiene.CanTouchDevices(physicalPadCount: 1));
        Assert.False(PadNodeHygiene.CanTouchDevices(physicalPadCount: 0, slot0Occupied: true)); // 蓝牙手柄 PnP 查不到时兜底
        Assert.True(PadNodeHygiene.CanTouchDevices(physicalPadCount: 0));
        Assert.True(PadNodeHygiene.CanTouchDevices(physicalPadCount: 2, force: true)); // 仅人工排障
    }

    [Fact]
    public void PnputilArguments_AreExact()
    {
        Assert.Equal(new[] { "/remove-device", @"USB\VID_045E&PID_028E\01" },
            PadNodeHygiene.BuildRemoveArguments(@"USB\VID_045E&PID_028E\01"));
        Assert.Equal(new[] { "/scan-devices" }, PadNodeHygiene.BuildScanArguments());
        Assert.Equal(new[] { "/restart-device", @"ROOT\SYSTEM\0001" },
            PadNodeHygiene.BuildRestartArguments(@"ROOT\SYSTEM\0001"));
    }

    [Theory]
    [InlineData("HID-compliant game controller", true)]
    [InlineData("Xbox 360 Controller for Windows", true)]
    [InlineData("XBOX Synthetic Gamepad Bus", true)]
    [InlineData("USB Input Device", false)]
    [InlineData("HID-compliant mouse", false)]
    public void LooksLikePad_RecognisesControllers(string name, bool expected)
        => Assert.Equal(expected, PadNodeHygiene.LooksLikePad(name));

    [Theory]
    [InlineData(@"USB\VID_045E&PID_028E\01", true)]
    [InlineData(@"HID\VID_045E&PID_028E&IG_00\3&34F4D81E&0&0000", true)]
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002045E\7&1", true)]
    [InlineData(@"SWD\SYNTHETICGAMEPADENUM\SYNTHETICGAMEPAD0", false)]
    [InlineData(@"ROOT\SYSTEM\0001", false)]
    [InlineData(@"VHF\HID_DEVICE_SYSTEM_VHF\1&1A590E2C&0&{BA465EFF}", false)]
    public void IsRealBusInstance_ExcludesSoftwareEnumerators(string id, bool expected)
        => Assert.Equal(expected, PadNodeHygiene.IsRealBusInstance(id));
}
