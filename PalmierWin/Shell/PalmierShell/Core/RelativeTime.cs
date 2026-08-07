namespace PalmierShell.Core;

/// "2 h ago"-style age text. Abbreviated units, so no plural rules; past a
/// month the date says more than a count.
public static class RelativeTime {
    public static string Ago(DateTimeOffset then, DateTimeOffset now) {
        var span = now - then;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;   // clock skew reads as fresh
        return span switch {
            { TotalMinutes: < 1 } => "just now",
            { TotalHours: < 1 } => $"{(int)span.TotalMinutes} min ago",
            { TotalDays: < 1 } => $"{(int)span.TotalHours} h ago",
            { TotalDays: < 30 } => $"{(int)span.TotalDays} d ago",
            _ => then.ToLocalTime().ToString("d MMM yyyy"),
        };
    }
}
