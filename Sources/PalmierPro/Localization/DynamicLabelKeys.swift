import Foundation

/// PalmierCore model enums vend their display labels as plain English strings
/// (the portable core cannot depend on L10n); the app localizes them at the
/// call site via `L10n.string(key: label)`. The localization sync only scans
/// this module for `L10n.key` literals, so these dynamic keys are registered
/// here to keep them — and their existing translations — in the catalogs.
enum DynamicLabelKeys {
    static let all: [String] = [
        L10n.key("Color Burn"),
        L10n.key("Color Dodge"),
        L10n.key("Darken"),
        L10n.key("Difference"),
        L10n.key("Exclusion"),
        L10n.key("Footage"),
        L10n.key("Full Frame"),
        L10n.key("Grid 2×2"),
        L10n.key("Grid 3×3"),
        L10n.key("Grid 4×4"),
        L10n.key("Hard Light"),
        L10n.key("Highlight Block"),
        L10n.key("Lighten"),
        L10n.key("Luminosity"),
        L10n.key("Main + Sidebar"),
        L10n.key("Multiply"),
        L10n.key("Normal"),
        L10n.key("Off"),
        L10n.key("Overlay"),
        L10n.key("PiP Bottom Left"),
        L10n.key("PiP Bottom Right"),
        L10n.key("PiP Top Left"),
        L10n.key("PiP Top Right"),
        L10n.key("Pop In"),
        L10n.key("Screen"),
        L10n.key("Side by Side"),
        L10n.key("Slide Up"),
        L10n.key("Soft Light"),
        L10n.key("Three-Up"),
        L10n.key("Three-Stack"),
        L10n.key("Top / Bottom"),
        L10n.key("Typewriter"),
        L10n.key("UPPERCASE"),
        L10n.key("Word Cycle"),
        L10n.key("Word Pop"),
        L10n.key("Word Reveal"),
        L10n.key("Word Slide"),
        L10n.key("lowercase"),
    ]
}
