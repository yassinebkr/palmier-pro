using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class ProbeParseTests {
    [Fact]
    public void FourFieldsParseWithoutALocation() {
        var probe = CoreApi.ParseProbe("1920,1080,3000,180");
        Assert.NotNull(probe);
        Assert.Equal(1920, probe.Value.Width);
        Assert.Equal(1080, probe.Value.Height);
        Assert.Equal(30.0, probe.Value.Fps);
        Assert.Equal(180, probe.Value.TotalFrames);
        Assert.Equal("", probe.Value.Location);
    }

    [Fact]
    public void TheLocationFieldRidesAlong() {
        var probe = CoreApi.ParseProbe("1920,1080,2997,540,+48.8566+002.3522/");
        Assert.NotNull(probe);
        Assert.Equal("+48.8566+002.3522/", probe.Value.Location);
    }

    [Fact]
    public void AnEmptyLocationFieldStaysEmpty() {
        var probe = CoreApi.ParseProbe("0,0,3000,900,");
        Assert.NotNull(probe);
        Assert.Equal("", probe.Value.Location);
    }

    [Theory]
    [InlineData("1920,1080,3000")]
    [InlineData("1920,1080,3000,180,loc,extra")]
    [InlineData("abc,1080,3000,180")]
    [InlineData("1920,1080,30.5,180")]
    [InlineData("")]
    public void MalformedLinesReturnNull(string text) {
        Assert.Null(CoreApi.ParseProbe(text));
    }
}
