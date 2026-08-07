using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PalmierShell.Core;

namespace PalmierShell.Views;

public enum CurveEditorMode { Grade, Hue }

public sealed class CurvePointsEventArgs : EventArgs {
    public CurvePointsEventArgs(IReadOnlyList<CurvePoint> points) => Points = points;
    public IReadOnlyList<CurvePoint> Points { get; }
}

/// The inspector's curve plot, grade or hue: a square unit plot whose channel
/// points the pointer grabs, drags, adds, and double-tap-removes. The gesture
/// math lives in CurveEditing; the eval that strokes the curve lives on the
/// Core models (a port of the engine's Swift eval, so the line matches the
/// rendered pixels).
///
/// A press grabs the nearest point inside the hit diameter or drops a new one
/// there; the drag fires Changed per move with the in-flight points and
/// Committed once on release, so one gesture is one undoable intent. A click
/// that never passes the drag threshold commits nothing.
public class CurveEditor : Control {
    public static readonly StyledProperty<CurveEditorMode> ModeProperty =
        AvaloniaProperty.Register<CurveEditor, CurveEditorMode>(nameof(Mode));
    public static readonly StyledProperty<int> ChannelProperty =
        AvaloniaProperty.Register<CurveEditor, int>(nameof(Channel));
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>> PointsProperty =
        AvaloniaProperty.Register<CurveEditor, IReadOnlyList<CurvePoint>>(
            nameof(Points), Array.Empty<CurvePoint>());

    static CurveEditor() =>
        AffectsRender<CurveEditor>(ModeProperty, ChannelProperty, PointsProperty);

    public CurveEditorMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public int Channel { get => GetValue(ChannelProperty); set => SetValue(ChannelProperty, value); }
    /// The committed points of the active channel; empty displays the
    /// identity diagonal (grade) or the six neutral defaults (hue).
    public IReadOnlyList<CurvePoint> Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    /// Live gesture updates during a drag (preview only — do not commit).
    public event EventHandler<CurvePointsEventArgs>? Changed;
    /// Gesture finished: pointer released, or a double-tap removal.
    public event EventHandler<CurvePointsEventArgs>? Committed;

    const double MinDragDistance = 3;   // macOS DragGesture(minimumDistance: 3)

    // The spectrum backdrop IS data (the hue axis), not chrome — hardcoded
    // like the wheel's hue disc. Alpha is macOS Opacity.medium.
    static readonly LinearGradientBrush SpectrumBrush = new() {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = {
            new GradientStop(Color.Parse("#59FF3B30"), 0),
            new GradientStop(Color.Parse("#59F2D933"), 1.0 / 6),
            new GradientStop(Color.Parse("#594DD959"), 2.0 / 6),
            new GradientStop(Color.Parse("#5933CCD9"), 3.0 / 6),
            new GradientStop(Color.Parse("#594080F2"), 4.0 / 6),
            new GradientStop(Color.Parse("#59CC59E6"), 5.0 / 6),
            new GradientStop(Color.Parse("#59FF3B30"), 1),
        },
    };

    IBrush? gridBrush;
    IBrush? borderBrush;
    IBrush? masterTint;
    IBrush? primaryTint;
    IBrush? redTint;
    IBrush? greenTint;
    IBrush? blueTint;
    double pointDiameter = 9;
    double hitRadius = 15;

    bool pressed;
    Point pressPosition;
    List<CurvePoint>? dragPoints;
    int dragIndex;

    public CurveEditor() => DoubleTapped += OnDoubleTapped;

    IReadOnlyList<CurvePoint> DisplayPoints {
        get {
            if (Points.Count == 0)
                return Mode == CurveEditorMode.Grade ? GradeCurve.IdentityPoints : HueCurves.DefaultPoints;
            return Points.OrderBy(p => p.X).ToList();
        }
    }

    /// Points to draw — the live in-flight drag if any, else the committed curve.
    IReadOnlyList<CurvePoint> ActivePoints => dragPoints ?? DisplayPoints;

    IBrush Tint => Mode == CurveEditorMode.Hue ? primaryTint! : Channel switch {
        1 => redTint!,
        2 => greenTint!,
        3 => blueTint!,
        _ => masterTint!,
    };

    void EnsureResources() {
        gridBrush ??= this.FindResource("CurveGridBrush") as IBrush ?? Brushes.Gray;
        borderBrush ??= this.FindResource("ThemeBorderBrush") as IBrush ?? Brushes.Gray;
        masterTint ??= this.FindResource("ThemeTextSecondaryBrush") as IBrush ?? Brushes.White;
        primaryTint ??= this.FindResource("ThemeTextBrush") as IBrush ?? Brushes.White;
        redTint ??= this.FindResource("CurveRedBrush") as IBrush ?? Brushes.Red;
        greenTint ??= this.FindResource("CurveGreenBrush") as IBrush ?? Brushes.Green;
        blueTint ??= this.FindResource("CurveBlueBrush") as IBrush ?? Brushes.Blue;
        if (this.FindResource("CurvePointDiameter") is double d) pointDiameter = d;
        if (this.FindResource("CurvePointHitDiameter") is double hd) hitRadius = hd / 2;
    }

    protected override Size MeasureOverride(Size availableSize) {
        double h = this.FindResource("CurveEditorHeight") is double v ? v : 180;
        return new Size(h, h);
    }

    public override void Render(DrawingContext context) {
        EnsureResources();
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);

        if (Mode == CurveEditorMode.Hue)
            context.DrawRectangle(SpectrumBrush, null, rect);

        // Grade: black · shadow · mid · highlight · white. Hue: one vertical
        // per spectrum stop.
        var gridPen = new Pen(gridBrush, 1);
        if (Mode == CurveEditorMode.Grade) {
            for (int i = 0; i <= 4; i++) {
                double s = i * 0.25;
                context.DrawLine(gridPen, new Point(s * w, 0), new Point(s * w, h));
                context.DrawLine(gridPen, new Point(0, s * h), new Point(w, s * h));
            }
        } else {
            for (int i = 0; i <= 6; i++) {
                double x = i / 6.0 * w;
                context.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }
        }

        var dashed = new Pen(borderBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
        if (Mode == CurveEditorMode.Grade)
            context.DrawLine(dashed, new Point(0, h), new Point(w, 0));
        else
            context.DrawLine(dashed, new Point(0, h / 2), new Point(w, h / 2));
        context.DrawRectangle(null, new Pen(borderBrush, 1), rect);

        // The curve stroke: piecewise-linear eval sampled across the plot.
        var points = ActivePoints;
        double step = Mode == CurveEditorMode.Grade ? 0.02 : 0.01;
        var geometry = new StreamGeometry();
        using (var gc = geometry.Open()) {
            bool first = true;
            for (double x = 0; x <= 1 + 1e-9; x += step) {
                double cx = Math.Min(x, 1);
                double y = Mode == CurveEditorMode.Grade
                    ? GradeCurve.Eval(points, cx)
                    : HueCurves.Eval(points, cx);
                var p = new Point(cx * w, (1 - y) * h);
                if (first) {
                    gc.BeginFigure(p, false);
                    first = false;
                } else {
                    gc.LineTo(p);
                }
            }
            gc.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Tint, 1.5), geometry);

        double radius = pointDiameter / 2;
        foreach (var p in points) {
            var (sx, sy) = CurveEditing.ToScreen(p, w, h);
            context.DrawEllipse(Tint, null, new Point(sx, sy), radius, radius);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        pressed = true;
        pressPosition = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (!pressed) return;
        var p = e.GetPosition(this);
        if (dragPoints is null) {
            double dx = p.X - pressPosition.X, dy = p.Y - pressPosition.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < MinDragDistance) return;
            (dragPoints, dragIndex) = CurveEditing.Grab(DisplayPoints,
                pressPosition.X, pressPosition.Y, Bounds.Width, Bounds.Height, hitRadius);
        }
        dragPoints = CurveEditing.Move(dragPoints, dragIndex, p.X, p.Y, Bounds.Width, Bounds.Height);
        InvalidateVisual();
        Changed?.Invoke(this, new CurvePointsEventArgs(Normalize(dragPoints)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (!pressed) return;
        pressed = false;
        // Capture(null) raises CaptureLost synchronously, which clears the
        // drag state — snapshot it first or the commit would be lost.
        var inFlight = dragPoints;
        dragPoints = null;
        e.Pointer.Capture(null);
        if (inFlight is null) return;   // a click without a drag changes nothing
        var p = e.GetPosition(this);
        var final = CurveEditing.Move(inFlight, dragIndex, p.X, p.Y, Bounds.Width, Bounds.Height);
        InvalidateVisual();
        Committed?.Invoke(this, new CurvePointsEventArgs(Normalize(final)));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        pressed = false;
        dragPoints = null;
        InvalidateVisual();
    }

    void OnDoubleTapped(object? sender, TappedEventArgs e) {
        var p = e.GetPosition(this);
        var remaining = CurveEditing.RemoveNearest(
            DisplayPoints, p.X, p.Y, Bounds.Width, Bounds.Height, hitRadius);
        if (remaining is null) return;
        Committed?.Invoke(this, new CurvePointsEventArgs(Normalize(remaining)));
        e.Handled = true;
    }

    /// What the gesture emits: the neutral shape stores as an empty channel.
    IReadOnlyList<CurvePoint> Normalize(IReadOnlyList<CurvePoint> points) =>
        Mode == CurveEditorMode.Grade
            ? GradeCurve.NormalizeChannel(points)
            : HueCurves.NormalizeChannel(points);
}
