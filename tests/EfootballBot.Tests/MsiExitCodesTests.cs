using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

public class MsiExitCodesTests
{
    [Theory]
    [InlineData(0, DriverSetupResultKind.Installed)]
    [InlineData(3010, DriverSetupResultKind.RebootRequired)]
    [InlineData(1638, DriverSetupResultKind.AlreadyPresent)]
    [InlineData(1602, DriverSetupResultKind.UserCancelled)]
    [InlineData(1603, DriverSetupResultKind.Failed)]
    [InlineData(-1, DriverSetupResultKind.Failed)]
    public void Map_maps_known_exit_codes(int code, DriverSetupResultKind expected)
    {
        Assert.Equal(expected, MsiExitCodes.Map(code));
    }
}
