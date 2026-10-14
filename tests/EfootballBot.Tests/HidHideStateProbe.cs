using EfootballBot.Core.Input;
using Xunit;
using Xunit.Abstractions;
namespace EfootballBot.Tests;
public class HidHideStateProbe
{
    private readonly ITestOutputHelper _out;
    public HidHideStateProbe(ITestOutputHelper o) => _out = o;
    [Fact]
    public void Probe()
    {
        var s = HidHideManager.QueryStatus();
        _out.WriteLine($"DriverInstalled={s.DriverInstalled} Active={s.Active} VigemHidden={s.VigemHidden} VigemCount={s.VigemInstanceCount} Err={s.Error}");
        _out.WriteLine("WHITELIST:");
        foreach (var w in s.Whitelist) _out.WriteLine("  " + w);
        _out.WriteLine("BLACKLIST:");
        foreach (var b in s.Blacklist) _out.WriteLine("  " + b);
    }
}
