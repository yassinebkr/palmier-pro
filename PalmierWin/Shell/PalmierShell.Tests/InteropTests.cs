using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class InteropTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void AddClip_AppearsInJsonSnapshot() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string? clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            Assert.NotNull(clipId);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var clip = Assert.Single(state.Tracks.SelectMany(t => t.Clips));
            Assert.Equal(clipId, clip.Id);
            Assert.Equal(60, clip.DurationFrames);
            Assert.Equal(60, state.TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AddClips_PlaceSequentiallyOnVideoTrack() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var video = state.Tracks.Single(t => t.Type == "video");
            Assert.Equal(2, video.Clips.Count);
            Assert.Equal(0, video.Clips[0].StartFrame);
            Assert.Equal(60, video.Clips[1].StartFrame);
            Assert.Equal(90, state.TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RemoveClip_RemovesAndReportsNoOpForUnknownId() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_timeline_remove_clip(project, clipId));
            Assert.Equal(0, CoreApi.palmier_timeline_remove_clip(project, clipId));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Empty(state.Tracks.SelectMany(t => t.Clips));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SetClipProperties_ReflectInSnapshot() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_set_transform(project, clipId, 0.25, 0.25, 0.5, 0.5, 45));
            Assert.Equal(1, CoreApi.palmier_clip_set_speed(project, clipId, 2.0));
            Assert.Equal(1, CoreApi.palmier_clip_set_volume_db(project, clipId, -6.0));
            Assert.Equal(1, CoreApi.palmier_clip_set_opacity(project, clipId, 0.8));

            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(clipId)!;
            Assert.Equal(0.25, clip.Transform.CenterX);
            Assert.Equal(0.5, clip.Transform.Width);
            Assert.Equal(45, clip.Transform.Rotation);
            Assert.Equal(2.0, clip.Speed);
            Assert.Equal(0.8, clip.Opacity);
            Assert.Equal(Math.Pow(10, -6.0 / 20.0), clip.Volume, 6);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(101.0, 1.0)]
    public void SetSpeed_RejectsInvalidValues(double speed, double expected) {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(0, CoreApi.palmier_clip_set_speed(project, clipId, speed));
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(clipId)!;
            Assert.Equal(expected, clip.Speed);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void ProbeMedia_ReturnsRealDimensionsAndDuration() {
        var probe = CoreApi.ProbeMedia(TestMediaPath("testsrc.mp4"));
        Assert.NotNull(probe);
        Assert.True(probe.Value.Width > 0);
        Assert.True(probe.Value.Height > 0);
        Assert.True(probe.Value.Fps > 0);
        Assert.True(probe.Value.TotalFrames > 0);
    }

    [Fact]
    public void SplitClip_ProducesTwoContiguousClipsPlayingSameSource() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            string? rightId = CoreApi.SplitClip(project, clipId, 20);
            Assert.NotNull(rightId);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var clips = state.Tracks.Single(t => t.Type == "video").Clips;
            Assert.Equal(2, clips.Count);
            Assert.Equal((0, 20), (clips[0].StartFrame, clips[0].DurationFrames));
            Assert.Equal((20, 40), (clips[1].StartFrame, clips[1].DurationFrames));
            Assert.Equal(clipId, clips[0].Id);
            Assert.Equal(rightId, clips[1].Id);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SplitClip_RejectsFrameOutsideClip() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Null(CoreApi.SplitClip(project, clipId, 0));
            Assert.Null(CoreApi.SplitClip(project, clipId, 60));
            Assert.Null(CoreApi.SplitClip(project, clipId, -5));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void UndoStack_RestoresExactStateAcrossUndoRedo() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var undo = new UndoStack(
                () => CoreApi.GetTimelineJson(project),
                json => CoreApi.palmier_timeline_load_json(project, json) == 1);

            Assert.True(undo.Execute("Add Clip", () => CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60) is not null));
            string afterAdd = CoreApi.GetTimelineJson(project);
            Assert.True(undo.CanUndo);

            undo.Undo();
            Assert.Empty(TimelineState.Parse(CoreApi.GetTimelineJson(project)).Tracks.SelectMany(t => t.Clips));
            Assert.True(undo.CanRedo);

            undo.Redo();
            // Key order in re-encoded JSON is nondeterministic; compare state.
            var expected = TimelineState.Parse(afterAdd);
            var actual = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(
                expected.Tracks.SelectMany(t => t.Clips).Select(c => (c.Id, c.StartFrame, c.DurationFrames)),
                actual.Tracks.SelectMany(t => t.Clips).Select(c => (c.Id, c.StartFrame, c.DurationFrames)));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void UndoStack_FailedIntentCreatesNoEntry() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var undo = new UndoStack(
                () => CoreApi.GetTimelineJson(project),
                json => CoreApi.palmier_timeline_load_json(project, json) == 1);
            Assert.False(undo.Execute("Remove Clip", () => CoreApi.palmier_timeline_remove_clip(project, "missing") == 1));
            Assert.False(undo.CanUndo);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AddClip_WithAudioSource_AddsLinkedAudioClip() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var video = Assert.Single(state.Tracks.Single(t => t.Type == "video").Clips);
            var audioClip = Assert.Single(state.Tracks.Single(t => t.Type == "audio").Clips);
            Assert.Equal("audio", audioClip.MediaType);
            Assert.Equal(video.StartFrame, audioClip.StartFrame);
            Assert.Equal(video.DurationFrames, audioClip.DurationFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AddClip_WithoutAudioSource_AddsNoAudioClip() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Empty(state.Tracks.Single(t => t.Type == "audio").Clips);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Waveform_ReturnsNonSilentMinMaxPairs() {
        var waveform = CoreApi.GetWaveform(TestMediaPath("testav.mp4"), 64);
        Assert.NotNull(waveform);
        Assert.Equal(128, waveform!.Length);
        // 440 Hz sine (ffmpeg default ~1/8 amplitude): peaks must be well
        // above silence and mins mirrored below.
        Assert.Contains(waveform, v => v > 0.05f);
        Assert.Contains(waveform, v => v < -0.05f);
        for (int col = 0; col < 64; col++)
            Assert.True(waveform[col * 2] <= waveform[col * 2 + 1]);
    }

    [Fact]
    public void Waveform_FailsForVideoOnlyFile() {
        Assert.Null(CoreApi.GetWaveform(TestMediaPath("testsrc.mp4"), 32));
    }

    [Fact]
    public void TrackMuteAndHidden_IntentsReflectInSnapshot() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var video = state.Tracks.Single(t => t.Type == "video");
            var audioTrack = state.Tracks.Single(t => t.Type == "audio");

            Assert.Equal(1, CoreApi.palmier_track_set_hidden(project, video.Id, 1));
            Assert.Equal(1, CoreApi.palmier_track_set_muted(project, audioTrack.Id, 1));
            Assert.Equal(0, CoreApi.palmier_track_set_muted(project, "missing", 1));

            var after = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.True(after.Tracks.Single(t => t.Type == "video").Hidden);
            Assert.True(after.Tracks.Single(t => t.Type == "audio").Muted);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AudioEngine_CreatePlaySeekDestroy() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            IntPtr audio = CoreApi.palmier_audio_create(project);
            if (audio == IntPtr.Zero) return;  // no output device (CI): silent no-op by contract
            try {
                Assert.Equal(1, CoreApi.palmier_audio_set_playing(audio, 1, 0));
                Thread.Sleep(300);  // let the callback mix a few buffers
                Assert.Equal(1, CoreApi.palmier_audio_seek(audio, 30));
                Assert.Equal(1, CoreApi.palmier_audio_set_playing(audio, 0, 30));
                Assert.Equal(1, CoreApi.palmier_audio_sync(audio));
            } finally {
                CoreApi.palmier_audio_destroy(audio);
            }
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AddClipAt_PlacesClipAndLinkedAudioAtFrame() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.NotNull(CoreApi.AddClipAt(project, TestMediaPath("testav.mp4"), 60, 120));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(120, state.Tracks.Single(t => t.Type == "video").Clips.Single().StartFrame);
            Assert.Equal(120, state.Tracks.Single(t => t.Type == "audio").Clips.Single().StartFrame);
            Assert.Null(CoreApi.AddClipAt(project, TestMediaPath("testav.mp4"), 60, -5));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    static void AssertNoOverlap(TimelineState state) {
        foreach (var track in state.Tracks) {
            var ordered = track.Clips.OrderBy(c => c.StartFrame).ToList();
            for (int i = 1; i < ordered.Count; i++)
                Assert.True(ordered[i].StartFrame >= ordered[i - 1].EndFrame,
                    $"clips overlap on {track.Type}: {ordered[i - 1].Id} ends {ordered[i - 1].EndFrame}, {ordered[i].Id} starts {ordered[i].StartFrame}");
        }
    }

    [Fact]
    public void AddClipAt_OverwritesInsteadOfOverlapping() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            // 60-frame clip at 0, then drop another straddling its middle.
            CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 60, 0);
            CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 60, 30);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            AssertNoOverlap(state);
            var video = state.Tracks.Single(t => t.Type == "video").Clips.OrderBy(c => c.StartFrame).ToList();
            Assert.Equal(2, video.Count);
            Assert.Equal((0, 30), (video[0].StartFrame, video[0].DurationFrames));  // trimmed to 30
            Assert.Equal((30, 60), (video[1].StartFrame, video[1].DurationFrames));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void AddClipAt_InsideLongerClip_SplitsIt() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string bottom = CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 90, 0)!;
            CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 30, 30);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            AssertNoOverlap(state);
            var video = state.Tracks.Single(t => t.Type == "video").Clips.OrderBy(c => c.StartFrame).ToList();
            Assert.Equal(3, video.Count);
            Assert.Equal((0, 30), (video[0].StartFrame, video[0].DurationFrames));
            Assert.Equal(bottom, video[0].Id);
            Assert.Equal((30, 60), (video[1].StartFrame, video[1].EndFrame));
            // Right remainder plays its original source offset (trim carried).
            Assert.Equal((60, 30), (video[2].StartFrame, video[2].DurationFrames));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void MoveClip_ClampsAgainstNeighborInsteadOfCutting() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string first = CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 60, 0)!;
            string second = CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 60, 90)!;
            // Dragging the second clip onto the first sticks flush at its end.
            Assert.Equal(1, CoreApi.palmier_timeline_move_clip(project, second, 30));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            AssertNoOverlap(state);
            var video = state.Tracks.Single(t => t.Type == "video").Clips.OrderBy(c => c.StartFrame).ToList();
            Assert.Equal((0, 60), (video[0].StartFrame, video[0].DurationFrames));  // untouched
            Assert.Equal(first, video[0].Id);
            Assert.Equal((60, 60), (video[1].StartFrame, video[1].DurationFrames)); // clamped to 60
            Assert.Equal(second, video[1].Id);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void TrimClip_RightEdgeShortensAndLeftEdgeCarriesTrim() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, clipId, 1, 40));
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, clipId, 0, 10));
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(clipId)!;
            Assert.Equal((10, 30, 10), (clip.StartFrame, clip.DurationFrames, clip.TrimStartFrame));

            // Restoring the head reuses the trimmed-off source frames.
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, clipId, 0, 0));
            clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(clipId)!;
            Assert.Equal((0, 40, 0), (clip.StartFrame, clip.DurationFrames, clip.TrimStartFrame));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void TrimClip_ClampsToSourceLengthNeighborsAndMinimumDuration() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            // testsrc.mp4 is 2 s = 60 timeline frames of source.
            string first = CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 30, 0)!;
            string second = CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 30, 30)!;

            // Right edge past the source end clamps to 60 total source frames…
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, second, 1, 300));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(90, state.FindClip(second)!.EndFrame);
            // …and the first clip's right edge clamps against its neighbor.
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, first, 1, 300));
            state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(30, state.FindClip(first)!.EndFrame);
            AssertNoOverlap(state);

            // A boundary past the clip's other edge leaves at least 1 frame.
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, first, 1, 0));
            Assert.Equal(1, TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(first)!.DurationFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SetFades_ClampsWithinClipDuration() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_set_fades(project, clipId, 15, 20));
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(clipId)!;
            Assert.Equal((15, 20), (clip.FadeInFrames, clip.FadeOutFrames));

            // Oversized ramps clamp to fit inside the clip.
            Assert.Equal(1, CoreApi.palmier_clip_set_fades(project, clipId, 100, 100));
            clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(clipId)!;
            Assert.True(clip.FadeInFrames + clip.FadeOutFrames <= 60);
            Assert.Equal(0, CoreApi.palmier_clip_set_fades(project, clipId, -1, 0));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void TrimClip_MirrorsOntoLinkedAudio() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string videoId = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, videoId, 1, 40));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(40, state.FindClip(videoId)!.EndFrame);
            var audioClip = state.Tracks.Single(t => t.Type == "audio").Clips.Single();
            Assert.Equal(40, audioClip.EndFrame);

            Assert.Equal(1, CoreApi.palmier_clip_trim(project, videoId, 0, 10));
            state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal((10, 10), (state.FindClip(videoId)!.StartFrame, state.FindClip(videoId)!.TrimStartFrame));
            audioClip = state.Tracks.Single(t => t.Type == "audio").Clips.Single();
            Assert.Equal((10, 10), (audioClip.StartFrame, audioClip.TrimStartFrame));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void TextClip_AddEditAndAppearInSnapshot() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string? id = CoreApi.AddTextClip(project, "Hello", 30, 120);
            Assert.NotNull(id);
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(id!)!;
            Assert.Equal(("text", 30, 120, "Hello"),
                (clip.MediaType, clip.StartFrame, clip.DurationFrames, clip.TextContent));

            Assert.Equal(1, CoreApi.palmier_clip_set_text(project, id!, "Title card"));
            clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(id!)!;
            Assert.Equal("Title card", clip.TextContent);

            // set_text refuses non-text clips.
            string videoId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(0, CoreApi.palmier_clip_set_text(project, videoId, "nope"));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Theory]
    [InlineData("opacity", 0.5, 0)]
    [InlineData("rotation", 45.0, 0)]
    [InlineData("volume", -6.0, 0)]
    [InlineData("position", 0.1, 0.2)]
    [InlineData("scale", 0.5, 0.5)]
    public void Keyframes_AddAndClearPerProperty(string property, double v1, double v2) {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_add_keyframe(project, clipId, property, 10, v1, v2));
            Assert.Equal(1, CoreApi.palmier_clip_add_keyframe(project, clipId, property, 50, v1, v2));
            Assert.Contains($"\"{property}Track\"", CoreApi.GetTimelineJson(project));
            Assert.Equal(1, CoreApi.palmier_clip_clear_keyframes(project, clipId, property));
            Assert.DoesNotContain($"\"{property}Track\"", CoreApi.GetTimelineJson(project));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Keyframes_RejectInvalidPropertyAndValues() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(0, CoreApi.palmier_clip_add_keyframe(project, clipId, "bogus", 10, 1, 0));
            Assert.Equal(0, CoreApi.palmier_clip_add_keyframe(project, clipId, "opacity", 10, 2.0, 0));
            Assert.Equal(0, CoreApi.palmier_clip_add_keyframe(project, clipId, "opacity", 10, double.NaN, 0));
            Assert.Equal(0, CoreApi.palmier_clip_clear_keyframes(project, clipId, "bogus"));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void MoveClip_MovesLinkedGroupTogether() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var video = state.Tracks.Single(t => t.Type == "video").Clips.Single();

            Assert.Equal(1, CoreApi.palmier_timeline_move_clip(project, video.Id, 90));
            var after = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(90, after.Tracks.Single(t => t.Type == "video").Clips.Single().StartFrame);
            Assert.Equal(90, after.Tracks.Single(t => t.Type == "audio").Clips.Single().StartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void MoveClip_RejectsNegativeTargetAndUnknownClip() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clipId = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.Equal(0, CoreApi.palmier_timeline_move_clip(project, clipId, -10));
            Assert.Equal(0, CoreApi.palmier_timeline_move_clip(project, "missing", 10));
            Assert.Equal(1, CoreApi.palmier_timeline_move_clip(project, clipId, 0));  // no-op move reports success
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Thumbnails_ReturnsNonBlackTiles() {
        const int count = 4;
        var result = CoreApi.GetThumbnails(TestMediaPath("testsrc.mp4"), count);
        Assert.NotNull(result);
        Assert.Equal(count, result.Value.Count);
        // testsrc frames are colorful — the strip must not be all near-black.
        Assert.Contains(result.Value.Tiles, b => b > 32);
    }

    [Fact]
    public void Thumbnails_FailsForMissingFile() {
        Assert.Null(CoreApi.GetThumbnails(TestMediaPath("does-not-exist.mp4"), 2));
    }

    [Fact]
    public void ProbeMedia_FailsForMissingFile() {
        Assert.Null(CoreApi.ProbeMedia(TestMediaPath("does-not-exist.mp4")));
    }

    [Fact]
    public void ProbeMedia_SkipsAttachedPicStreams() {
        var probe = CoreApi.ProbeMedia(TestMediaPath("coverart.mp4"));
        Assert.NotNull(probe);
        Assert.Equal(320, probe.Value.Width);
        Assert.Equal(240, probe.Value.Height);
        Assert.Equal(30, probe.Value.Fps, 1);
    }
}
