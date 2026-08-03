using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// A clip imported with sound becomes a linked video/audio pair. Edits that
/// touch one side and not the other leave sound playing from a picture that is
/// no longer there.
public class LinkedEditTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) => TimelineState.Parse(CoreApi.GetTimelineJson(project));

    static List<ClipState> AllClips(IntPtr project) =>
        State(project).Tracks.SelectMany(t => t.Clips).ToList();

    /// testav.mp4 has sound, so importing it creates a linked pair.
    static (IntPtr Project, string VideoId) LinkedProject() {
        IntPtr project = CoreApi.palmier_project_create();
        string video = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
        return (project, video);
    }

    [Fact]
    public void ImportingMediaWithSoundMakesALinkedPair() {
        var (project, video) = LinkedProject();
        try {
            var clips = AllClips(project);
            Assert.Equal(2, clips.Count);
            Assert.All(clips, c => Assert.NotNull(c.LinkGroupId));
            Assert.Single(clips.Select(c => c.LinkGroupId).Distinct());
            Assert.Contains(clips, c => c.Id == video);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void DeletingTheVideoTakesItsAudioWithIt() {
        var (project, video) = LinkedProject();
        try {
            Assert.Equal(1, CoreApi.palmier_timeline_remove_clip(project, video));
            Assert.Empty(AllClips(project));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void DeletingAnUnlinkedClipLeavesTheRestAlone() {
        var (project, video) = LinkedProject();
        try {
            CoreApi.palmier_clip_unlink(project, video);
            Assert.Equal(1, CoreApi.palmier_timeline_remove_clip(project, video));
            Assert.Single(AllClips(project));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SplittingAClipSplitsItsPartnerAtTheSameFrame() {
        var (project, video) = LinkedProject();
        try {
            Assert.NotNull(CoreApi.SplitClip(project, video, 30));
            var clips = AllClips(project);
            Assert.Equal(4, clips.Count);
            Assert.Equal(2, clips.Count(c => c.StartFrame == 30));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Each half must be its own pair, or deleting one half would take the
    /// other half's audio with it.
    [Fact]
    public void EachHalfOfASplitIsItsOwnPair() {
        var (project, video) = LinkedProject();
        try {
            string right = CoreApi.SplitClip(project, video, 30)!;
            var clips = AllClips(project);
            var leftGroup = clips.Single(c => c.Id == video).LinkGroupId;
            var rightGroup = clips.Single(c => c.Id == right).LinkGroupId;
            Assert.NotNull(leftGroup);
            Assert.NotNull(rightGroup);
            Assert.NotEqual(leftGroup, rightGroup);
            Assert.Equal(2, clips.Count(c => c.LinkGroupId == leftGroup));
            Assert.Equal(2, clips.Count(c => c.LinkGroupId == rightGroup));

            Assert.Equal(1, CoreApi.palmier_timeline_remove_clip(project, video));
            var remaining = AllClips(project);
            Assert.Equal(2, remaining.Count);
            Assert.All(remaining, c => Assert.Equal(30, c.StartFrame));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SplittingAnUnlinkedClipStillWorks() {
        var (project, video) = LinkedProject();
        try {
            CoreApi.palmier_clip_unlink(project, video);
            string right = CoreApi.SplitClip(project, video, 30)!;
            var clips = AllClips(project);
            Assert.Equal(3, clips.Count);   // two video halves + the loose audio
            Assert.Equal(30, clips.Single(c => c.Id == right).StartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
