using EfootballBot.Core.System;
using Xunit;

namespace EfootballBot.Tests;

public class DriverInstallerArgumentsTests
{
    [Fact]
    public void BuildArguments_uses_silent_switches_with_proprietary_switch_first_and_quotes_log()
    {
        string args = DriverInstaller.BuildArguments(@"C:\Temp\a b\install.log");

        // Advanced Installer 规则：专有开关 /exenoui 必须位于 MSI 开关 /qn /norestart 之前
        Assert.Equal(@"/exenoui /qn /norestart /exelog ""C:\Temp\a b\install.log""", args);
    }
}
