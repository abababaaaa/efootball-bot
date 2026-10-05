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

    // ---------- 实例路径纯函数 ----------

    [Theory]
    [InlineData(@"HID\VID_045E&PID_028E\6&2DF92C3&0&0000", true)]
    [InlineData(@"hid\vid_045e&pid_028e&mi_00\7&2df92c3&0&0000", true)] // 大小写不敏感
    [InlineData(@"HID\VID_045E&PID_02EA\6&2DF92C3&0&0000", false)]      // 真实微软手柄 HID 接口
    [InlineData(@"HID\VID_1532&PID_0900\6&abcd", false)]                // Razer 等第三方
    [InlineData(@"USB\VID_045E&PID_028E\6&2DF92C3", false)]             // USB 枚举器非 HID 前缀
    public void IsViGEmInstance_MatchesByPrefix(string path, bool expected)
        => Assert.Equal(expected, HidDeviceEnumerator.IsViGEmInstance(path));

    [Fact]
    public void InterfacePathToInstancePath_ConvertsStandardPath()
    {
        var ifPath = @"\\?\hid#vid_045e&pid_028e&mi_00#7&2df92c3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(@"hid\vid_045e&pid_028e&mi_00\7&2df92c3&0&0000",
            HidDeviceEnumerator.InterfacePathToInstancePath(ifPath));
    }

    [Fact]
    public void InterfacePathToInstancePath_DecodesPercentEscapes()
    {
        var ifPath = @"\\?\hid#vid_045e&pid_028e#7%262df92c3%260%260000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(@"hid\vid_045e&pid_028e\7&2df92c3&0&0000",
            HidDeviceEnumerator.InterfacePathToInstancePath(ifPath));
    }

    // ---------- 黑/白名单合并纯函数 ----------

    [Fact]
    public void MergeInto_AddsNewItems_CaseInsensitive()
    {
        var existing = new List<string> { @"C:\Old\app.exe" };
        var merged = HidHideManager.MergeInto(existing, new[] { @"c:\new\efootball.exe" });
        Assert.Equal(2, merged.Count);
        Assert.Contains(@"C:\Old\app.exe", merged);
        // 不区分大小写去重：再次合并同路径不同大小写不产生重复
        var again = HidHideManager.MergeInto(merged, new[] { @"C:\NEW\EFOOTBALL.EXE" });
        Assert.Equal(2, again.Count);
    }

    [Fact]
    public void RemoveAll_RemovesCaseInsensitive_KeepsOthers()
    {
        var existing = new List<string>
        {
            @"HID\VID_045E&PID_028E\6&2DF92C3&0&0000",
            @"HID\VID_1532&PID_0900\6&abcd", // 用户已有条目，必须保留
            @"C:\Games\eFootball.exe",
        };
        var result = HidHideManager.RemoveAll(existing, new[]
        {
            @"hid\vid_045e&pid_028e\6&2df92c3&0&0000", // 大小写不同也要删掉
            @"C:\Games\eFootball.exe",
        });
        Assert.Single(result);
        Assert.Equal(@"HID\VID_1532&PID_0900\6&abcd", result[0]);
    }

    [Fact]
    public void IsolateVigemPrefixEntries_Splits()
    {
        var bl = new List<string>
        {
            @"HID\VID_045E&PID_028E\6&2DF92C3&0&0000",
            @"HID\VID_045E&PID_028E\9&OLDINSTANCE",
            @"HID\VID_1532&PID_0900\6&abcd",
        };
        var (keep, vigemEntries) = HidHideManager.SplitByVigemPrefix(bl);
        Assert.Single(keep);
        Assert.Equal(2, vigemEntries.Count);
    }
}
