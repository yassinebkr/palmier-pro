namespace PalmierShell.Core;

/// A clip transform in the units the core stores: centre and size as a
/// fraction of the frame, rotation in degrees clockwise.
public readonly record struct ClipTransform(
    double CenterX, double CenterY, double Width, double Height, double Rotation);

/// Turns a drag across the preview into a transform. Kept out of the view so
/// the arithmetic can be tested without a window or a GPU.
///
/// The handles this hit-tests are the ones the engine draws
/// (`SelectionOverlay.handleCentres`), in the same order, so what the pointer
/// grabs is what the eye sees.
public static class PreviewGesture {
    /// What a drag does. The eight scale grips are named for the box corner or
    /// edge they pull, in the clip's own rotated frame.
    public enum Grip {
        None, Move, Rotate,
        ScaleNW, ScaleNE, ScaleSE, ScaleSW,
        ScaleN, ScaleE, ScaleS, ScaleW,
    }

    /// Handle side in preview pixels — the engine's `handlePixels`, plus a
    /// couple of pixels of slop so a handle is easier to hit than to miss.
    public const double HandlePixels = 9;
    const double GrabSlop = 4;

    /// Ring outside the box, in pixels, where a drag rotates instead of moves.
    const double RotateBandPixels = 22;

    /// Distance from the top edge to the rotate knob, in pixels — the engine's
    /// `rotateOffsetPixels`. The knob is drawn inside the frame because a clip
    /// filling the canvas leaves no room outside it.
    public const double RotateOffsetPixels = 20;

    /// A clip may never be scaled away to nothing or blown up past this.
    const double MinSize = 0.02, MaxSize = 20;

    /// Which grip a press at (x, y) takes, in 0…1 preview coordinates.
    /// `surfaceWidth`/`surfaceHeight` are the preview's pixel size, so handles
    /// keep a constant on-screen target however the panel is sized. Alt forces
    /// rotation, matching the modifier the engine's frame advertises.
    public static Grip GripFor(ClipTransform clip, double x, double y,
                               bool alt, double surfaceWidth, double surfaceHeight) {
        if (surfaceWidth < 1 || surfaceHeight < 1) return Grip.None;
        double grabX = (HandlePixels / 2 + GrabSlop) / surfaceWidth;
        double grabY = (HandlePixels / 2 + GrabSlop) / surfaceHeight;

        // Work in the clip's unrotated frame so a rotated box hit-tests as the
        // rectangle the user sees rather than its bounding box.
        var (u, v) = ToLocal(clip, x, y);
        double half = 0.5;

        // The rotate knob wins over everything: it is the only affordance that
        // has no other way in.
        if (clip.Height > 0 &&
            Math.Abs(u) <= grabX * 1.4 &&
            Math.Abs(v + RotateKnobOffset(clip.Height, surfaceHeight)) <= grabY)
            return Grip.Rotate;

        if (clip.Width > 0 && clip.Height > 0) {
            var grips = new[] {
                (Grip.ScaleNW, -half, -half), (Grip.ScaleNE, half, -half),
                (Grip.ScaleSE, half, half), (Grip.ScaleSW, -half, half),
                (Grip.ScaleN, 0.0, -half), (Grip.ScaleE, half, 0.0),
                (Grip.ScaleS, 0.0, half), (Grip.ScaleW, -half, 0.0),
            };
            foreach (var (grip, fx, fy) in grips) {
                if (Math.Abs(u - fx * clip.Width) <= grabX &&
                    Math.Abs(v - fy * clip.Height) <= grabY)
                    return alt ? Grip.Rotate : grip;
            }
        }

        if (alt) return Grip.Rotate;

        bool inside = Math.Abs(u) <= clip.Width / 2 && Math.Abs(v) <= clip.Height / 2;
        if (inside) return Grip.Move;

        // Just outside the frame: the rotate ring, as in every other editor.
        double bandX = RotateBandPixels / surfaceWidth, bandY = RotateBandPixels / surfaceHeight;
        bool nearBox = Math.Abs(u) <= clip.Width / 2 + bandX &&
                       Math.Abs(v) <= clip.Height / 2 + bandY;
        return nearBox ? Grip.Rotate : Grip.None;
    }

    /// Applies a drag from (fromX, fromY) to (x, y), all in 0…1 preview space,
    /// against the transform as it was when the drag began. `lockAspect` is the
    /// Shift modifier: it keeps the aspect while scaling and snaps rotation.
    /// `fromCorner` is Ctrl: it pins the opposite corner instead of the centre.
    public static ClipTransform Apply(ClipTransform clip, Grip grip,
                                      double fromX, double fromY, double x, double y,
                                      bool lockAspect, bool fromCorner = false) {
        switch (grip) {
            case Grip.Move:
                return clip with {
                    CenterX = clip.CenterX + (x - fromX),
                    CenterY = clip.CenterY + (y - fromY),
                };

            case Grip.Rotate: {
                double before = Math.Atan2(fromY - clip.CenterY, fromX - clip.CenterX);
                double after = Math.Atan2(y - clip.CenterY, x - clip.CenterX);
                double rotation = clip.Rotation + (after - before) * 180 / Math.PI;
                if (lockAspect) rotation = Math.Round(rotation / 15) * 15;
                return clip with { Rotation = Wrap(rotation) };
            }

            case Grip.None:
                return clip;

            default:
                return Scale(clip, grip, x, y, lockAspect, fromCorner);
        }
    }

    /// Scaling grows the clip about its own centre, so a framed shot stays
    /// framed while you size it. Ctrl (`fromCorner`) pins the opposite corner
    /// instead, for when the anchor matters more than the centre.
    static ClipTransform Scale(ClipTransform clip, Grip grip, double x, double y,
                               bool lockAspect, bool fromCorner) {
        (double dirX, double dirY) = Direction(grip);
        var (u, v) = ToLocal(clip, x, y);

        double width = clip.Width, height = clip.Height;
        if (fromCorner) {
            // Anchor is the opposite side, at -dir * half; the pointer sets the
            // grabbed side, so the span between them is the new size.
            if (dirX != 0) width = Math.Clamp(dirX * u + clip.Width / 2, MinSize, MaxSize);
            if (dirY != 0) height = Math.Clamp(dirY * v + clip.Height / 2, MinSize, MaxSize);
        } else {
            // Centre fixed: the pointer is half the size away from it.
            if (dirX != 0) width = Math.Clamp(2 * Math.Abs(u), MinSize, MaxSize);
            if (dirY != 0) height = Math.Clamp(2 * Math.Abs(v), MinSize, MaxSize);
        }

        if (lockAspect && clip.Width > 0 && clip.Height > 0 && dirX != 0 && dirY != 0) {
            double factor = Math.Max(width / clip.Width, height / clip.Height);
            width = Math.Clamp(clip.Width * factor, MinSize, MaxSize);
            height = Math.Clamp(clip.Height * factor, MinSize, MaxSize);
        }

        if (!fromCorner) return clip with { Width = width, Height = height };

        // Put the centre back so the anchored side did not move: it sits at
        // -dir * half from the centre, before and after.
        double shiftU = dirX * (width - clip.Width) / 2;
        double shiftV = dirY * (height - clip.Height) / 2;
        var (worldDx, worldDy) = ToWorldDelta(clip.Rotation, shiftU, shiftV);
        return clip with {
            Width = width,
            Height = height,
            CenterX = clip.CenterX + worldDx,
            CenterY = clip.CenterY + worldDy,
        };
    }

    /// The knob's distance above the centre along the clip's local -Y, in
    /// normalized units. Same rule as the engine's `rotateKnobOffset`.
    public static double RotateKnobOffset(double height, double surfaceHeight) {
        if (surfaceHeight < 1) return 0;
        return Math.Max(0, height / 2 - Math.Min(RotateOffsetPixels / surfaceHeight, height * 0.3));
    }

    /// Unit direction of the grabbed side in the clip's local frame.
    static (double X, double Y) Direction(Grip grip) => grip switch {
        Grip.ScaleNW => (-1, -1),
        Grip.ScaleNE => (1, -1),
        Grip.ScaleSE => (1, 1),
        Grip.ScaleSW => (-1, 1),
        Grip.ScaleN => (0, -1),
        Grip.ScaleE => (1, 0),
        Grip.ScaleS => (0, 1),
        Grip.ScaleW => (-1, 0),
        _ => (0, 0),
    };

    /// Preview point → offset from the clip centre in the clip's own frame.
    static (double U, double V) ToLocal(ClipTransform clip, double x, double y) {
        double dx = x - clip.CenterX, dy = y - clip.CenterY;
        double rad = -clip.Rotation * Math.PI / 180;
        return (dx * Math.Cos(rad) - dy * Math.Sin(rad),
                dx * Math.Sin(rad) + dy * Math.Cos(rad));
    }

    /// The inverse: a local offset back into preview space.
    static (double X, double Y) ToWorldDelta(double rotation, double u, double v) {
        double rad = rotation * Math.PI / 180;
        return (u * Math.Cos(rad) - v * Math.Sin(rad),
                u * Math.Sin(rad) + v * Math.Cos(rad));
    }

    /// True when (x, y) falls inside the clip's rotated box — used to pick the
    /// clip a click in the preview selects.
    public static bool Contains(ClipTransform clip, double x, double y) {
        if (clip.Width <= 0 || clip.Height <= 0) return false;
        var (u, v) = ToLocal(clip, x, y);
        return Math.Abs(u) <= clip.Width / 2 && Math.Abs(v) <= clip.Height / 2;
    }

    static double Wrap(double degrees) {
        double wrapped = degrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }
}
