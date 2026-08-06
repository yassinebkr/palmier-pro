namespace PalmierShell.Core;

/// Cumulative per-track Y offsets for one timeline state — the single
/// geometry render and every hit test share (upstream's TimelineGeometry).
/// The height rule arrives as a function so CompactRows (uniform 28) and
/// per-track heights share one path.
public sealed class TrackLayout {
    public IReadOnlyList<(TrackState Track, double Y, double Height)> Rows { get; }
    public double Bottom { get; }

    public TrackLayout(IReadOnlyList<TrackState> tracks, double top, Func<TrackState, double> heightOf) {
        var rows = new List<(TrackState, double, double)>(tracks.Count);
        double y = top;
        foreach (var track in tracks) {
            double h = heightOf(track);
            rows.Add((track, y, h));
            y += h;
        }
        Rows = rows;
        Bottom = y;
    }

    public double YOf(string trackId) => Rows.First(r => r.Track.Id == trackId).Y;
    public double HeightOf(string trackId) => Rows.First(r => r.Track.Id == trackId).Height;

    public TrackState? TrackAt(double y) => RowAt(y)?.Track;

    public (TrackState Track, double Y, double Height)? RowAt(double y) {
        foreach (var row in Rows)
            if (y >= row.Y && y < row.Y + row.Height) return row;
        return null;
    }
}
