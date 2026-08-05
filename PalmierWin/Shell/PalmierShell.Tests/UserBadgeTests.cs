using Avalonia.Media;
using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// The badge letter and the accent-derived button shades are pure functions;
/// these pin the derivations, including the hover/pressed hexes AppTheme
/// declares as its initial values.
public sealed class UserBadgeTests {
    [Theory]
    [InlineData("yassin", "Y")]
    [InlineData("Alice", "A")]
    [InlineData("  bob  ", "B")]
    [InlineData("émilie", "É")]
    public void Initial_IsTheFirstLetterUppercased(string name, string expected) =>
        Assert.Equal(expected, UserBadge.Initial(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Initial_FallsBackUntilANameExists(string? name) =>
        Assert.Equal("?", UserBadge.Initial(name));

    [Fact]
    public void Shade_ZeroLeavesTheColourAlone() {
        var amber = Color.Parse(Accent.DefaultHex);
        Assert.Equal(amber, Accent.Shade(amber, 0));
    }

    [Fact]
    public void Shade_ReachesWhiteAndBlackAtTheEnds() {
        var teal = Color.Parse("#3AB7A8");
        Assert.Equal(Colors.White, Accent.Shade(teal, 1));
        Assert.Equal(Colors.Black, Accent.Shade(teal, -1));
    }

    [Fact]
    public void Shade_HoverLightensAndPressedDarkens() {
        foreach (var (_, hex) in Accent.Choices) {
            var color = Color.Parse(hex);
            var hover = Accent.Shade(color, 0.12);
            var pressed = Accent.Shade(color, -0.12);
            Assert.True(hover.R + hover.G + hover.B > color.R + color.G + color.B, $"{hex} hover");
            Assert.True(pressed.R + pressed.G + pressed.B < color.R + color.G + color.B, $"{hex} pressed");
        }
    }

    [Fact]
    public void Shade_MatchesTheThemeInitialHoverAndPressed() {
        // AppTheme's starting tokens assume the default amber; drift between
        // the two shows up as a hover jump on first run before any accent load.
        var amber = Color.Parse(Accent.DefaultHex);
        Assert.Equal(Color.Parse("#F4A54B"), Accent.Shade(amber, 0.12));
        Assert.Equal(Color.Parse("#D5872D"), Accent.Shade(amber, -0.12));
    }

    [Fact]
    public void ReadableText_IsDarkOnEveryPaletteAccent() {
        foreach (var (name, hex) in Accent.Choices)
            Assert.True(Accent.ReadableText(Color.Parse(hex)) == Accent.DarkText, name);
    }

    [Theory]
    [InlineData("#161616")]  // theme surface
    [InlineData("#000000")]
    [InlineData("#27415C")]
    public void ReadableText_IsLightOnDarkFills(string hex) =>
        Assert.Equal(Accent.LightText, Accent.ReadableText(Color.Parse(hex)));
}

/// The welcome dialog's name persists like every other setting, and settings
/// files written before the field existed load with no name (first-run flow).
[Collection("settings-file")]
public sealed class SettingsUserNameTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-settings-{Guid.NewGuid():N}.json");

    public SettingsUserNameTests() => SettingsStore.PathOverride = path;

    public void Dispose() {
        SettingsStore.PathOverride = null;
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public void UserName_RoundTrips() {
        SettingsStore.Save(AppSettings.Default with { UserName = "Yassin" });
        Assert.Equal("Yassin", SettingsStore.Load().UserName);
    }

    [Fact]
    public void UserName_MissingInOldFiles_LoadsEmpty() {
        File.WriteAllText(path, "{\"Provider\":\"anthropic\",\"Model\":\"m\",\"ApiKeyProtected\":\"\"}");
        Assert.Equal("", SettingsStore.Load().UserName);
    }
}
