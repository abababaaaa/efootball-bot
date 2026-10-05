using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

public class HidHideTests
{
    // ---------- IOCTL 常量（对照官方 v1.5.230.0 Logic.h 手算值）----------

    [Theory]
    [InlineData(HidHideIoctl.IoctlGetWhitelist, 0x80016000u)]
    [InlineData(HidHideIoctl.IoctlSetWhitelist, 0x80016004u)]
    [InlineData(HidHideIoctl.IoctlGetBlacklist, 0x80016008u)]
    [InlineData(HidHideIoctl.IoctlSetBlacklist, 0x8001600Cu)]
    [InlineData(HidHideIoctl.IoctlGetActive, 0x80016010u)]
    [InlineData(HidHideIoctl.IoctlSetActive, 0x80016014u)]
    public void IoctlCodes_MatchOfficialContract(uint actual, uint expected)
        => Assert.Equal(expected, actual);

    // ---------- MultiString 序列化 ----------

    [Fact]
    public void ListToMultiString_SingleItem_ProducesDoubleNullTerminatedUtf16()
    {
        var bytes = HidHideIoctl.ListToMultiString(new[] { @"C:\A\b.exe" });
        // C:\A\b.exe = 10 chars + null = 22 bytes，再加最后一个 null = 24 bytes
        Assert.Equal(24, bytes.Length);
        Assert.Equal(0, bytes[^1]);
        Assert.Equal(0, bytes[^2]);
        Assert.Equal(0, bytes[^3]); // 字符串自身的 null 结尾
        Assert.Equal((byte)'C', bytes[0]);
    }

    [Fact]
    public void ListToMultiString_Empty_ProducesTwoUnicodeNulls()
    {
        var bytes = HidHideIoctl.ListToMultiString(new List<string>());
        Assert.Equal(4, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void MultiString_RoundTrip_PreservesItems()
    {
        var items = new List<string> { @"C:\Games\eFootball.exe", @"HID\VID_045E&PID_028E\6&2DF92C3&0&0000" };
        var back = HidHideIoctl.MultiStringToList(HidHideIoctl.ListToMultiString(items));
        Assert.Equal(items, back);
    }

    [Fact]
    public void MultiStringToList_EmptyOrShort_ReturnsEmptyList()
    {
        Assert.Empty(HidHideIoctl.MultiStringToList(Array.Empty<byte>()));
        Assert.Empty(HidHideIoctl.MultiStringToList(new byte[] { 0, 0, 0, 0 }));
    }
}
