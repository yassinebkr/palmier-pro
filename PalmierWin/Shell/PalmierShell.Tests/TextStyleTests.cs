using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// Text clip styling behind the inspector's TEXT section: the ABI patches
/// fontSize/color/alignment on the clip's TextStyle, and the compositor's
/// captured pixels prove the style reached the rasterizer.
public class TextStyleTests {
    static TimelineState State(IntPtr project) =>
        TimelineState.Parse(CoreApi.GetTimelineJson(project));

    [Fact]
    public void StylePatchRoundTripsThroughTheTimelineState() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddTextClip(project, "HELLO", 0, 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_set_text_style(project, id,
                """{"fontSize":120,"color":"#FF0000","alignment":"left"}"""));
            var style = State(project).FindClip(id)!.TextStyle;
            Assert.NotNull(style);
            Assert.Equal(120, style!.FontSize);
            Assert.Equal("left", style.Alignment);
            Assert.Equal(1, style.Color!.R, 3);
            Assert.Equal(0, style.Color.G, 3);

            // A patch touches only its own keys.
            Assert.Equal(1, CoreApi.palmier_clip_set_text_style(project, id, """{"alignment":"right"}"""));
            style = State(project).FindClip(id)!.TextStyle!;
            Assert.Equal("right", style.Alignment);
            Assert.Equal(120, style.FontSize);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Theory]
    [InlineData("""{"alignment":"middle"}""")]
    [InlineData("""{"color":"red"}""")]
    [InlineData("""{"fontSize":-12}""")]
    [InlineData("""{"kerning":2}""")]
    [InlineData("not json")]
    public void MalformedStylePatchesAreRefusedWholesale(string json) {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddTextClip(project, "HELLO", 0, 60)!;
            Assert.Equal(0, CoreApi.palmier_clip_set_text_style(project, id, json));
            var style = State(project).FindClip(id)!.TextStyle;
            Assert.True(style is null || (style.FontSize == 96 && style.Alignment == "center"));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Default white glyphs turn red: over the solid interior pixels the green
    /// and blue channels must collapse while red stays lit.
    [Fact]
    public void TextColorReachesTheCompositedPixels() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddTextClip(project, "WWWWWWWW", 0, 60)!;
            int width = CaptureSize(project).w;
            var before = GlyphMeans(Capture(project, 10), width);
            Assert.True(before.count > 500, $"fixture: only {before.count} glyph pixels");
            Assert.True(before.g > 200, $"fixture: default text not white (G {before.g})");

            Assert.Equal(1, CoreApi.palmier_clip_set_text_style(project, id, """{"color":"#FF0000"}"""));
            var after = GlyphMeans(Capture(project, 10), width);
            Assert.True(after.r > 200 && after.g < 60 && after.b < 60,
                $"recoloured text reads back R {after.r} G {after.g} B {after.b}");
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Left alignment shifts the glyph mass off centre; halving the font size
    /// quarters the glyph area.
    [Fact]
    public void AlignmentAndSizeReachTheCompositedPixels() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddTextClip(project, "WWWWWWWW", 0, 60)!;
            int width = CaptureSize(project).w;
            var centered = GlyphMeans(Capture(project, 10), width);

            Assert.Equal(1, CoreApi.palmier_clip_set_text_style(project, id, """{"alignment":"left"}"""));
            var left = GlyphMeans(Capture(project, 10), width);
            Assert.True(left.meanX < width * 0.35,
                $"left-aligned text centroid at {left.meanX} of {width}");
            Assert.True(centered.meanX > width * 0.4 && centered.meanX < width * 0.6,
                $"centred text centroid at {centered.meanX} of {width}");

            Assert.Equal(1, CoreApi.palmier_clip_set_text_style(project, id,
                """{"fontSize":48,"alignment":"center"}"""));
            var small = GlyphMeans(Capture(project, 10), width);
            Assert.True(small.count < centered.count * 0.5,
                $"halving the font size left {small.count} of {centered.count} glyph pixels");
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    static (int w, int h) CaptureSize(IntPtr project) {
        int needed = -CoreApi.palmier_project_capture_frame(project, 0, [], 0, out int w, out int h);
        Assert.True(needed > 0);
        return (w, h);
    }

    static byte[] Capture(IntPtr project, int frame) {
        int needed = -CoreApi.palmier_project_capture_frame(project, frame, [], 0, out _, out _);
        Assert.True(needed > 0);
        var pixels = new byte[needed];
        Assert.Equal(1, CoreApi.palmier_project_capture_frame(
            project, frame, pixels, pixels.Length, out _, out _));
        return pixels;
    }

    /// Channel means and horizontal centroid over the solid glyph interior
    /// (alpha > 200), so antialiased edges can't blur the colour assertions.
    static (int count, double r, double g, double b, double meanX) GlyphMeans(byte[] bgra, int width) {
        long r = 0, g = 0, b = 0, x = 0;
        int count = 0;
        int total = bgra.Length / 4;
        for (int i = 0; i < total; i++) {
            if (bgra[i * 4 + 3] <= 200) continue;
            b += bgra[i * 4]; g += bgra[i * 4 + 1]; r += bgra[i * 4 + 2];
            x += i % width;
            count++;
        }
        if (count == 0) return (0, 0, 0, 0, 0);
        return (count, (double)r / count, (double)g / count, (double)b / count, (double)x / count);
    }
}
