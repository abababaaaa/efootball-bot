using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// slot 0 被真实手柄占用时虚拟手柄必然落到 slot 1+，eFootball 菜单只响应 Player 1，
/// 输入被忽略。连接前检测 slot 0 占用并提示，避免用户误挂机
/// （现仅用于禁用真实手柄前的等待判定；纯 HID 后端本身不占 XInput 槽位）。
/// </summary>
public sealed class SlotZeroBlockTests
{
    [Fact]
    public void Slot0Occupied_ReturnsBlockMessage()
    {
        var msg = PadBackendProbe.SlotZeroOccupiedMessage(true);
        Assert.NotNull(msg);
        Assert.Contains("slot 0", msg);
        Assert.Contains("拔下", msg);
    }

    [Fact]
    public void Slot0Free_ReturnsNull()
    {
        Assert.Null(PadBackendProbe.SlotZeroOccupiedMessage(false));
    }
}
