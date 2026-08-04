using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The clip effect stack behind the Adjust section: upsert semantics at the
/// ABI, and proof through the compositor that a grade changes the pixels.
public class ClipEffectTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) =>
        TimelineState.Parse(CoreApi.GetTimelineJson(project));

    [Fact]
    public void UpsertAddsThenReplacesInPlaceAndEmptyRemoves() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;

            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id,
                "detail.clarity", """{"clarity":0.5,"dehaze":0.1}"""));
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id,
                "stylize.grain", """{"amount":0.4,"size":1.0}"""));

            var effects = State(project).FindClip(id)!.Effects!;
            Assert.Equal(["detail.clarity", "stylize.grain"], effects.Select(f => f.Type));
            Assert.Equal(0.5, effects[0].Number("clarity", 0));

            // Replacing keeps the stack position.
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id,
                "detail.clarity", """{"clarity":0.9}"""));
            effects = State(project).FindClip(id)!.Effects!;
            Assert.Equal("detail.clarity", effects[0].Type);
            Assert.Equal(0.9, effects[0].Number("clarity", 0));
            Assert.Equal(0, effects[0].Number("dehaze", 0));   // wholesale replace

            // Empty params remove; removing again refuses rather than lying.
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id, "detail.clarity", "{}"));
            Assert.Equal(0, CoreApi.palmier_clip_set_effect(project, id, "detail.clarity", "{}"));
            Assert.Single(State(project).FindClip(id)!.Effects!);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"amount":"NaN-ish","nested":{"x":1}}""")]
    [InlineData("""{"amount":true}""")]
    public void MalformedParamsAreRefusedWholesale(string json) {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            Assert.Equal(0, CoreApi.palmier_clip_set_effect(project, id, "stylize.grain", json));
            Assert.Null(State(project).FindClip(id)!.Effects);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// End to end: an aggressive levels grade must change what the compositor
    /// produces. Captured through the same path the preview and stills use.
    [Fact]
    public void AGradeChangesTheCompositedPixels() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("colorbands24.mp4"), 60)!;
            byte[] before = Capture(project, 10);
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id,
                "color.blacksWhites", """{"blacks":0.9,"whites":-0.9}"""));
            byte[] after = Capture(project, 10);
            Assert.False(before.AsSpan().SequenceEqual(after), "grade did not reach the pixels");
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Frame 10 sits in the red band; invert turns it cyan, so every channel
    /// mean must land on 255 minus the unmodified mean.
    [Fact]
    public void InvertInvertsTheCompositedPixels() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("colorbands24.mp4"), 60)!;
            var before = ChannelMeans(Capture(project, 10));
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id,
                "stylize.invert", """{"amount":1}"""));
            var after = ChannelMeans(Capture(project, 10));
            Assert.True(Math.Abs(after.R - (255 - before.R)) < 8, $"R: {before.R} -> {after.R}");
            Assert.True(Math.Abs(after.G - (255 - before.G)) < 8, $"G: {before.G} -> {after.G}");
            Assert.True(Math.Abs(after.B - (255 - before.B)) < 8, $"B: {before.B} -> {after.B}");
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Zeroing the red gain on the red band kills that channel and leaves the
    /// other two alone.
    [Fact]
    public void WheelsGainReachesTheCompositedPixels() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("colorbands24.mp4"), 60)!;
            var before = ChannelMeans(Capture(project, 10));
            Assert.True(before.R > 150, $"fixture: red band mean R was {before.R}");
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id,
                "color.wheels", """{"gain.r":0}"""));
            var after = ChannelMeans(Capture(project, 10));
            Assert.True(after.R < 30, $"gain.r=0 left R at {after.R}");
            Assert.True(Math.Abs(after.G - before.G) < 10, $"G moved: {before.G} -> {after.G}");
            Assert.True(Math.Abs(after.B - before.B) < 10, $"B moved: {before.B} -> {after.B}");
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// The shell's wheel mapping end to end: a full cyan drag on Gain halves
    /// the red channel, so the red band must visibly dim.
    [Fact]
    public void WheelDragParamsReachTheCompositedPixels() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("colorbands24.mp4"), 60)!;
            var before = ChannelMeans(Capture(project, 10));
            Assert.True(before.R > 150, $"fixture: red band mean R was {before.R}");
            var (r, g, b) = ColorWheelMath.ToParams(ColorWheelMath.WheelKind.Gain, -1, 0, 0);
            string json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> {
                ["gain.r"] = r, ["gain.g"] = g, ["gain.b"] = b });
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id, "color.wheels", json));
            var after = ChannelMeans(Capture(project, 10));
            Assert.True(after.R < before.R * 0.75, $"cyan drag left R at {after.R} (was {before.R})");
            Assert.True(Math.Abs(after.G - before.G * 1.25) < 12,
                $"G off its 1.25 gain: {before.G} -> {after.G}");
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Keying the red band's colour makes those pixels transparent. Effects
    /// run on the composited frame (per-layer keying comes with per-layer
    /// effects), so the key shows in the readback's alpha, not the RGB.
    [Fact]
    public void ChromaKeyCutsTheKeyedColour() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("colorbands24.mp4"), 60)!;
            var before = ChannelMeans(Capture(project, 10));
            Assert.True(before.R > 150 && before.A > 200,
                $"fixture: red band mean was R {before.R} A {before.A}");
            Assert.Equal(1, CoreApi.palmier_clip_set_effect(project, id,
                "key.chroma", """{"keyColor.r":1,"keyColor.g":0,"keyColor.b":0,"threshold":0.4,"spill":0.5}"""));
            var after = ChannelMeans(Capture(project, 10));
            Assert.True(after.A < 40, $"keyed red still reads back opaque (A {after.A})");
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    static (double B, double G, double R, double A) ChannelMeans(byte[] bgra) {
        long b = 0, g = 0, r = 0, a = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4) {
            b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; a += bgra[i + 3];
        }
        double n = bgra.Length / 4.0;
        return (b / n, g / n, r / n, a / n);
    }

    [Fact]
    public void RemovingAKeyframeDropsItAndAnEmptiedTrackDisappears() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_add_keyframe(project, id, "opacity", 10, 0.2, 0));
            Assert.Equal(1, CoreApi.palmier_clip_add_keyframe(project, id, "opacity", 40, 0.9, 0));

            Assert.Equal(1, CoreApi.palmier_clip_remove_keyframe(project, id, "opacity", 10));
            var frames = State(project).FindClip(id)!.KeyframeFrames.ToList();
            Assert.Equal([40], frames);

            // Removing where nothing is keyed refuses; emptying drops the track.
            Assert.Equal(0, CoreApi.palmier_clip_remove_keyframe(project, id, "opacity", 10));
            Assert.Equal(1, CoreApi.palmier_clip_remove_keyframe(project, id, "opacity", 40));
            Assert.False(State(project).FindClip(id)!.HasKeyframes);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    static byte[] Capture(IntPtr project, int frame) {
        int needed = -CoreApi.palmier_project_capture_frame(project, frame, [], 0, out _, out _);
        Assert.True(needed > 0);
        var pixels = new byte[needed];
        Assert.Equal(1, CoreApi.palmier_project_capture_frame(
            project, frame, pixels, pixels.Length, out _, out _));
        return pixels;
    }
}

public class EffectParamClampTests {
    [Fact]
    public void OutOfRangeWhitesClampToTheRegistryRange() {
        var values = new Dictionary<string, object> { ["whites"] = 4.0, ["blacks"] = -3.0 };
        InspectorViewModel.ClampEffectParams("color.blacksWhites", values);
        Assert.Equal(1.0, values["whites"]);
        Assert.Equal(-1.0, values["blacks"]);
    }

    [Fact]
    public void InRangeAndUnknownParamsPassThrough() {
        var values = new Dictionary<string, object> { ["whites"] = 0.4, ["path"] = "luts/x.cube" };
        InspectorViewModel.ClampEffectParams("color.blacksWhites", values);
        Assert.Equal(0.4, values["whites"]);
        Assert.Equal("luts/x.cube", values["path"]);
    }
}
