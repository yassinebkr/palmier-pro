import Foundation

/// Matte aspect-ratio presets and the pure pixel-fitting math. The
/// CoreGraphics-backed PNG renderer (`Matte.png`) and the SwiftUI `Color`
/// helper stay app-side; this file holds only the portable model + sizing.
public enum MatteAspect: String, CaseIterable, Identifiable, Sendable {
    case project = "Project", sixteenNine = "16:9", nineSixteen = "9:16"
    case oneOne = "1:1", fourThree = "4:3", nineFourteen = "9:14", twoPointFourOne = "2.4:1"
    public var id: String { rawValue }

    private var ratio: (Int, Int)? {
        switch self {
        case .project: nil
        case .sixteenNine: (16, 9)
        case .nineSixteen: (9, 16)
        case .oneOne: (1, 1)
        case .fourThree: (4, 3)
        case .nineFourteen: (9, 14)
        case .twoPointFourOne: (24, 10)
        }
    }

    public func pixelSize(timelineWidth w: Int, timelineHeight h: Int) -> (width: Int, height: Int) {
        guard let (aw, ah) = ratio else { return Matte.even(w, h) }
        return Matte.fit(short: min(w, h), aspectW: aw, aspectH: ah)
    }

    public static func parse(_ raw: String?) -> MatteAspect? {
        guard let raw = raw?.trimmingCharacters(in: .whitespacesAndNewlines), !raw.isEmpty else { return nil }
        if raw.caseInsensitiveCompare("project") == .orderedSame { return .project }
        return MatteAspect(rawValue: raw)
    }
}

/// Pure matte sizing math + errors. The `png` renderer and `Color.matteHex`
/// helper live in the app (CoreGraphics / AppKit); they extend this enum there.
public enum Matte {
    public enum Error: LocalizedError, Sendable {
        case renderFailed, noProject
        public var errorDescription: String? {
            switch self {
            case .renderFailed: "Couldn't render matte image."
            case .noProject: "Open a project before creating a matte."
            }
        }
    }

    /// Round each dimension down to an even, ≥2 value (encoders prefer even sizes).
    public static func even(_ w: Int, _ h: Int) -> (width: Int, height: Int) {
        (max(2, (max(2, w) / 2) * 2), max(2, (max(2, h) / 2) * 2))
    }

    /// Fit a matte to an aspect ratio, anchoring on the shorter timeline edge.
    public static func fit(short edge: Int, aspectW: Int, aspectH: Int) -> (width: Int, height: Int) {
        let e = max(2, edge)
        let aw = Double(aspectW), ah = Double(aspectH)
        if aw >= ah { return even(Int((Double(e) * aw / ah).rounded()), e) }
        return even(e, Int((Double(e) * ah / aw).rounded()))
    }
}
