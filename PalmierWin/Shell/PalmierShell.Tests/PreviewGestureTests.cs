using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class PreviewGestureTests {
    /// A clip filling the frame: centre 0.5,0.5 and full width/height.
    static readonly ClipTransform Full = new(0.5, 0.5, 1, 1, 0);
    /// Half-size, centred — leaves room outside the box for the rotate ring.
    static readonly ClipTransform Half = new(0.5, 0.5, 0.5, 0.5, 0);

    const double Width = 800, Height = 450;

    static PreviewGesture.Grip GripAt(ClipTransform clip, double x, double y, bool alt = false) =>
        PreviewGesture.GripFor(clip, x, y, alt, Width, Height);

    [Fact]
    public void DragFromTheMiddleMovesTheClip() {
        var moved = PreviewGesture.Apply(Full, PreviewGesture.Grip.Move,
            0.5, 0.5, 0.7, 0.4, lockAspect: false);
        Assert.Equal(0.7, moved.CenterX, 3);
        Assert.Equal(0.4, moved.CenterY, 3);
        Assert.Equal(Full.Width, moved.Width, 3);
        Assert.Equal(0, moved.Rotation, 3);
    }

    [Theory]
    [InlineData(0.25, 0.25, PreviewGesture.Grip.ScaleNW)]
    [InlineData(0.75, 0.25, PreviewGesture.Grip.ScaleNE)]
    [InlineData(0.75, 0.75, PreviewGesture.Grip.ScaleSE)]
    [InlineData(0.25, 0.75, PreviewGesture.Grip.ScaleSW)]
    [InlineData(0.50, 0.25, PreviewGesture.Grip.ScaleN)]
    [InlineData(0.75, 0.50, PreviewGesture.Grip.ScaleE)]
    [InlineData(0.50, 0.50, PreviewGesture.Grip.Move)]
    public void EachHandleGrabsItsOwnCorner(double x, double y, PreviewGesture.Grip expected) {
        Assert.Equal(expected, GripAt(Half, x, y));
    }

    [Fact]
    public void JustOutsideTheFrameRotates() {
        Assert.Equal(PreviewGesture.Grip.Rotate, GripAt(Half, 0.5, 0.22));
    }

    [Fact]
    public void AHandleWinsOverTheRotateRingWhereTheyOverlap() {
        Assert.Equal(PreviewGesture.Grip.ScaleN, GripAt(Half, 0.5, 0.24));
    }

    [Fact]
    public void WellOutsideTheFrameGrabsNothing() {
        Assert.Equal(PreviewGesture.Grip.None, GripAt(Half, 0.05, 0.05));
    }

    [Fact]
    public void AltRotatesFromAnywhereOnTheClip() {
        Assert.Equal(PreviewGesture.Grip.Rotate, GripAt(Full, 0.5, 0.5, alt: true));
        Assert.Equal(PreviewGesture.Grip.Rotate, GripAt(Half, 0.25, 0.25, alt: true));
    }

    [Fact]
    public void HandlesFollowTheClipWhenItIsRotated() {
        // Rotated 90°, the box's top-left corner sits at its former top-right.
        var turned = Half with { Rotation = 90 };
        Assert.Equal(PreviewGesture.Grip.ScaleNW, GripAt(turned, 0.75, 0.25));
        Assert.Equal(PreviewGesture.Grip.ScaleSE, GripAt(turned, 0.25, 0.75));
    }

    [Fact]
    public void ScalingKeepsTheClipCentred() {
        var scaled = PreviewGesture.Apply(Half, PreviewGesture.Grip.ScaleSE,
            0.75, 0.75, 0.95, 0.90, lockAspect: false);
        Assert.Equal(Half.CenterX, scaled.CenterX, 3);
        Assert.Equal(Half.CenterY, scaled.CenterY, 3);
        // The grabbed corner still lands under the pointer.
        Assert.Equal(0.95, scaled.CenterX + scaled.Width / 2, 3);
        Assert.Equal(0.90, scaled.CenterY + scaled.Height / 2, 3);
    }

    [Fact]
    public void CtrlScalingHoldsTheOppositeCornerStill() {
        // Drag the SE corner out; the NW corner must not move.
        var scaled = PreviewGesture.Apply(Half, PreviewGesture.Grip.ScaleSE,
            0.75, 0.75, 0.95, 0.95, lockAspect: false, fromCorner: true);
        Assert.Equal(0.25, scaled.CenterX - scaled.Width / 2, 3);
        Assert.Equal(0.25, scaled.CenterY - scaled.Height / 2, 3);
        Assert.Equal(0.95, scaled.CenterX + scaled.Width / 2, 3);
    }

    [Fact]
    public void TheGrabbedCornerLandsUnderThePointer() {
        var scaled = PreviewGesture.Apply(Half, PreviewGesture.Grip.ScaleNW,
            0.25, 0.25, 0.35, 0.40, lockAspect: false, fromCorner: true);
        Assert.Equal(0.35, scaled.CenterX - scaled.Width / 2, 3);
        Assert.Equal(0.40, scaled.CenterY - scaled.Height / 2, 3);
    }

    /// The knob is the only rotate affordance a full-frame clip has, so it has
    /// to be reachable when the box touches every edge of the canvas.
    [Fact]
    public void TheRotateKnobIsReachableOnAClipFillingTheFrame() {
        double offset = PreviewGesture.RotateKnobOffset(Full.Height, Height);
        Assert.Equal(PreviewGesture.Grip.Rotate, GripAt(Full, 0.5, 0.5 - offset));
    }

    [Fact]
    public void TheRotateKnobSitsInsideTheFrame() {
        double offset = PreviewGesture.RotateKnobOffset(Half.Height, Height);
        Assert.True(offset < Half.Height / 2);
        Assert.Equal(PreviewGesture.Grip.Rotate, GripAt(Half, 0.5, 0.5 - offset));
    }

    [Fact]
    public void TheRotateKnobTurnsWithTheClip() {
        var turned = Half with { Rotation = 90 };
        double offset = PreviewGesture.RotateKnobOffset(turned.Height, Height);
        // Rotated a quarter turn clockwise, "above" points to the right.
        Assert.Equal(PreviewGesture.Grip.Rotate, GripAt(turned, 0.5 + offset, 0.5));
    }

    [Fact]
    public void RotateKnobOffsetMatchesTheEngine() {
        Assert.Equal(PreviewGesture.RotateOffsetPixels,
                     CoreApi.palmier_selection_rotate_offset(), 3);
    }

    [Fact]
    public void AnEdgeHandleScalesOneAxisOnly() {
        var scaled = PreviewGesture.Apply(Half, PreviewGesture.Grip.ScaleE,
            0.75, 0.5, 0.9, 0.2, lockAspect: false);
        Assert.Equal(0.8, scaled.Width, 3);
        Assert.Equal(Half.Height, scaled.Height, 3);
    }

    [Fact]
    public void ShiftScalingKeepsTheAspect() {
        var wide = new ClipTransform(0.5, 0.5, 0.4, 0.2, 0);
        var scaled = PreviewGesture.Apply(wide, PreviewGesture.Grip.ScaleSE,
            0.7, 0.6, 0.9, 0.65, lockAspect: true);
        Assert.Equal(wide.Width / wide.Height, scaled.Width / scaled.Height, 3);
    }

    [Fact]
    public void ScaleCannotCollapseOrExplodeTheClip() {
        var tiny = PreviewGesture.Apply(Half, PreviewGesture.Grip.ScaleSE,
            0.75, 0.75, 0.1, 0.1, lockAspect: false);
        Assert.True(tiny.Width >= 0.02);
        Assert.True(tiny.Height >= 0.02);

        var huge = PreviewGesture.Apply(Half, PreviewGesture.Grip.ScaleSE,
            0.75, 0.75, 500, 500, lockAspect: false);
        Assert.True(huge.Width <= 20);
    }

    [Fact]
    public void ScalingARotatedClipStaysInItsOwnFrame() {
        var turned = Half with { Rotation = 90 };
        var scaled = PreviewGesture.Apply(turned, PreviewGesture.Grip.ScaleE,
            0.5, 0.75, 0.5, 0.95, lockAspect: false);
        // The east edge points down at 90°, so pulling down widens the clip
        // along its own X while the centre stays put.
        Assert.Equal(0.9, scaled.Width, 3);
        Assert.Equal(Half.Height, scaled.Height, 3);
        Assert.Equal(0.5, scaled.CenterX, 3);
        Assert.Equal(0.5, scaled.CenterY, 3);
    }

    [Fact]
    public void RotatingAQuarterTurnAddsNinetyDegrees() {
        // Right of centre → below centre is a quarter turn clockwise.
        var turned = PreviewGesture.Apply(Full, PreviewGesture.Grip.Rotate,
            1.0, 0.5, 0.5, 1.0, lockAspect: false);
        Assert.Equal(90, turned.Rotation, 1);
        Assert.Equal(Full.Width, turned.Width, 3);
    }

    [Fact]
    public void RotationStaysInZeroToThreeSixty() {
        var back = PreviewGesture.Apply(Full, PreviewGesture.Grip.Rotate,
            1.0, 0.5, 0.5, 0.0, lockAspect: false);
        Assert.InRange(back.Rotation, 0, 360);
        Assert.Equal(270, back.Rotation, 1);
    }

    [Fact]
    public void ShiftRotationSnapsToFifteenDegrees() {
        var turned = PreviewGesture.Apply(Full, PreviewGesture.Grip.Rotate,
            1.0, 0.5, 0.95, 0.62, lockAspect: true);
        Assert.Equal(0, turned.Rotation % 15, 3);
    }

    [Fact]
    public void ContainsRespectsRotation() {
        var thin = new ClipTransform(0.5, 0.5, 0.6, 0.1, 90);
        Assert.True(PreviewGesture.Contains(thin, 0.5, 0.75));
        Assert.False(PreviewGesture.Contains(thin, 0.75, 0.5));
    }

    [Fact]
    public void AZeroSizedClipMovesRatherThanDividingByZero() {
        var degenerate = new ClipTransform(0.5, 0.5, 0, 0, 0);
        Assert.Equal(PreviewGesture.Grip.Move, GripAt(degenerate, 0.5, 0.5));
        Assert.False(PreviewGesture.Contains(degenerate, 0.5, 0.5));
        var scaled = PreviewGesture.Apply(degenerate, PreviewGesture.Grip.ScaleSE,
            0.5, 0.5, 0.6, 0.6, lockAspect: false);
        Assert.True(double.IsFinite(scaled.Width));
    }

    [Fact]
    public void AZeroSizedSurfaceGrabsNothing() {
        Assert.Equal(PreviewGesture.Grip.None,
            PreviewGesture.GripFor(Full, 0.5, 0.5, alt: false, 0, 0));
    }

    /// The engine draws the handles and the shell hit-tests them. If the two
    /// sizes drift you get handles you can see but cannot grab.
    [Fact]
    public void HandleSizeMatchesTheOneTheEngineDraws() {
        Assert.Equal(PreviewGesture.HandlePixels, CoreApi.palmier_selection_handle_size(), 3);
    }
}
