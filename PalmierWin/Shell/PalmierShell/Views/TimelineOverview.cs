using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PalmierShell.Core;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// Resolve-style overview strip across the top of the timeline: the whole
/// timeline at fit scale, with a translucent rectangle over the region the
/// main view is showing. It follows scroll and zoom; clicking or dragging
/// jumps the main view there.
public sealed class TimelineOverview : Control {
    static readonly SolidColorBrush ViewportBrush = new(Color.Parse("#2EFFFFFF"));
    static readonly Pen ViewportPen = new(new SolidColorBrush(Color.Parse("#A6FFFFFF")), 1);

    TimelineViewModel? vm;
    bool scrubbing;

    public TimelineOverview() {
        ClipToBounds = true;
        DataContextChanged += (_, _) => Attach(DataContext as TimelineViewModel);
        Accent.Changed += InvalidateVisual;
        DetachedFromVisualTree += (_, _) => Accent.Changed -= InvalidateVisual;
    }

    void Attach(TimelineViewModel? next) {
        if (vm is not null) {
            vm.StateReloaded -= InvalidateVisual;
            vm.PropertyChanged -= OnVmPropertyChanged;
        }
        vm = next;
        if (vm is not null) {
            vm.StateReloaded += InvalidateVisual;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
        InvalidateVisual();
    }

    void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        InvalidateVisual();

    static double StripLeft => TimelineView.HeaderWidth;

    public override void Render(DrawingContext ctx) {
        var bounds = Bounds;
        ctx.FillRectangle(new SolidColorBrush(TimelineView.RaisedColor), new Rect(bounds.Size));
        ctx.DrawLine(new Pen(new SolidColorBrush(TimelineView.BorderColor)),
            new Point(0, bounds.Height - 0.5), new Point(bounds.Width, bounds.Height - 0.5));
        if (vm?.State is not { } state) return;
        double stripWidth = Math.Max(0, bounds.Width - StripLeft);
        if (stripWidth <= 0) return;
        int total = Math.Max(1, vm.TotalFrames);
        double X(int frame) => StripLeft + frame * stripWidth / total;

        int rows = Math.Max(1, state.Tracks.Count);
        double rowHeight = (bounds.Height - 2) / rows;
        for (int i = 0; i < state.Tracks.Count; i++) {
            var track = state.Tracks[i];
            double y = 1 + i * rowHeight;
            foreach (var clip in track.Clips) {
                var color = track.Type == "audio" ? TimelineView.AudioClipColor
                    : clip.MediaType == "text" ? TimelineView.TextClipColor
                    : TimelineView.VideoClipColor;
                double x1 = X(clip.EndFrame);
                ctx.FillRectangle(new SolidColorBrush(color),
                    new Rect(X(clip.StartFrame), y + 0.5, Math.Max(1, x1 - X(clip.StartFrame)),
                             Math.Max(1, rowHeight - 1)));
            }
        }

        double px = X(vm.PlayheadFrame);
        ctx.DrawLine(new Pen(new SolidColorBrush(TimelineView.PlayheadColor), 1),
            new Point(px, 0), new Point(px, bounds.Height));

        var (vx, vw) = TimelineMath.OverviewViewport(
            vm.ScrollOffsetX, vm.PixelsPerFrame, vm.ViewportWidth, total, stripWidth);
        var viewport = new Rect(StripLeft + vx, 0.5, Math.Max(2, vw), bounds.Height - 1);
        ctx.FillRectangle(ViewportBrush, viewport);
        ctx.DrawRectangle(null, ViewportPen, viewport);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (vm is null) return;
        scrubbing = true;
        e.Pointer.Capture(this);
        JumpTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (!scrubbing) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) JumpTo(e.GetPosition(this).X);
        else scrubbing = false;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        scrubbing = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        scrubbing = false;
    }

    /// Centres the main view on the strip position under `x`.
    void JumpTo(double x) {
        if (vm is null || Bounds.Width <= StripLeft) return;
        double frac = Math.Clamp((x - StripLeft) / (Bounds.Width - StripLeft), 0, 1);
        int total = Math.Max(1, vm.TotalFrames);
        vm.ScrollOffsetX = TimelineMath.ScrollCentering(
            (int)Math.Round(frac * total), vm.PixelsPerFrame, vm.ViewportWidth, total);
    }
}
