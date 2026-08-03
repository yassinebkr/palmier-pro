using PalmierShell.Core;
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
