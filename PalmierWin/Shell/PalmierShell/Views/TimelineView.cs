using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using PalmierShell.Core;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// Custom-drawn timeline: ruler, track headers, filmstrip clips, playhead.
/// Renders straight from the view model's TimelineState snapshot; all input
/// (scrub, select, blade, zoom, scroll) is handled here.
public sealed class TimelineView : Control {
    const double RulerHeight = 24;
    /// Row height follows the view model's density setting: upstream fits
    /// roughly eight tracks where the roomy default fits three.
    double TrackHeight => vm?.CompactRows == true ? 28 : 50;
    const double HeaderWidth = 100;
    const double ClipCornerRadius = 4;
    const double MinPixelsPerFrame = 0.5;
    const double MaxPixelsPerFrame = 16;

    static readonly Color SurfaceColor = Color.Parse("#161616");
    static readonly Color RaisedColor = Color.Parse("#1E1E1E");
    static readonly Color BorderColor = Color.Parse("#29FFFFFF");
    static readonly Color TimecodeColor = Color.Parse("#F29933");
    static readonly Color PlayheadColor = Color.Parse("#E54F4F");
    static readonly Color VideoClipColor = Color.Parse("#1D5878");
    static readonly Color AudioClipColor = Color.Parse("#2E7765");
    static readonly Color TextClipColor = Color.Parse("#715486");

    static readonly Typeface LabelTypeface = new("Inter");

    TimelineViewModel? vm;
    bool scrubbing;

    // Clip drag-move state (Select tool). Committed on release as one intent.
    string? dragClipId;
    string? dragLinkGroupId;
    int dragOriginalStart;
    double dragStartX;
    int dragDeltaFrames;
    int dragMinDelta, dragMaxDelta;
    bool dragActive;
    /// Track the drag started on, and the one under the pointer now. A clip
    /// can only land on a track of its own kind.
    string? dragOriginalTrackId;
    string? dragTargetTrackId;

    // Edge-trim drag state (Select tool, grabbed within EdgeGrabWidth px).
    const double EdgeGrabWidth = 6;
    string? trimClipId;
    int trimEdge = -1;              // 0 = left, 1 = right
    int trimBoundary;
    int trimMinBoundary, trimMaxBoundary;
    bool trimActive;

    // Roll state: grabbing the cut between two touching clips moves both
    // edges. Alt falls back to trimming only the clip under the pointer.
    string? rollLeftId, rollRightId;
    int rollBoundary;
    int rollMinBoundary, rollMaxBoundary;
    bool rollActive;

    /// Allowed drag interval so the ghost previews the core's clamp: the
    /// moved group stays inside the gap between its non-moved neighbors.
    void ComputeDragBounds(ClipState grabbed) {
        dragMinDelta = int.MinValue;
        dragMaxDelta = int.MaxValue;
        if (vm?.State is not { } state) return;
        bool IsMoved(ClipState c) => c.Id == grabbed.Id ||
            (grabbed.LinkGroupId is not null && c.LinkGroupId == grabbed.LinkGroupId);
        foreach (var track in state.Tracks) {
            var moved = track.Clips.Where(IsMoved).ToList();
            if (moved.Count == 0) continue;
            var others = track.Clips.Where(c => !IsMoved(c)).ToList();
            foreach (var c in moved) {
                dragMinDelta = Math.Max(dragMinDelta, -c.StartFrame);
                foreach (var other in others) {
                    if (other.EndFrame <= c.StartFrame)
                        dragMinDelta = Math.Max(dragMinDelta, other.EndFrame - c.StartFrame);
                    else if (other.StartFrame >= c.EndFrame)
                        dragMaxDelta = Math.Min(dragMaxDelta, other.StartFrame - c.EndFrame);
                }
            }
        }
    }

    public TimelineView() {
        ClipToBounds = true;
        Focusable = true;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as TimelineViewModel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    void OnDragOver(object? sender, DragEventArgs e) {
        e.DragEffects = e.Data.Contains(MediaPanel.MediaPathFormat)
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDrop(object? sender, DragEventArgs e) {
        if (vm is null || e.Data.Get(MediaPanel.MediaPathFormat) is not string path) return;
        int frame = Math.Max(0, vm.Snap(XToFrame(e.GetPosition(this).X), vm.PixelsPerFrame));
        vm.RequestMediaDrop(path, frame);
        e.Handled = true;
    }

    // Visual feedback: amber guide when a drag/trim snapped; blade cut preview.
    int? snapGuideFrame;
    int? bladeHoverFrame;

    void AttachViewModel(TimelineViewModel? next) {
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

    double FrameToX(int frame) => HeaderWidth + frame * vm!.PixelsPerFrame - vm.ScrollOffsetX;
    int XToFrame(double x) => (int)Math.Round((x - HeaderWidth + vm!.ScrollOffsetX) / vm.PixelsPerFrame);

    public override void Render(DrawingContext ctx) {
        var bounds = Bounds;
        ctx.FillRectangle(new SolidColorBrush(SurfaceColor), new Rect(bounds.Size));
        if (vm?.State is not { } state) return;

        RenderRuler(ctx, bounds);

        double y = RulerHeight;
        foreach (var track in state.Tracks) {
            RenderTrack(ctx, bounds, track, LabelFor(state, track), y);
            y += TrackHeight;
        }

        // The marked range: a wash over the tracks plus in/out brackets on the
        // ruler, like upstream's range selection.
        if (vm.RangeStart is not null || vm.RangeEnd is not null) {
            double x0 = vm.RangeStart is { } risIn ? FrameToX(risIn) : HeaderWidth;
            double x1 = vm.RangeEnd is { } rout ? FrameToX(rout) : bounds.Width;
            if (vm.HasRange && x1 > Math.Max(HeaderWidth, x0)) {
                double left = Math.Max(HeaderWidth, x0);
                ctx.FillRectangle(RangeWashBrush, new Rect(left, 0, x1 - left, bounds.Height));
            }
            var bracket = new Pen(new SolidColorBrush(TimecodeColor), 2);
            if (vm.RangeStart is not null && x0 >= HeaderWidth) {
                ctx.DrawLine(bracket, new Point(x0, 0), new Point(x0, RulerHeight));
                ctx.DrawLine(bracket, new Point(x0, 2), new Point(x0 + 5, 2));
            }
            if (vm.RangeEnd is not null && x1 >= HeaderWidth) {
                ctx.DrawLine(bracket, new Point(x1, 0), new Point(x1, RulerHeight));
                ctx.DrawLine(bracket, new Point(x1 - 5, 2), new Point(x1, 2));
            }
        }

        if (snapGuideFrame is { } snapFrame) {
            double sx = FrameToX(snapFrame);
            if (sx >= HeaderWidth)
                ctx.DrawLine(new Pen(new SolidColorBrush(TimecodeColor), 1),
                    new Point(sx, RulerHeight), new Point(sx, bounds.Height));
        }
        if (vm.Tool == TimelineTool.Blade && bladeHoverFrame is { } bladeFrame) {
            double bx = FrameToX(bladeFrame);
            if (bx >= HeaderWidth)
                ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#CCFFFFFF")), 1),
                    new Point(bx, RulerHeight), new Point(bx, bounds.Height));
        }

        RenderPlayhead(ctx, bounds);
    }

    void RenderRuler(DrawingContext ctx, Rect bounds) {
        ctx.FillRectangle(new SolidColorBrush(RaisedColor),
            new Rect(0, 0, bounds.Width, RulerHeight));

        double ppf = vm!.PixelsPerFrame;
        // Major tick every second; thin out labels when zoomed far out.
        int framesPerMajor = TimelineViewModel.TimelineFps;
        int labelEvery = ppf * framesPerMajor >= 60 ? 1 : ppf * framesPerMajor >= 24 ? 5 : 10;

        var tickPen = new Pen(new SolidColorBrush(BorderColor));
        int firstFrame = Math.Max(0, XToFrame(HeaderWidth));
        int lastFrame = XToFrame(bounds.Width) + framesPerMajor;
        for (int f = firstFrame - firstFrame % framesPerMajor; f <= lastFrame; f += framesPerMajor) {
            double x = FrameToX(f);
            if (x < HeaderWidth) continue;
            int second = f / framesPerMajor;
            bool labeled = second % labelEvery == 0;
            ctx.DrawLine(tickPen, new Point(x, labeled ? 6 : 14), new Point(x, RulerHeight));
            if (labeled) {
                var text = new FormattedText(FormatTimecode(f), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelTypeface, 10,
                    new SolidColorBrush(Color.Parse("#9EFFFFFF")));
                ctx.DrawText(text, new Point(x + 3, 4));
            }
        }
    }

    void RenderTrack(DrawingContext ctx, Rect bounds, TrackState track, string label, double y) {
        var rowRect = new Rect(0, y, bounds.Width, TrackHeight);
        ctx.FillRectangle(new SolidColorBrush(SurfaceColor), rowRect);
        ctx.DrawLine(new Pen(new SolidColorBrush(BorderColor)),
            new Point(0, y + TrackHeight), new Point(bounds.Width, y + TrackHeight));

        // Header: name, link chain, then the eye/mute toggle — one row, as upstream.
        ctx.FillRectangle(new SolidColorBrush(RaisedColor), new Rect(0, y, HeaderWidth, TrackHeight));
        var name = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelTypeface, 12, new SolidColorBrush(Colors.White)) {
            // A renamed track takes the sync glyph's space rather than running
            // under the eye; the derived V1/A1 labels never need it.
            MaxTextWidth = track.Name is { Length: > 0 } ? 46 : 28,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        double rowMid = y + TrackHeight / 2;
        ctx.DrawText(name, new Point(12, rowMid - name.Height / 2));
        if (track.Name is not { Length: > 0 }) RenderLinkIcon(ctx, new Rect(44, rowMid - 5, 12, 10));
        var iconRect = new Rect(66, rowMid - 6, 14, 12);
        if (track.Type == "audio")
            RenderSpeakerIcon(ctx, iconRect, off: track.Muted);
        else
            RenderEyeIcon(ctx, iconRect, off: track.Hidden);

        using var clipRegion = ctx.PushClip(new Rect(HeaderWidth, y, bounds.Width - HeaderWidth, TrackHeight));
        foreach (var clip in track.Clips) {
            // A clip being dragged to another track draws there, not here.
            if (dragActive && clip.Id == dragClipId && dragTargetTrackId != track.Id) continue;
            RenderClip(ctx, track, clip, y);
        }
        if (dragActive && dragTargetTrackId == track.Id && dragClipId is { } moving &&
            track.Clips.All(c => c.Id != moving) && vm?.State?.FindClip(moving) is { } incoming)
            RenderClip(ctx, track, incoming, y);
    }

    void RenderClip(DrawingContext ctx, TrackState track, ClipState clip, double y) {
        int visualStart = clip.StartFrame;
        int visualDuration = clip.DurationFrames;
        if (dragActive && (clip.Id == dragClipId ||
            (dragLinkGroupId is not null && clip.LinkGroupId == dragLinkGroupId)))
            visualStart = Math.Max(0, clip.StartFrame + dragDeltaFrames);
        if (trimActive && clip.Id == trimClipId) {
            if (trimEdge == 0) {
                visualStart = trimBoundary;
                visualDuration = clip.EndFrame - trimBoundary;
            } else {
                visualDuration = trimBoundary - clip.StartFrame;
            }
        }
        // Roll preview: both sides of the cut move together.
        if (rollActive) {
            if (clip.Id == rollLeftId) {
                visualDuration = rollBoundary - clip.StartFrame;
            } else if (clip.Id == rollRightId) {
                visualStart = rollBoundary;
                visualDuration = clip.EndFrame - rollBoundary;
            }
        }
        double x = FrameToX(visualStart);
        double w = visualDuration * vm!.PixelsPerFrame;
        if (x + w < HeaderWidth || x > Bounds.Width) return;
        var rect = new Rect(x, y + ClipPadY, Math.Max(2, w), TrackHeight - ClipPadY * 2);

        var fill = track.Type == "audio" ? AudioClipColor
                 : clip.MediaType == "text" ? TextClipColor
                 : VideoClipColor;
        ctx.DrawRectangle(new SolidColorBrush(fill), null, rect, ClipCornerRadius, ClipCornerRadius);

        if (track.Type == "video" && clip.MediaType == "video" &&
            vm.FilmstripFor(clip.MediaRef) is { Length: > 0 } strip) {
            using var clipClip = ctx.PushClip(rect);
            double tileH = rect.Height;
            double tileW = tileH * CoreApi.ThumbTileWidth / CoreApi.ThumbTileHeight;
            int tileCount = Math.Max(1, (int)Math.Ceiling(rect.Width / tileW));
            // Window the strip to the clip's slice of the media, or each half
            // of a cut shows the whole movie squeezed into its own width.
            int sourceTotal = vm.CachedSourceFrames(clip.MediaRef) ?? 0;
            for (int i = 0; i < tileCount; i++) {
                var tile = strip[FilmstripWindow.TileIndex(
                    i, tileCount, strip.Length,
                    clip.TrimStartFrame, clip.SourceFramesConsumed, sourceTotal)];
                ctx.DrawImage(tile, new Rect(rect.X + i * tileW, rect.Y, tileW, tileH));
            }
        }

        if (clip.MediaType == "audio" && vm.WaveformFor(clip.MediaRef) is { } waveform) {
            RenderWaveform(ctx, rect, waveform, clip);
            RenderVolumeEnvelope(ctx, rect, clip);
        }

        // Linked audio rides as a slim waveform strip under the filmstrip,
        // matching upstream's video-clip look.
        if (track.Type == "video" && clip.LinkGroupId is not null &&
            vm.WaveformFor(clip.MediaRef) is { } linkedWave) {
            var audioStrip = new Rect(rect.X, rect.Bottom - 11, rect.Width, 11);
            ctx.DrawRectangle(new SolidColorBrush(AudioClipColor), null, audioStrip);
            RenderWaveform(ctx, audioStrip, linkedWave, clip);
        }

        var nameText = new FormattedText(NameFor(clip), System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, LabelTypeface, 10, new SolidColorBrush(Colors.White));
        using (ctx.PushClip(rect)) {
            // Backing strip keeps the name legible over bright filmstrip frames.
            ctx.DrawRectangle(ClipLabelBrush, null,
                new Rect(rect.X, rect.Y, Math.Min(rect.Width, nameText.Width + 10), 15),
                ClipCornerRadius, ClipCornerRadius);
            ctx.DrawText(nameText, new Point(rect.X + 5, rect.Y + 2));
        }

        RenderFades(ctx, rect, clip);

        // Animated text clips get the upstream diagonal-stripe pattern.
        if (clip.MediaType == "text" && clip.HasKeyframes) {
            using var stripeClip = ctx.PushClip(rect);
            var stripePen = new Pen(new SolidColorBrush(Color.Parse("#2EFFFFFF")), 1);
            for (double sx = rect.X - rect.Height; sx < rect.Right; sx += 7)
                ctx.DrawLine(stripePen, new Point(sx, rect.Bottom), new Point(sx + rect.Height, rect.Y));
        }

        // Keyframe markers on the selected clip: diamonds along the bottom.
        if (clip.Id == vm.SelectedClipId && clip.HasKeyframes) {
            var kfBrush = new SolidColorBrush(TimecodeColor);
            foreach (int kf in clip.KeyframeFrames) {
                double kx = FrameToX(visualStart + kf);
                if (kx < rect.X || kx > rect.Right) continue;
                double ky = rect.Bottom - 6;
                ctx.DrawGeometry(kfBrush, null, new PolylineGeometry(new[] {
                    new Point(kx, ky - 3.5), new Point(kx + 3.5, ky),
                    new Point(kx, ky + 3.5), new Point(kx - 3.5, ky),
                }, true));
            }
        }

        // The primary selection gets the full-strength ring; the rest of a
        // multi-selection is marked more quietly.
        if (vm.IsSelected(clip.Id)) {
            var stroke = clip.Id == vm.SelectedClipId
                ? new SolidColorBrush(Colors.White)
                : new SolidColorBrush(Color.Parse("#8CFFFFFF"));
            ctx.DrawRectangle(null, new Pen(stroke, 1.5), rect, ClipCornerRadius, ClipCornerRadius);
        }
    }

    static readonly SolidColorBrush FadeShadeBrush = new(Color.Parse("#59000000"));
    static readonly SolidColorBrush RangeWashBrush = new(Color.Parse("#1FFFFFFF"));

    /// Fade ramps render as shaded wedges from the clip corner to the ramp end.
    // MARK: On-clip audio controls

    /// The linear-gain range the volume line spans: silence at the clip's
    /// bottom edge, 2x at the top, unity in the middle. Matches the mixer's
    /// linear domain; dB conversion happens only at the ABI boundary.
    internal const double MaxEnvelopeGain = 2.0;

    /// Volume the line is showing: the drag preview while one is live,
    /// otherwise the committed value.
    double EnvelopeGain(ClipState clip) =>
        envelopeClipId == clip.Id && envelopeActive ? envelopeGainPreview : clip.Volume;

    internal static double EnvelopeY(Rect rect, double gain) =>
        rect.Bottom - Math.Clamp(gain / MaxEnvelopeGain, 0, 1) * rect.Height;

    internal static double GainAtY(Rect rect, double y) =>
        Math.Clamp((rect.Bottom - y) / rect.Height, 0, 1) * MaxEnvelopeGain;

    /// The horizontal volume line with grab dots at the fade boundaries.
    /// Dragging the line changes the clip's gain; dragging a dot moves that
    /// fade's length — audio adjustment without a trip to the inspector.
    void RenderVolumeEnvelope(DrawingContext ctx, Rect rect, ClipState clip) {
        if (rect.Width < 24 || rect.Height < 20) return;
        using var clipRegion = ctx.PushClip(rect);
        double gain = EnvelopeGain(clip);
        double y = EnvelopeY(rect, gain);
        double fadeInX = rect.X + FadePreview(clip, edgeIn: true) * vm!.PixelsPerFrame;
        double fadeOutX = rect.Right - FadePreview(clip, edgeIn: false) * vm.PixelsPerFrame;

        var pen = new Pen(EnvelopeBrush, 1.4);
        // Fade ramps rise from the clip's corners to the volume line.
        ctx.DrawLine(pen, new Point(rect.X, rect.Bottom), new Point(fadeInX, y));
        ctx.DrawLine(pen, new Point(fadeInX, y), new Point(fadeOutX, y));
        ctx.DrawLine(pen, new Point(fadeOutX, y), new Point(rect.Right, rect.Bottom));
        ctx.DrawEllipse(EnvelopeBrush, null, new Point(fadeInX, y), 3.5, 3.5);
        ctx.DrawEllipse(EnvelopeBrush, null, new Point(fadeOutX, y), 3.5, 3.5);

        if (envelopeClipId == clip.Id && envelopeActive) {
            double db = gain <= 0.001 ? -60 : 20 * Math.Log10(gain);
            var label = new FormattedText($"{db:+0.0;-0.0} dB",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, 10, EnvelopeBrush);
            ctx.DrawText(label, new Point(rect.X + 6, Math.Max(rect.Y, y - 14)));
        }
    }

    static readonly SolidColorBrush EnvelopeBrush = new(Color.Parse("#E8F2F2F2"));

    /// Fade length in frames the envelope is showing (drag preview or committed).
    int FadePreview(ClipState clip, bool edgeIn) {
        if (fadeClipId == clip.Id && fadeActive && fadeIsIn == edgeIn) return fadeFramesPreview;
        return edgeIn ? clip.FadeInFrames : clip.FadeOutFrames;
    }

    // Volume-line drag state (preview only; committed on release).
    string? envelopeClipId;
    bool envelopeActive;
    double envelopeGainPreview;

    // Fade-dot drag state.
    string? fadeClipId;
    bool fadeActive;
    bool fadeIsIn;
    int fadeFramesPreview;

    /// Hit-tests the audio envelope of the audio clip under `p`. Returns the
    /// gesture it arms, or null when the pointer is not on a control.
    (string ClipId, bool OnFadeDot, bool FadeIsIn)? EnvelopeHit(Point p) {
        if (vm?.State is null || vm.Tool != TimelineTool.Select) return null;
        if (HitTestClip(p) is not { } hit || hit.Track.Type != "audio" || hit.Clip.MediaType != "audio")
            return null;
        var rect = ClipRect(hit.Track, hit.Clip);
        if (rect.Width < 24 || rect.Height < 20) return null;
        double y = EnvelopeY(rect, hit.Clip.Volume);
        double fadeInX = rect.X + hit.Clip.FadeInFrames * vm.PixelsPerFrame;
        double fadeOutX = rect.Right - hit.Clip.FadeOutFrames * vm.PixelsPerFrame;
        if (Math.Abs(p.X - fadeInX) <= 6 && Math.Abs(p.Y - y) <= 6)
            return (hit.Clip.Id, true, true);
        if (Math.Abs(p.X - fadeOutX) <= 6 && Math.Abs(p.Y - y) <= 6)
            return (hit.Clip.Id, true, false);
        // The line between the dots adjusts gain.
        if (p.Y >= y - 4 && p.Y <= y + 4 && p.X >= fadeInX && p.X <= fadeOutX)
            return (hit.Clip.Id, false, false);
        return null;
    }

    void RenderFades(DrawingContext ctx, Rect rect, ClipState clip) {
        using var clipRegion = ctx.PushClip(rect);
        if (clip.FadeInFrames > 0) {
            double fw = Math.Min(rect.Width, clip.FadeInFrames * vm!.PixelsPerFrame);
            ctx.DrawGeometry(FadeShadeBrush, null, new PolylineGeometry(new[] {
                new Point(rect.X, rect.Bottom), new Point(rect.X, rect.Y),
                new Point(rect.X + fw, rect.Y),
            }, true));
        }
        if (clip.FadeOutFrames > 0) {
            double fw = Math.Min(rect.Width, clip.FadeOutFrames * vm!.PixelsPerFrame);
            ctx.DrawGeometry(FadeShadeBrush, null, new PolylineGeometry(new[] {
                new Point(rect.Right, rect.Bottom), new Point(rect.Right, rect.Y),
                new Point(rect.Right - fw, rect.Y),
            }, true));
        }
    }

    static readonly SolidColorBrush IconOnBrush = new(Color.Parse("#9EFFFFFF"));
    static readonly SolidColorBrush IconOffBrush = new(Color.Parse("#57FFFFFF"));
    static readonly SolidColorBrush ClipLabelBrush = new(Color.Parse("#73000000"));

    /// Two overlapping rings — the chain glyph upstream uses for sync lock.
    void RenderLinkIcon(DrawingContext ctx, Rect r) {
        var pen = new Pen(IconOffBrush, 1.1);
        ctx.DrawEllipse(null, pen, new Point(r.X + 3.4, r.Center.Y), 3.4, 2.6);
        ctx.DrawEllipse(null, pen, new Point(r.Right - 3.4, r.Center.Y), 3.4, 2.6);
    }

    void RenderEyeIcon(DrawingContext ctx, Rect r, bool off) {
        var brush = off ? IconOffBrush : IconOnBrush;
        var pen = new Pen(brush, 1.2);
        var mid = r.Center.Y;
        // Almond outline as two arcs approximated by an ellipse, plus pupil.
        ctx.DrawEllipse(null, pen, new Point(r.Center.X, mid), r.Width / 2, r.Height / 2.4);
        ctx.DrawEllipse(brush, null, new Point(r.Center.X, mid), 1.8, 1.8);
        if (off)
            ctx.DrawLine(pen, new Point(r.X - 1, r.Bottom + 1), new Point(r.Right + 1, r.Y - 1));
    }

    void RenderSpeakerIcon(DrawingContext ctx, Rect r, bool off) {
        var brush = off ? IconOffBrush : IconOnBrush;
        var pen = new Pen(brush, 1.2);
        double mid = r.Center.Y;
        // Body: small rect + cone.
        var body = new PolylineGeometry(new[] {
            new Point(r.X, mid - 2.5), new Point(r.X + 3.5, mid - 2.5),
            new Point(r.X + 8, mid - 5.5), new Point(r.X + 8, mid + 5.5),
            new Point(r.X + 3.5, mid + 2.5), new Point(r.X, mid + 2.5),
        }, true);
        ctx.DrawGeometry(brush, null, body);
        if (off) {
            ctx.DrawLine(pen, new Point(r.X - 1, r.Bottom + 1), new Point(r.Right + 1, r.Y - 1));
        } else {
            // Sound arc.
            ctx.DrawLine(pen, new Point(r.X + 10.5, mid - 3), new Point(r.X + 12, mid));
            ctx.DrawLine(pen, new Point(r.X + 12, mid), new Point(r.X + 10.5, mid + 3));
        }
    }

    static readonly SolidColorBrush WaveformBrush = new(Color.Parse("#9EFFFFFF"));

    /// Draws min/max column pairs across the rect, windowed to the clip's
    /// trimmed source range so a trimmed/split clip shows its actual audio.
    void RenderWaveform(DrawingContext ctx, Rect rect, TimelineViewModel.WaveformData waveform, ClipState clip) {
        using var clipRegion = ctx.PushClip(rect);
        int columns = waveform.MinMax.Length / 2;
        double windowStart = 0, windowEnd = 1;
        if (waveform.SourceFrames > 0) {
            windowStart = Math.Clamp((double)clip.TrimStartFrame / waveform.SourceFrames, 0, 1);
            windowEnd = Math.Clamp((double)(clip.TrimStartFrame + clip.SourceFramesConsumed) / waveform.SourceFrames,
                                   windowStart, 1);
        }
        double midY = rect.Y + rect.Height / 2;
        double halfH = rect.Height / 2 - 1;
        var pen = new Pen(WaveformBrush);
        int steps = Math.Max(1, (int)rect.Width);
        for (int px = 0; px < steps; px++) {
            double frac = windowStart + (windowEnd - windowStart) * px / steps;
            int col = Math.Min((int)(frac * columns), columns - 1);
            double lo = Math.Clamp(waveform.MinMax[col * 2] * waveform.Gain, -1, 1);
            double hi = Math.Clamp(waveform.MinMax[col * 2 + 1] * waveform.Gain, -1, 1);
            double x = rect.X + px + 0.5;
            ctx.DrawLine(pen, new Point(x, midY - hi * halfH), new Point(x, midY - lo * halfH));
        }
    }

    void RenderPlayhead(DrawingContext ctx, Rect bounds) {
        double x = FrameToX(vm!.PlayheadFrame);
        if (x < HeaderWidth) return;
        var brush = new SolidColorBrush(PlayheadColor);
        ctx.DrawLine(new Pen(brush, 1.5), new Point(x, 0), new Point(x, bounds.Height));
        var triangle = new PolylineGeometry(new[] {
            new Point(x - 5, 0), new Point(x + 5, 0), new Point(x, 8),
        }, true);
        ctx.DrawGeometry(brush, null, triangle);
    }

    static string NameFor(ClipState clip) =>
        clip.MediaType == "text"
            ? (string.IsNullOrEmpty(clip.TextContent) ? "Text" : clip.TextContent!)
            : System.IO.Path.GetFileNameWithoutExtension(clip.MediaRef);

    static string FormatTimecode(int frame) => Timecode.Format(frame, TimelineViewModel.TimelineFps);

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (vm?.State is null) return;
        Focus();
        var p = e.GetPosition(this);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) {
            // The header column owns the track actions; the clip area owns
            // what is under the pointer.
            if (p.X < HeaderWidth) ShowTrackMenu(p);
            else ShowContextMenu(p);
            e.Handled = true;
            return;
        }
        if (p.X < HeaderWidth) {
            // Header toggle zone: the eye/mute glyph at the right of the name row.
            if (p.Y >= RulerHeight && p.X >= 62 && TrackAt(p) is { } toggled)
                vm.RequestTrackToggle(toggled);
            // The empty column below the last track has exactly one meaning:
            // track management. A left click opens it like the right click does.
            else if (p.Y >= RulerHeight && TrackAt(p) is null)
                ShowTrackMenu(p);
            return;
        }

        // Audio-envelope controls sit on top of the clip body, so they claim
        // the press before selection and drag do.
        if (EnvelopeHit(p) is { } env) {
            var clip = vm.State!.FindClip(env.ClipId)!;
            if (env.OnFadeDot) {
                fadeClipId = env.ClipId;
                fadeIsIn = env.FadeIsIn;
                fadeFramesPreview = env.FadeIsIn ? clip.FadeInFrames : clip.FadeOutFrames;
                fadeActive = true;
            } else {
                envelopeClipId = env.ClipId;
                envelopeGainPreview = clip.Volume;
                envelopeActive = true;
            }
            vm.SelectOnly(env.ClipId);
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        var hit = HitTestClip(p);
        if (hit is { } h) {
            if (vm.Tool == TimelineTool.Blade) {
                vm.RequestBlade(h.Clip.Id, XToFrame(p.X));
                return;
            }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ||
                e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
                vm.ToggleSelection(h.Clip.Id);
                InvalidateVisual();
                return;
            }
            if (!vm.IsSelected(h.Clip.Id)) vm.SelectOnly(h.Clip.Id);
            // A cut between two touching clips rolls by default; Alt trims
            // just the side under the pointer.
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt) && JunctionUnderPointer(h.Track, p.X) is { } cut) {
                rollLeftId = cut.Left.Id;
                rollRightId = cut.Right.Id;
                rollBoundary = cut.Left.EndFrame;
                ComputeRollBounds(cut.Left, cut.Right);
                rollActive = false;
                dragStartX = p.X;
                e.Pointer.Capture(this);
                return;
            }
            if (EdgeUnderPointer(h.Clip, p.X) is { } edge) {
                // Arm an edge trim.
                trimClipId = h.Clip.Id;
                trimEdge = edge;
                trimBoundary = edge == 0 ? h.Clip.StartFrame : h.Clip.EndFrame;
                ComputeTrimBounds(h.Track, h.Clip, edge);
                trimActive = false;
                dragStartX = p.X;
                e.Pointer.Capture(this);
            } else {
                // Arm a potential drag-move; it activates after a threshold.
                dragClipId = h.Clip.Id;
                dragLinkGroupId = h.Clip.LinkGroupId;
                dragOriginalStart = h.Clip.StartFrame;
                dragOriginalTrackId = h.Track.Id;
                dragTargetTrackId = h.Track.Id;
                dragStartX = p.X;
                dragDeltaFrames = 0;
                dragActive = false;
                ComputeDragBounds(h.Clip);
                e.Pointer.Capture(this);
            }
            return;
        }

        // Ruler or empty track area: scrub.
        vm.SelectOnly(null);
        scrubbing = true;
        e.Pointer.Capture(this);
        vm.Scrub(XToFrame(p.X));
    }

    /// Right-click menu over the clip area: what the pointer is on. A cut
    /// offers the transition, a clip offers unlink and delete. Track actions
    /// are not here — they belong to the header column.
    void ShowContextMenu(Point p) {
        if (vm is null || p.X < HeaderWidth) return;
        var menu = new MenuFlyout();

        if (JunctionAt(p) is { } cut) {
            var transition = new MenuItem { Header = "Generate Transition Here…" };
            transition.Click += (_, _) => vm.RequestTransition(cut.Left, cut.Right);
            menu.Items.Add(transition);
            menu.Items.Add(new Separator());
        } else if (GapAt(p) is { } gap) {
            // A gap between two clips can be bridged the same way a cut can —
            // the difference is that the generated clip has room of its own.
            var transition = new MenuItem { Header = "Generate Transition Here…" };
            transition.Click += (_, _) => vm.RequestTransition(gap.Left, gap.Right);
            menu.Items.Add(transition);

            var shot = new MenuItem { Header = "Generate a Shot Here…" };
            shot.Click += (_, _) => vm.RequestShot(gap.Track.Id, gap.Start, gap.End - gap.Start);
            menu.Items.Add(shot);

            var closeGap = new MenuItem { Header = "Ripple Delete Gap" };
            closeGap.Click += (_, _) => vm.RequestCloseGap(gap.Track.Id, gap.Start, gap.End);
            menu.Items.Add(closeGap);
            menu.Items.Add(new Separator());
        } else if (EmptyTrackAt(p) is { } empty) {
            var shot = new MenuItem { Header = "Generate a Shot Here…" };
            shot.Click += (_, _) => vm.RequestShot(empty.Track.Id, empty.Frame, 0);
            menu.Items.Add(shot);
            menu.Items.Add(new Separator());
        }

        if (HitTestClip(p) is { } hit) {
            if (hit.Clip.LinkGroupId is not null) {
                var unlink = new MenuItem { Header = "Unlink Audio and Video" };
                unlink.Click += (_, _) => vm.RequestUnlink(hit.Clip.Id);
                menu.Items.Add(unlink);
            }
            if (hit.Clip.MediaType == "video") {
                var silence = new MenuItem { Header = "Remove Silence…" };
                silence.Click += (_, _) => vm.RequestRemoveSilence(hit.Clip.Id);
                menu.Items.Add(silence);
            }
            var remove = new MenuItem { Header = "Delete Clip" };
            remove.Click += (_, _) => vm.RequestDeleteClip(hit.Clip.Id);
            menu.Items.Add(remove);

            var forwardTrack = new MenuItem { Header = "Select Forward on Track" };
            forwardTrack.Click += (_, _) => { vm.SelectForward(hit.Clip, allTracks: false); InvalidateVisual(); };
            menu.Items.Add(forwardTrack);
            var forwardAll = new MenuItem { Header = "Select Forward on All Tracks" };
            forwardAll.Click += (_, _) => { vm.SelectForward(hit.Clip, allTracks: true); InvalidateVisual(); };
            menu.Items.Add(forwardAll);
            menu.Items.Add(new Separator());

            var ripple = new MenuItem {
                Header = "Ripple Delete",
                InputGesture = new KeyGesture(Key.Delete, KeyModifiers.Shift),
            };
            ripple.Click += (_, _) => vm.RequestRippleDelete(hit.Clip.Id);
            menu.Items.Add(ripple);

            var split = new MenuItem {
                Header = "Split at Playhead",
                InputGesture = new KeyGesture(Key.B, KeyModifiers.Control),
                IsEnabled = vm.PlayheadFrame > hit.Clip.StartFrame && vm.PlayheadFrame < hit.Clip.EndFrame,
            };
            split.Click += (_, _) => vm.RequestBlade(hit.Clip.Id, vm.PlayheadFrame);
            menu.Items.Add(split);

            // A diamond under the pointer offers its own deletion.
            if (hit.Clip.Id == vm.SelectedClipId && hit.Clip.HasKeyframes) {
                int frame = XToFrame(p.X);
                int grabFrames = Math.Max(1, (int)Math.Round(5 / vm.PixelsPerFrame));
                var nearby = hit.Clip.KeyframeFrames
                    .Select(k => hit.Clip.StartFrame + k)
                    .Where(k => Math.Abs(k - frame) <= grabFrames)
                    .OrderBy(k => Math.Abs(k - frame))
                    .Cast<int?>().FirstOrDefault();
                if (nearby is { } keyframe) {
                    var deleteKey = new MenuItem { Header = "Delete Keyframe" };
                    deleteKey.Click += (_, _) => vm.RequestDeleteKeyframe(hit.Clip.Id, keyframe);
                    menu.Items.Add(deleteKey);
                }
            }
        }

        if (menu.Items.Count == 0) return;
        menu.ShowAt(this, showAtPointer: true);
    }

    /// The track menu, opened by clicking the header column. Rename and delete
    /// apply to the row under the pointer; adding works from anywhere in the
    /// column, including the empty space below the last track.
    void ShowTrackMenu(Point p) {
        if (vm?.State is not { } state) return;
        var menu = new MenuFlyout();

        var addVideo = new MenuItem { Header = "Add Video Track" };
        addVideo.Click += (_, _) => vm.RequestAddTrack("video");
        var addAudio = new MenuItem { Header = "Add Audio Track" };
        addAudio.Click += (_, _) => vm.RequestAddTrack("audio");
        menu.Items.Add(addVideo);
        menu.Items.Add(addAudio);

        if (TrackAt(p) is { } track) {
            menu.Items.Add(new Separator());
            var rename = new MenuItem { Header = $"Rename {LabelFor(state, track)}…" };
            rename.Click += (_, _) => vm.RequestRenameTrack(track.Id, LabelFor(state, track));
            menu.Items.Add(rename);

            var removeTrack = new MenuItem {
                Header = $"Delete {LabelFor(state, track)}",
                // The core refuses the last of a kind; don't offer it either.
                IsEnabled = state.Tracks.Count(t => t.Type == track.Type) > 1,
            };
            removeTrack.Click += (_, _) => vm.RequestRemoveTrack(track.Id);
            menu.Items.Add(removeTrack);
        }

        menu.ShowAt(this, showAtPointer: true);
    }

    /// The track's own name, or V1…Vn / A1…An numbered within each kind in
    /// timeline order.
    static string LabelFor(TimelineState state, TrackState track) {
        if (track.Name is { Length: > 0 } named) return named;
        int index = state.Tracks.Where(t => t.Type == track.Type).ToList().IndexOf(track) + 1;
        return (track.Type == "audio" ? "A" : "V") + index;
    }

    /// The track a dragged clip could land on: same kind as the clip, and not
    /// the one it already sits on.
    TrackState? TrackDropTarget(Point p) {
        if (vm?.State is not { } state || dragClipId is null) return null;
        if (TrackAt(p) is not { } track) return null;
        var source = state.Tracks.FirstOrDefault(t => t.Id == dragOriginalTrackId);
        return source is not null && track.Type == source.Type ? track : null;
    }

    TrackState? TrackAt(Point p) {
        if (vm?.State is not { } state || p.Y < RulerHeight) return null;
        int row = (int)((p.Y - RulerHeight) / TrackHeight);
        return row >= 0 && row < state.Tracks.Count ? state.Tracks[row] : null;
    }

    /// Vertical inset between a clip and its track row, shared by the renderer
    /// and every hit test so they cannot disagree about where a clip is.
    const double ClipPadY = 2;

    /// The rect a clip is drawn in at rest (no live gesture applied) — the
    /// same geometry the renderer uses, so hit tests land on what is shown.
    Rect ClipRect(TrackState track, ClipState clip) {
        int row = vm!.State!.Tracks.IndexOf(track);
        double y = RulerHeight + row * TrackHeight;
        return new Rect(FrameToX(clip.StartFrame), y + ClipPadY,
                        clip.DurationFrames * vm.PixelsPerFrame, TrackHeight - ClipPadY * 2);
    }

    /// 0/1 when `x` grabs the clip's left/right edge, else null.
    int? EdgeUnderPointer(ClipState clip, double x) {
        if (Math.Abs(x - FrameToX(clip.StartFrame)) <= EdgeGrabWidth) return 0;
        if (Math.Abs(x - FrameToX(clip.EndFrame)) <= EdgeGrabWidth) return 1;
        return null;
    }

    /// The pair of clips sharing the cut under `x`, when two clips abut there.
    public (ClipState Left, ClipState Right)? JunctionUnderPointer(TrackState track, double x) {
        foreach (var left in track.Clips) {
            if (Math.Abs(x - FrameToX(left.EndFrame)) > EdgeGrabWidth) continue;
            var right = track.Clips.FirstOrDefault(c => c.StartFrame == left.EndFrame && c.Id != left.Id);
            if (right is not null) return (left, right);
        }
        return null;
    }

    /// The junction under a point, with its track — used by the context menu
    /// and the transition affordance as well as the roll drag.
    public (TrackState Track, ClipState Left, ClipState Right)? JunctionAt(Point p) {
        if (vm?.State is not { } state || p.Y < RulerHeight || p.X < HeaderWidth) return null;
        int row = (int)((p.Y - RulerHeight) / TrackHeight);
        if (row < 0 || row >= state.Tracks.Count) return null;
        var track = state.Tracks[row];
        return JunctionUnderPointer(track, p.X) is { } cut ? (track, cut.Left, cut.Right) : null;
    }

    /// The empty span under `p` when it has a clip on both sides, with the two
    /// clips that bracket it. Null over a clip, or past the last clip.
    public (TrackState Track, ClipState Left, ClipState Right, int Start, int End)? GapAt(Point p) {
        if (vm?.State is not { } state || TrackAt(p) is not { } track || p.X < HeaderWidth) return null;
        int frame = XToFrame(p.X);
        if (track.Clips.Any(c => frame >= c.StartFrame && frame < c.EndFrame)) return null;

        var left = track.Clips.Where(c => c.EndFrame <= frame).OrderBy(c => c.EndFrame).LastOrDefault();
        var right = track.Clips.Where(c => c.StartFrame > frame).OrderBy(c => c.StartFrame).FirstOrDefault();
        if (left is null || right is null || right.StartFrame <= left.EndFrame) return null;
        return (track, left, right, left.EndFrame, right.StartFrame);
    }

    /// Empty timeline space that is not a gap between two clips — the tail of
    /// a track, or an empty one. A generated shot can still land here.
    public (TrackState Track, int Frame)? EmptyTrackAt(Point p) {
        if (TrackAt(p) is not { } track || p.X < HeaderWidth) return null;
        int frame = Math.Max(0, XToFrame(p.X));
        return track.Clips.Any(c => frame >= c.StartFrame && frame < c.EndFrame)
            ? null
            : (track, frame);
    }

    /// How far the cut can travel, mirroring the core's rule: each side keeps
    /// a frame, the outgoing clip cannot run past its source, and the incoming
    /// clip can only give back source it actually has before its in-point.
    /// The ghost must not promise a roll the core will refuse.
    void ComputeRollBounds(ClipState left, ClipState right) {
        rollMinBoundary = left.StartFrame + 1;
        rollMaxBoundary = right.EndFrame - 1;
        if (vm is null) return;

        if (left.MediaType != "text") {
            int available = vm.SourceFramesFor(left.MediaRef) - left.TrimStartFrame;
            if (available < int.MaxValue / 2) {
                int maxDuration = Math.Max(1, (int)(available / Math.Max(left.Speed, 0.0001)));
                rollMaxBoundary = Math.Min(rollMaxBoundary, left.StartFrame + maxDuration);
            }
        }
        if (right.MediaType != "text") {
            int headroom = (int)(right.TrimStartFrame / Math.Max(right.Speed, 0.0001));
            rollMinBoundary = Math.Max(rollMinBoundary, right.StartFrame - headroom);
        }
        rollMinBoundary = Math.Min(rollMinBoundary, rollMaxBoundary);
    }

    /// View-side boundary limits: ≥1 frame of clip and flush with neighbors.
    /// The core additionally clamps to the source media's length.
    void ComputeTrimBounds(TrackState track, ClipState clip, int edge) {
        if (edge == 0) {
            trimMinBoundary = 0;
            trimMaxBoundary = clip.EndFrame - 1;
            foreach (var other in track.Clips)
                if (other.Id != clip.Id && other.EndFrame <= clip.StartFrame)
                    trimMinBoundary = Math.Max(trimMinBoundary, other.EndFrame);
        } else {
            trimMinBoundary = clip.StartFrame + 1;
            trimMaxBoundary = int.MaxValue;
            foreach (var other in track.Clips)
                if (other.Id != clip.Id && other.StartFrame >= clip.EndFrame)
                    trimMaxBoundary = Math.Min(trimMaxBoundary, other.StartFrame);
        }
    }

    (TrackState Track, ClipState Clip)? HitTestClip(Point p) {
        if (vm?.State is not { } state || p.Y < RulerHeight) return null;
        int row = (int)((p.Y - RulerHeight) / TrackHeight);
        if (row < 0 || row >= state.Tracks.Count) return null;
        var track = state.Tracks[row];
        int frame = XToFrame(p.X);
        var clip = track.Clips.FirstOrDefault(c => frame >= c.StartFrame && frame < c.EndFrame);
        return clip is null ? null : (track, clip);
    }

    protected override void OnPointerMoved(PointerEventArgs e) {
        base.OnPointerMoved(e);
        if (vm is null) return;
        var p = e.GetPosition(this);
        // A gesture is only live while the button that armed it is still down.
        // Capture-lost already disarms, but the button state is the ground
        // truth: no missed event can leave a move dragging the playhead.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && DisarmGesture()) {
            InvalidateVisual();
            UpdateEdgeCursor(p);
            return;
        }
        if (envelopeActive && envelopeClipId is { } gainClip) {
            if (vm.State?.FindClip(gainClip) is { } clip &&
                vm.State.Tracks.FirstOrDefault(t => t.Clips.Any(c => c.Id == gainClip)) is { } track) {
                envelopeGainPreview = GainAtY(ClipRect(track, clip), p.Y);
                InvalidateVisual();
            }
            return;
        }
        if (fadeActive && fadeClipId is { } fadeClip) {
            if (vm.State?.FindClip(fadeClip) is { } clip) {
                int frame = XToFrame(p.X);
                fadeFramesPreview = fadeIsIn
                    ? Math.Clamp(frame - clip.StartFrame, 0, clip.DurationFrames)
                    : Math.Clamp(clip.EndFrame - frame, 0, clip.DurationFrames);
                InvalidateVisual();
            }
            return;
        }
        if (scrubbing) {
            vm.Scrub(XToFrame(p.X));
            return;
        }
        if (rollLeftId is not null) {
            double rollDx = p.X - dragStartX;
            if (!rollActive && Math.Abs(rollDx) > 2) rollActive = true;
            if (rollActive) {
                int raw = XToFrame(p.X);
                // A roll must not snap: the cut is the thing being placed, and
                // snapping it to a neighbour would fight the gesture.
                rollBoundary = Math.Clamp(raw, rollMinBoundary, rollMaxBoundary);
                InvalidateVisual();
            }
            return;
        }
        if (trimClipId is not null) {
            double dx = p.X - dragStartX;
            if (!trimActive && Math.Abs(dx) > 2) trimActive = true;
            if (trimActive) {
                int raw = XToFrame(p.X);
                int snapped = vm.Snap(raw, vm.PixelsPerFrame, new HashSet<string> { trimClipId });
                snapGuideFrame = snapped != raw ? snapped : null;
                trimBoundary = Math.Clamp(snapped, trimMinBoundary, trimMaxBoundary);
                InvalidateVisual();
            }
            return;
        }
        if (dragClipId is not null) {
            double dx = p.X - dragStartX;
            if (!dragActive && Math.Abs(dx) > 4) dragActive = true;
            if (dragActive) {
                int rawDelta = (int)Math.Round(dx / vm.PixelsPerFrame);
                int snappedDelta = SnapDelta(rawDelta);
                snapGuideFrame = snappedDelta != rawDelta ? dragOriginalStart + snappedDelta : null;
                dragDeltaFrames = Math.Clamp(snappedDelta, dragMinDelta, dragMaxDelta);
                dragTargetTrackId = TrackDropTarget(p)?.Id ?? dragOriginalTrackId;
                InvalidateVisual();
            }
            return;
        }
        if (vm.Tool == TimelineTool.Blade) {
            int? next = p.X >= HeaderWidth && HitTestClip(p) is not null ? XToFrame(p.X) : null;
            if (next != bladeHoverFrame) {
                bladeHoverFrame = next;
                InvalidateVisual();
            }
            return;
        }
        UpdateEdgeCursor(p);
    }

    /// Snaps a move delta on whichever clip edge (start or end) lands closer
    /// to a snap target.
    int SnapDelta(int rawDelta) {
        if (vm?.State is not { } state || dragClipId is null) return rawDelta;
        var clip = state.FindClip(dragClipId);
        if (clip is null) return rawDelta;
        var exclude = new HashSet<string>(
            state.Tracks.SelectMany(t => t.Clips)
                .Where(c => c.Id == dragClipId ||
                            (dragLinkGroupId is not null && c.LinkGroupId == dragLinkGroupId))
                .Select(c => c.Id));
        int start = clip.StartFrame + rawDelta;
        int snappedStart = vm.Snap(start, vm.PixelsPerFrame, exclude);
        if (snappedStart != start) return rawDelta + (snappedStart - start);
        int end = clip.EndFrame + rawDelta;
        int snappedEnd = vm.Snap(end, vm.PixelsPerFrame, exclude);
        return rawDelta + (snappedEnd - end);
    }

    void UpdateEdgeCursor(Point p) {
        if (vm?.Tool != TimelineTool.Select) {
            Cursor = Cursor.Default;
            return;
        }
        // A cut reads as a roll handle; a lone edge reads as a trim handle.
        bool overJunction = JunctionAt(p) is not null;
        bool overEdge = HitTestClip(p) is { } h && EdgeUnderPointer(h.Clip, p.X) is not null;
        if (overJunction != hoverJunction) {
            hoverJunction = overJunction;
            InvalidateVisual();
        }
        // Both move an edit point along the timeline, so both read as the
        // horizontal double-arrow; only the number of edges differs.
        Cursor = overJunction || overEdge
            ? new Cursor(StandardCursorType.SizeWestEast)
            : Cursor.Default;
    }

    bool hoverJunction;

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (envelopeActive && envelopeClipId is { } gainClip)
            vm?.RequestSetVolume(gainClip, envelopeGainPreview);
        if (fadeActive && fadeClipId is { } fadeClip && vm?.State?.FindClip(fadeClip) is { } faded)
            vm?.RequestSetFades(fadeClip,
                fadeIsIn ? fadeFramesPreview : faded.FadeInFrames,
                fadeIsIn ? faded.FadeOutFrames : fadeFramesPreview);
        if (rollLeftId is { } rl && rollRightId is { } rr && rollActive)
            vm?.RequestRoll(rl, rr, rollBoundary);
        if (trimClipId is { } tid && trimActive)
            vm?.RequestTrim(tid, trimEdge, trimBoundary);
        if (dragClipId is { } id && dragActive) {
            bool changedTrack = dragTargetTrackId is not null && dragTargetTrackId != dragOriginalTrackId;
            if (changedTrack)
                vm?.RequestMoveToTrack(id, dragTargetTrackId!, dragOriginalStart + dragDeltaFrames);
            else if (dragDeltaFrames != 0)
                vm?.RequestMove(id, dragOriginalStart + dragDeltaFrames);
        }
        DisarmGesture();
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    /// Capture can end without a release — a flyout opening, the window
    /// deactivating, the preview's child HWND taking the mouse. The armed
    /// gesture has to come down with it: a `scrubbing` flag left set turns
    /// every later pointer move over the timeline into a playhead drag and an
    /// audio re-seek, for the rest of the session.
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) {
        base.OnPointerCaptureLost(e);
        if (DisarmGesture()) InvalidateVisual();
    }

    /// Clears every armed gesture without committing it. Returns whether
    /// anything was armed, and is safe to call twice — the release path calls
    /// it, then Avalonia raises capture-lost for the same gesture.
    bool DisarmGesture() {
        bool armed = scrubbing || rollLeftId is not null || trimClipId is not null
                     || dragClipId is not null || envelopeActive || fadeActive;
        envelopeClipId = null;
        envelopeActive = false;
        fadeClipId = null;
        fadeActive = false;
        scrubbing = false;
        rollLeftId = null;
        rollRightId = null;
        rollActive = false;
        trimClipId = null;
        trimEdge = -1;
        trimActive = false;
        dragClipId = null;
        dragLinkGroupId = null;
        dragOriginalTrackId = null;
        dragTargetTrackId = null;
        dragActive = false;
        dragDeltaFrames = 0;
        snapGuideFrame = null;
        return armed;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
        base.OnPointerWheelChanged(e);
        if (vm is null) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
            // Zoom about the cursor: keep the frame under the pointer fixed.
            var p = e.GetPosition(this);
            double frameAtCursor = (p.X - HeaderWidth + vm.ScrollOffsetX) / vm.PixelsPerFrame;
            double factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
            double next = Math.Clamp(vm.PixelsPerFrame * factor, MinPixelsPerFrame, MaxPixelsPerFrame);
            vm.PixelsPerFrame = next;
            vm.ScrollOffsetX = Math.Max(0, frameAtCursor * next - (p.X - HeaderWidth));
        } else {
            vm.ScrollOffsetX = Math.Max(0, vm.ScrollOffsetX - e.Delta.Y * 60);
        }
        e.Handled = true;
    }
}
