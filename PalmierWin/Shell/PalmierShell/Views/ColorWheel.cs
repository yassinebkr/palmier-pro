using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PalmierShell.Views;

/// A Resolve-style color wheel: a hue-saturation disc with a draggable handle
/// (center = neutral) and a thin luma slider beside it. Gesture state is the
/// unit-disc vector (WheelX, WheelY) plus Offset ∈ [-1, 1] from the slider;
/// ColorWheelMath maps these onto kernel params. Dragging fires Changed per
/// move and Committed once on release, so a drag is one undoable intent.
/// Double-tap resets to neutral and commits.
///
/// Hue layout: red sits at 3 o'clock so the hue under the handle always
/// matches atan2(handle). The disc is a conic hue gradient under a radial
/// white→transparent wash, which pulls the desaturated center to white.
public class ColorWheel : Control {
    public static readonly StyledProperty<double> WheelXProperty =
        AvaloniaProperty.Register<ColorWheel, double>(nameof(WheelX));
    public static readonly StyledProperty<double> WheelYProperty =
        AvaloniaProperty.Register<ColorWheel, double>(nameof(WheelY));
    public static readonly StyledProperty<double> OffsetProperty =
        AvaloniaProperty.Register<ColorWheel, double>(nameof(Offset));

    static ColorWheel() =>
        AffectsRender<ColorWheel>(WheelXProperty, WheelYProperty, OffsetProperty);

    public double WheelX { get => GetValue(WheelXProperty); set => SetValue(WheelXProperty, value); }
    public double WheelY { get => GetValue(WheelYProperty); set => SetValue(WheelYProperty, value); }
    public double Offset { get => GetValue(OffsetProperty); set => SetValue(OffsetProperty, value); }

    /// Live gesture updates during a drag (readouts only — do not commit).
    public event EventHandler? Changed;
    /// Gesture finished: pointer released, or a double-tap reset.
    public event EventHandler? Committed;

    const double SliderWidth = 10;
    const double SliderGap = 8;
    const double HandleRadius = 5;

    // Content colours: the disc's hues ARE the data, not chrome.
    // Angle -90 puts red at 3 o'clock, matching atan2(WheelY, WheelX).
    static readonly ConicGradientBrush HueBrush = new() {
        Angle = -90,
        GradientStops = {
            new GradientStop(Color.Parse("#FF0000"), 0),
            new GradientStop(Color.Parse("#FFFF00"), 1.0 / 6),
            new GradientStop(Color.Parse("#00FF00"), 2.0 / 6),
            new GradientStop(Color.Parse("#00FFFF"), 3.0 / 6),
            new GradientStop(Color.Parse("#0000FF"), 4.0 / 6),
            new GradientStop(Color.Parse("#FF00FF"), 5.0 / 6),
            new GradientStop(Color.Parse("#FF0000"), 1),
        },
    };
    static readonly RadialGradientBrush SaturationWash = new() {
        GradientStops = {
            new GradientStop(Color.Parse("#FFFFFFFF"), 0),
            new GradientStop(Color.Parse("#00FFFFFF"), 1),
        },
    };
    static readonly LinearGradientBrush LumaBrush = new() {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = {
            new GradientStop(Colors.White, 0),
            new GradientStop(Colors.Black, 1),
        },
    };

    IBrush? ringBrush;
    IBrush? handleBrush;
    IBrush? baseBrush;

    enum DragMode { None, Disc, Slider }
    DragMode dragging = DragMode.None;

    public ColorWheel() => DoubleTapped += OnDoubleTapped;

    void OnDoubleTapped(object? sender, TappedEventArgs e) {
        WheelX = 0;
        WheelY = 0;
        Offset = 0;
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    Rect DiscRect => new(0, 0, Math.Min(Bounds.Height, Bounds.Width - SliderWidth - SliderGap),
                                Math.Min(Bounds.Height, Bounds.Width - SliderWidth - SliderGap));
    Rect SliderRect => new(DiscRect.Width + SliderGap, 0, SliderWidth, DiscRect.Height);

    protected override Size MeasureOverride(Size availableSize) =>
        new(96 + SliderWidth + SliderGap, 96);

    public override void Render(DrawingContext context) {
        ringBrush ??= this.FindResource("ThemeBorderBrush") as IBrush ?? Brushes.Gray;
        handleBrush ??= this.FindResource("ThemeTextBrush") as IBrush ?? Brushes.White;

        var disc = DiscRect;
        double radius = disc.Width / 2;
        var center = new Point(radius, radius);
        context.DrawEllipse(HueBrush, null, center, radius, radius);
        context.DrawEllipse(SaturationWash, new Pen(ringBrush, 1), center, radius, radius);

        double hx = center.X + WheelX * (radius - HandleRadius);
        double hy = center.Y + WheelY * (radius - HandleRadius);
        var handle = new Point(hx, hy);
        baseBrush ??= this.FindResource("ThemeBaseBrush") as IBrush ?? Brushes.Black;
        // Dark under-ring keeps the handle visible on the white centre.
        context.DrawEllipse(Brushes.Transparent, new Pen(baseBrush, 3), handle, HandleRadius, HandleRadius);
        context.DrawEllipse(Brushes.Transparent, new Pen(handleBrush, 1.5), handle, HandleRadius, HandleRadius);

        var slider = SliderRect;
        var track = new RoundedRect(slider, new CornerRadius(2));
        context.DrawRectangle(LumaBrush, new Pen(ringBrush, 1), track);
        double ty = slider.Y + (1 - (Offset + 1) / 2) * slider.Height;
        context.DrawRectangle(handleBrush, null,
            new RoundedRect(new Rect(slider.X - 2, ty - 1.5, slider.Width + 4, 3), new CornerRadius(1.5)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var disc = DiscRect;
        var center = new Point(disc.Width / 2, disc.Height / 2);
        var delta = new Vector(p.X - center.X, p.Y - center.Y);
        if (delta.Length <= disc.Width / 2) dragging = DragMode.Disc;
        else if (SliderRect.Contains(p)) dragging = DragMode.Slider;
        if (dragging == DragMode.None) return;
        e.Pointer.Capture(this);
        UpdateFrom(p);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (dragging != DragMode.None) UpdateFrom(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (dragging == DragMode.None) return;
        dragging = DragMode.None;
        e.Pointer.Capture(null);
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        dragging = DragMode.None;
    }

    void UpdateFrom(Point p) {
        if (dragging == DragMode.Disc) {
            var disc = DiscRect;
            double radius = disc.Width / 2;
            var v = new Vector((p.X - radius) / radius, (p.Y - radius) / radius);
            if (v.Length > 1) v = v.Normalize();
            WheelX = v.X;
            WheelY = v.Y;
        } else {
            var slider = SliderRect;
            Offset = Math.Clamp(1 - 2 * (p.Y - slider.Y) / slider.Height, -1, 1);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
