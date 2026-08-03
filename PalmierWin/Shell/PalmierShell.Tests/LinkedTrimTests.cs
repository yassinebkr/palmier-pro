using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// Trimming one half of a linked pair must trim the other, or the pair plays
/// out of sync — the user's Q/W repro: video trimmed to the playhead, audio
/// left whole.
public class LinkedTrimTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) =>
        TimelineState.Parse(CoreApi.GetTimelineJson(project));

    static (ClipState Video, ClipState Audio) Pair(IntPtr project, string videoId) {
        var state = State(project);
        var video = state.FindClip(videoId)!;
        var audio = state.Tracks.First(t => t.Type == "audio").Clips
            .Single(c => c.LinkGroupId == video.LinkGroupId);
        return (video, audio);
    }

    [Fact]
    public void HeadTrimMirrorsOntoAlignedLinkedAudio() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, id, 0, 10));
            var (video, audio) = Pair(project, id);
            Assert.Equal(10, video.StartFrame);
            Assert.Equal(10, audio.StartFrame);
            Assert.Equal(video.EndFrame, audio.EndFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// The user's exact sequence: Q then W. After the head trim the pair's
    /// placements have both changed; the tail trim must still mirror.
    [Fact]
    public void TailTrimAfterAHeadTrimStillMirrors() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, id, 0, 10));
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, id, 1, 40));
            var (video, audio) = Pair(project, id);
            Assert.Equal(10, audio.StartFrame);
            Assert.Equal(40, audio.EndFrame);
            Assert.Equal(video.StartFrame, audio.StartFrame);
            Assert.Equal(video.EndFrame, audio.EndFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// A pair whose tails have genuinely diverged (lone trim while unlinked,
    /// then relinked) still trims together on the edge they share. Mirroring
    /// used to require the whole placement to match, so one divergence ended
    /// mirroring forever — the user's Q left the audio behind and every trim
    /// after that made the pair worse.
    [Fact]
    public void SharedEdgeStillMirrorsWhenTheOtherEdgeDiverged() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            var (videoBefore, audioBefore) = Pair(project, id);
            Assert.Equal(1, CoreApi.palmier_clip_unlink(project, id));
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, audioBefore.Id, 1, 50));
            Assert.Equal(1, CoreApi.palmier_clip_link(project, videoBefore.Id, audioBefore.Id));

            // Tails differ (60 vs 50); heads share frame 0, so Q moves both.
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, id, 0, 15));
            var (video, audio) = Pair(project, id);
            Assert.Equal(15, video.StartFrame);
            Assert.Equal(15, audio.StartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
