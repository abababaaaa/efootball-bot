using EfootballBot.Core.UI;
using Xunit;

namespace EfootballBot.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void AllPalettesHaveIdenticalTokenKeys()
    {
        var problems = ThemePalette.Validate();
        Assert.Empty(problems);
    }

    [Fact]
    public void SemanticTokensExistInAllThemeWorldCombinations()
    {
        var required = new[] { "TextOnDanger", "ToggleThumbOn", "DialogBg", "LogTime", "LogTag" };
        foreach (var world in ThemeManager.Worlds)
        {
            foreach (var theme in ThemeManager.All)
            {
                var dict = ThemePalette.For(theme, world);
                foreach (var key in required)
                    Assert.True(dict.ContainsKey(key), $"{world}/{theme} missing token {key}");

                Assert.NotEqual(dict["ToggleOff"], dict["ToggleOn"]);
            }
        }
    }

    [Fact]
    public void GlassLightChipFillContrastsWithPrimaryText()
    {
        var glassLight = ThemePalette.For(ThemeManager.Light, ThemeManager.WorldGlass);
        Assert.NotEqual(glassLight["ChipFill"], glassLight["TextPrimary"]);
        Assert.NotEqual("#D91C1C1E", glassLight["ChipFill"]);
    }

    [Fact]
    public void GlassDarkPrimaryUsesAccentAndSubtleCardTopFill()
    {
        var glassDark = ThemePalette.For(ThemeManager.Dark, ThemeManager.WorldGlass);
        Assert.Equal(glassDark["Accent"], glassDark["PrimaryBtnFrom"]);
        Assert.NotEqual("#FFFFFFFF", glassDark["PrimaryBtnFrom"]);
        Assert.NotEqual("#3DFFFFFF", glassDark["GlassFillTop"]);
    }
}
