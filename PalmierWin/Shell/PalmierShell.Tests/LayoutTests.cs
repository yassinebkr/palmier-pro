using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class LayoutTests {
    [Fact]
    public void Sanitise_KeepsAValidLayoutUnchanged() {
        var layout = new WorkspaceLayout(1600, 1000, 120, 60, false, 240, 300, 360, 260);
        Assert.Equal(layout, layout.Sanitised());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-4000, -4000)]
    [InlineData(double.NaN, double.NaN)]
    [InlineData(99999, 99999)]
    public void Sanitise_ForcesAUsableWindowSize(double width, double height) {
        var s = new WorkspaceLayout(width, height, 0, 0, false, 230, 280, 340, 240).Sanitised();
        Assert.InRange(s.WindowWidth, 960, 12000);
        Assert.InRange(s.WindowHeight, 600, 12000);
    }

    [Fact]
    public void Sanitise_ClampsPanelsToTheirSplitterBounds() {
        var s = new WorkspaceLayout(1440, 900, 0, 0, false, 5, 5000, double.NaN, 1).Sanitised();
        Assert.Equal(170, s.AgentWidth);
        Assert.Equal(520, s.MediaWidth);
        Assert.Equal(WorkspaceLayout.Default.InspectorWidth, s.InspectorWidth);
        Assert.Equal(140, s.TimelineHeight);
    }

    [Fact]
    public void Sanitise_KeepsAnUnsetWindowOriginUnset() {
        var s = WorkspaceLayout.Default.Sanitised();
        Assert.True(double.IsNaN(s.WindowX));
        Assert.True(double.IsNaN(s.WindowY));
    }
}
