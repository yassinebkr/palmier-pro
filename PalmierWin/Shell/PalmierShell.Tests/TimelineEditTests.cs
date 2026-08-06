using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class TimelineEditTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) => TimelineState.Parse(CoreApi.GetTimelineJson(project));

    [Fact]
    public void AddTrack_PutsVideoAboveAudioAndKeepsTheOrder() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.NotNull(CoreApi.AddTrack(project, "video"));
            Assert.NotNull(CoreApi.AddTrack(project, "audio"));
            var types = State(project).Tracks.Select(t => t.Type).ToList();
            Assert.Equal(["video", "video", "audio", "audio"], types);
            Assert.Equal([50, 50, 72, 72],
                State(project).Tracks.Select(t => (int)t.DisplayHeight).ToList());
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RemoveTrack_RefusesTheLastOfItsKind() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var state = State(project);
            string video = state.Tracks.First(t => t.Type == "video").Id;
            string audio = state.Tracks.First(t => t.Type == "audio").Id;
            Assert.Equal(0, CoreApi.palmier_timeline_remove_track(project, video));
            Assert.Equal(0, CoreApi.palmier_timeline_remove_track(project, audio));

            string extra = CoreApi.AddTrack(project, "video")!;
            Assert.Equal(1, CoreApi.palmier_timeline_remove_track(project, extra));
            Assert.Equal(2, State(project).Tracks.Count);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Unlink_LetsTheAudioBeTrimmedWithoutTheVideo() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            var clips = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            var video = clips.Single(c => c.MediaType == "video");
            var audio = clips.Single(c => c.MediaType == "audio");
            Assert.NotNull(video.LinkGroupId);

            Assert.Equal(1, CoreApi.palmier_clip_unlink(project, video.Id));
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, audio.Id, 1, 30));

            var after = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            Assert.Equal(30, after.Single(c => c.Id == audio.Id).DurationFrames);
            Assert.Equal(60, after.Single(c => c.Id == video.Id).DurationFrames);
            Assert.All(after, c => Assert.Null(c.LinkGroupId));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Link_ReconnectsTwoClipsSoTrimsMirror() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            var clips = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            var video = clips.Single(c => c.MediaType == "video");
            var audio = clips.Single(c => c.MediaType == "audio");
            CoreApi.palmier_clip_unlink(project, video.Id);

            Assert.Equal(1, CoreApi.palmier_clip_link(project, video.Id, audio.Id));
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, video.Id, 1, 40));

            var after = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            Assert.Equal(40, after.Single(c => c.Id == video.Id).DurationFrames);
            Assert.Equal(40, after.Single(c => c.Id == audio.Id).DurationFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// A real cut comes from a split, which is what leaves the incoming clip
    /// with source before its in-point — the material a roll needs.
    static (string Left, string Right) SplitPair(IntPtr project, int at = 30) {
        string whole = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
        string right = CoreApi.SplitClip(project, whole, at)!;
        return (whole, right);
    }

    [Fact]
    public void RollEdit_MovesTheCutWithoutChangingTheCombinedLength() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var (a, b) = SplitPair(project);
            int totalBefore = State(project).TotalFrames;

            Assert.Equal(20, CoreApi.palmier_timeline_roll_edit(project, a, b, 20));

            var clips = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            Assert.Equal(20, clips.Single(c => c.Id == a).DurationFrames);
            Assert.Equal(20, clips.Single(c => c.Id == b).StartFrame);
            Assert.Equal(40, clips.Single(c => c.Id == b).DurationFrames);
            Assert.Equal(totalBefore, State(project).TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RollEdit_RollsTheOtherWayToo() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var (a, b) = SplitPair(project);
            Assert.Equal(45, CoreApi.palmier_timeline_roll_edit(project, a, b, 45));

            var clips = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            Assert.Equal(45, clips.Single(c => c.Id == a).DurationFrames);
            Assert.Equal(45, clips.Single(c => c.Id == b).StartFrame);
            Assert.Equal(15, clips.Single(c => c.Id == b).DurationFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RollEdit_ClampsRatherThanCollapsingAClip() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var (a, b) = SplitPair(project);

            int applied = CoreApi.palmier_timeline_roll_edit(project, a, b, -500);
            Assert.True(applied >= 1, $"boundary collapsed to {applied}");
            var clips = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            Assert.True(clips.Single(c => c.Id == a).DurationFrames >= 1);
            Assert.True(clips.Single(c => c.Id == b).DurationFrames >= 1);
            // The pair still ends where it did: a roll never ripples.
            Assert.Equal(60, State(project).TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Rolling a fresh, untrimmed cut can only go one way — the incoming clip
    /// has no source before its in-point to give back.
    [Fact]
    public void RollEdit_CannotPullSourceThatDoesNotExist() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string a = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            string b = CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 30, 30)!;
            Assert.Equal(30, CoreApi.palmier_timeline_roll_edit(project, a, b, 10));
            Assert.Equal(30, State(project).Tracks.SelectMany(t => t.Clips)
                                                 .Single(c => c.Id == a).DurationFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RollEdit_RefusesClipsThatDoNotTouch() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string a = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            string b = CoreApi.AddClipAt(project, TestMediaPath("testsrc.mp4"), 30, 90)!;
            Assert.Equal(-1, CoreApi.palmier_timeline_roll_edit(project, a, b, 45));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
