using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class TrackMoveTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) => TimelineState.Parse(CoreApi.GetTimelineJson(project));

    [Fact]
    public void MoveToTrack_PutsTheClipOnTheOtherVideoTrack() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clip = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            string target = CoreApi.AddTrack(project, "video")!;

            Assert.Equal(1, CoreApi.palmier_timeline_move_clip_to_track(project, clip, target, 45));

            var tracks = State(project).Tracks;
            Assert.Empty(tracks.First(t => t.Type == "video").Clips);
            var moved = Assert.Single(tracks.Single(t => t.Id == target).Clips);
            Assert.Equal(clip, moved.Id);
            Assert.Equal(45, moved.StartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void MoveToTrack_RefusesTheWrongKindOfTrack() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clip = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            string audio = State(project).Tracks.First(t => t.Type == "audio").Id;
            Assert.Equal(0, CoreApi.palmier_timeline_move_clip_to_track(project, clip, audio, 0));
            Assert.Single(State(project).Tracks.First(t => t.Type == "video").Clips);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void MoveToTrack_IsANoOpOnTheTrackItAlreadySitsOn() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string clip = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            string same = State(project).Tracks.First(t => t.Type == "video").Id;
            Assert.Equal(0, CoreApi.palmier_timeline_move_clip_to_track(project, clip, same, 10));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Moving half of a linked pair away breaks the link — leaving them
    /// "linked" across tracks while only one moved would be a lie.
    [Fact]
    public void MoveToTrack_BreaksALinkedPair() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            string video = State(project).Tracks.SelectMany(t => t.Clips)
                                                .First(c => c.MediaType == "video").Id;
            string target = CoreApi.AddTrack(project, "video")!;

            Assert.Equal(1, CoreApi.palmier_timeline_move_clip_to_track(project, video, target, 0));
            Assert.All(State(project).Tracks.SelectMany(t => t.Clips), c => Assert.Null(c.LinkGroupId));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void MoveToTrack_OverwritesWhatItLandsOn() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string target = CoreApi.AddTrack(project, "video")!;
            string sitting = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            CoreApi.palmier_timeline_move_clip_to_track(project, sitting, target, 0);

            string moving = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            Assert.Equal(1, CoreApi.palmier_timeline_move_clip_to_track(project, moving, target, 0));

            var clips = State(project).Tracks.Single(t => t.Id == target).Clips;
            Assert.Equal(moving, Assert.Single(clips).Id);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
