using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// A shot generated into empty timeline space has to match what it sits
/// between, so the composer seeds its two reference stills from the clips
/// bracketing the space. Right-clicking a gap used to open the composer with
/// both slots empty and leave the user to find those frames by hand.
public class ShotFramesTests {
    static TrackState TrackWith(params (int Start, int Duration)[] clips) {
        var list = clips.Select((c, i) => Clip($"c{i}", c.Start, c.Duration)).ToList();
        return new TrackState("v1", "video", false, false, list);
    }

    static ClipState Clip(string id, int start, int duration,
                          int trim = 0, double speed = 1.0, string mediaType = "video") =>
        new(id, $"{id}.mp4", mediaType, start, duration, trim, speed, 1, 1, 0, 0,
            new TransformState(0, 0, 0, 0, 0), null, null, null, null, null, null, null);

    [Fact]
    public void AGapIsBracketedByTheClipsOnEitherSide() {
        var track = TrackWith((0, 100), (150, 100));
        var (before, after) = track.ClipsAround(100, 150);
        Assert.Equal("c0", before?.Id);
        Assert.Equal("c1", after?.Id);
    }

    [Fact]
    public void SpaceAfterTheLastClipHasNothingFollowingIt() {
        var track = TrackWith((0, 100));
        var (before, after) = track.ClipsAround(140, 140);
        Assert.Equal("c0", before?.Id);
        Assert.Null(after);
    }

    [Fact]
    public void SpaceBeforeTheFirstClipHasNothingPrecedingIt() {
        var track = TrackWith((200, 100));
        var (before, after) = track.ClipsAround(0, 200);
        Assert.Null(before);
        Assert.Equal("c0", after?.Id);
    }

    [Fact]
    public void TheNearestClipOnEachSideWins() {
        var track = TrackWith((0, 50), (60, 40), (300, 50), (400, 50));
        var (before, after) = track.ClipsAround(100, 300);
        Assert.Equal("c1", before?.Id);
        Assert.Equal("c2", after?.Id);
    }

    /// A text clip has no media to read a still from.
    [Fact]
    public void TextClipsAreNotOfferedAsReferenceFrames() {
        var track = new TrackState("v1", "video", false, false, [
            Clip("video", 0, 100),
            Clip("title", 100, 40, mediaType: "text"),
        ]);
        var (before, after) = track.ClipsAround(140, 200);
        Assert.Equal("video", before?.Id);
        Assert.Null(after);
    }

    /// The stills come from where the compositor reads the clip, so a trimmed
    /// or retimed neighbour must resolve through the same rule.
    [Theory]
    [InlineData(0, 1.0, 0, 99)]      // untrimmed, 1x: first source frame is 0
    [InlineData(40, 1.0, 40, 139)]   // head-trimmed by 40
    [InlineData(10, 2.0, 10, 208)]   // 2x speed consumes two source frames per timeline frame
    public void ReferenceFramesFollowTrimAndSpeed(int trim, double speed, int first, int last) {
        var clip = Clip("c", start: 500, duration: 100, trim: trim, speed: speed);
        Assert.Equal(first, clip.FirstSourceFrame);
        Assert.Equal(last, clip.LastSourceFrame);
    }
}
