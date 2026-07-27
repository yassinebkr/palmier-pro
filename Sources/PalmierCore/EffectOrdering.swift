import Foundation

/// Canonical insertion order for always-on adjustment effects, plus the pure
/// `insertIndex` helper that places a new effect at its rank-correct position.
/// Extracted from the app's CoreImage-bound `EffectRegistry` so portable model
/// types (e.g. `HueCurves.upsert`) can order effects without depending on the
/// rendering surface. The app's `EffectRegistry` delegates here.
public enum EffectOrdering {
    public static let canonicalOrder: [String] = [
        "color.exposure", "color.contrast", "color.highlightsShadows", "color.blacksWhites",
        "color.temperature", "color.vibrance", "color.saturation", "color.wheels", "color.curves",
        "color.hueCurves", "color.lut", "detail.clarity", "key.chroma", "blur.gaussian", "blur.sharpen",
        "blur.noiseReduction", "blur.motion", "stylize.invert", "stylize.grain", "stylize.vignette",
        "stylize.glow",
    ]

    /// Index at which to insert `id` so the effect list stays in canonical order.
    public static func insertIndex(_ effects: [Effect], for id: String) -> Int {
        let rank = canonicalOrder.firstIndex(of: id) ?? Int.max
        return effects.firstIndex { (canonicalOrder.firstIndex(of: $0.type) ?? Int.max) > rank } ?? effects.count
    }
}
