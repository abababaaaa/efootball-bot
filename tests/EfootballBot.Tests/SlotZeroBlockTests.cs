using EfootballBot.Core.Input;
using Xunit;

namespace EfootballBot.Tests;

/// <summary>
/// slot 0 被真实手柄占用时虚拟手柄必然落到 slot 1+，eFootball 菜单只响应 Player 1，
/// 输入被忽略。连接前检测 slot 0 占用并阻断启动，避免用户误挂机。
/// </summary>
public sealed class SlotZeroBlockTests
{
    [Fact]
    public void Slot0Occupied_ReturnsBlockMessage()
    {
        var msg = VirtualGamepad360.SlotZeroOccupiedMessage(true);
        Assert.NotNull(msg);
        Assert.Contains("slot 0", msg);
        Assert.Contains("拔下", msg);
    }

    [Fact]
    public void Slot0Free_ReturnsNull()
    {
        Assert.Null(VirtualGamepad360.SlotZeroOccupiedMessage(false));
    }
}
