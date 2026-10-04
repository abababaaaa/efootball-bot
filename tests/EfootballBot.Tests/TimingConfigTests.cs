using EfootballBot.Core.Config;
using Xunit;

namespace EfootballBot.Tests;

public class TimingConfigTests
{
    // ---------- TryParseSeconds：通过 ----------

    [Fact]
    public void TryParseSeconds_IntegerRange_Passes()
    {
        bool ok = TimingConfig.TryParseSeconds("1", "4", out int minMs, out int maxMs, out string error);

        Assert.True(ok);
        Assert.Equal(1000, minMs);
        Assert.Equal(4000, maxMs);
        Assert.Equal("", error);
    }

    [Fact]
    public void TryParseSeconds_DecimalRange_Passes()
    {
        bool ok = TimingConfig.TryParseSeconds("0.5", "3.5", out int minMs, out int maxMs, out _);

        Assert.True(ok);
        Assert.Equal(500, minMs);
        Assert.Equal(3500, maxMs);
    }

    [Fact]
    public void TryParseSeconds_BoundaryValues_Passes()
    {
        bool ok = TimingConfig.TryParseSeconds("0.2", "30", out int minMs, out int maxMs, out _);

        Assert.True(ok);
        Assert.Equal(200, minMs);
        Assert.Equal(30000, maxMs);
    }

    [Fact]
    public void TryParseSeconds_MinEqualsMax_Passes()
    {
        bool ok = TimingConfig.TryParseSeconds("2", "2", out int minMs, out int maxMs, out _);

        Assert.True(ok);
        Assert.Equal(2000, minMs);
        Assert.Equal(2000, maxMs);
    }

    // ---------- TryParseSeconds：报错 ----------

    [Theory]
    [InlineData("3", "2")]
    [InlineData("0.1", "1")]
    [InlineData("1", "30.1")]
    [InlineData("a", "2")]
    [InlineData("1", "b")]
    [InlineData("", "2")]
    [InlineData("1", "")]
    [InlineData("-1", "2")]
    public void TryParseSeconds_Invalid_ReturnsFalseAndError(string min, string max)
    {
        bool ok = TimingConfig.TryParseSeconds(min, max, out _, out _, out string error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    // ---------- MigrateIdleTapDefaults ----------

    [Fact]
    public void Migrate_LegacyDefaults_ResetsToNewDefaults()
    {
        var t = new TimingConfig { IdleTapMinMs = 1000, IdleTapMaxMs = 10000 };

        t.MigrateIdleTapDefaults();

        Assert.Equal(1000, t.IdleTapMinMs);
        Assert.Equal(4000, t.IdleTapMaxMs);
    }

    [Fact]
    public void Migrate_CustomValues_Unchanged()
    {
        var t = new TimingConfig { IdleTapMinMs = 2000, IdleTapMaxMs = 8000 };

        t.MigrateIdleTapDefaults();

        Assert.Equal(2000, t.IdleTapMinMs);
        Assert.Equal(8000, t.IdleTapMaxMs);
    }

    [Fact]
    public void Migrate_NewDefaults_Unchanged()
    {
        var t = new TimingConfig(); // 默认构造即 1000/4000

        t.MigrateIdleTapDefaults();

        Assert.Equal(1000, t.IdleTapMinMs);
        Assert.Equal(4000, t.IdleTapMaxMs);
    }

    [Fact]
    public void NewConfig_HasOneToFourSecondDefaults()
    {
        var t = new TimingConfig();

        Assert.Equal(1000, t.IdleTapMinMs);
        Assert.Equal(4000, t.IdleTapMaxMs);
    }
}
