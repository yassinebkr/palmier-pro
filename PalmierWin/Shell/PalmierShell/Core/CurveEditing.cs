namespace PalmierShell.Core;

/// The curve editors' gesture math, ported from the macOS CurveEditorView /
/// HueCurveEditorView so the control and the tests share one implementation.
/// Screen coordinates are pixels with y down; curve values are unit [0, 1]
/// with y up.
public static class CurveEditing {
    /// Minimum x gap between neighbouring points — a drag may never cross or
    /// touch its neighbours.
    public const double NeighborGap = 0.001;

    public static CurvePoint FromScreen(double sx, double sy, double width, double height) => new(
        Math.Clamp(sx / width, 0, 1), Math.Clamp(1 - sy / height, 0, 1));

    public static (double X, double Y) ToScreen(CurvePoint point, double width, double height) =>
        (point.X * width, (1 - point.Y) * height);

    /// The nearest point within hitRadius (pixels), else -1.
    public static int NearestIndex(IReadOnlyList<CurvePoint> points, double sx, double sy,
                                   double width, double height, double hitRadius) {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < points.Count; i++) {
            var (px, py) = ToScreen(points[i], width, height);
            double dx = px - sx, dy = py - sy;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= hitRadius && distance < bestDistance) {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// Press: grab the nearest point, or drop a new one at the press position
    /// and grab that. Returns the sorted working list and the grabbed index.
    public static (List<CurvePoint> Points, int Index) Grab(IReadOnlyList<CurvePoint> points,
            double sx, double sy, double width, double height, double hitRadius) {
        var pts = points.OrderBy(p => p.X).ToList();
        int nearest = NearestIndex(pts, sx, sy, width, height, hitRadius);
        if (nearest >= 0) return (pts, nearest);
        var added = FromScreen(sx, sy, width, height);
        pts.Add(added);
        pts.Sort((a, b) => a.X.CompareTo(b.X));
        int index = pts.FindIndex(p => p.Equals(added));
        return (pts, index >= 0 ? index : 0);
    }

    /// Drag: y follows the pointer; x is clamped strictly between neighbours
    /// and the endpoints keep their x.
    public static List<CurvePoint> Move(IReadOnlyList<CurvePoint> points, int index,
                                        double sx, double sy, double width, double height) {
        var pts = points.ToList();
        var v = FromScreen(sx, sy, width, height);
        double x = pts[index].X;
        if (index != 0 && index != pts.Count - 1)
            x = Math.Min(pts[index + 1].X - NeighborGap, Math.Max(pts[index - 1].X + NeighborGap, v.X));
        pts[index] = new CurvePoint(x, v.Y);
        return pts;
    }

    /// Double-tap removal: refuses endpoints and never drops below two points.
    /// Null when the tap hit nothing removable.
    public static List<CurvePoint>? RemoveNearest(IReadOnlyList<CurvePoint> points,
            double sx, double sy, double width, double height, double hitRadius) {
        var pts = points.OrderBy(p => p.X).ToList();
        int i = NearestIndex(pts, sx, sy, width, height, hitRadius);
        if (i <= 0 || i >= pts.Count - 1 || pts.Count <= 2) return null;
        pts.RemoveAt(i);
        return pts;
    }
}
