namespace PalmierShell.Core;

/// The top-right badge: one letter from the user's name on the accent colour.
public static class UserBadge {
    /// First letter of the trimmed name, uppercased; "?" until a name exists.
    public static string Initial(string? name) {
        string trimmed = name?.Trim() ?? "";
        return trimmed.Length == 0 ? "?" : char.ToUpperInvariant(trimmed[0]).ToString();
    }
}
