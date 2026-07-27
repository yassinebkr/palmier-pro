import Foundation

/// One point on a color-adjustment curve (grade or hue). Pure data; the
/// CoreImage/Direct2D rendering of these curves stays app/platform-side.
public struct CurvePoint: Codable, Sendable, Equatable {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) { self.x = x; self.y = y }
}

/// Master (Rec.709 luma) + per-channel R/G/B tone curves. Pure model —
/// compiled to a `CIColorCube` (macOS) or an equivalent LUT (Windows) at the
/// render boundary, not here.
public struct GradeCurve: Codable, Sendable, Equatable {
    public var master: [CurvePoint] = []
    public var red: [CurvePoint] = []
    public var green: [CurvePoint] = []
    public var blue: [CurvePoint] = []

    public static let identityPoints = [CurvePoint(x: 0, y: 0), CurvePoint(x: 1, y: 1)]

    public init() {}
    public init(master: [CurvePoint] = [], red: [CurvePoint] = [], green: [CurvePoint] = [], blue: [CurvePoint] = []) {
        self.master = master; self.red = red; self.green = green; self.blue = blue
    }

    public var isIdentity: Bool {
        [master, red, green, blue].allSatisfy { $0.isEmpty || $0 == Self.identityPoints }
    }

    /// Piecewise-linear interpolation, clamped flat outside the point range.
    public static func eval(_ pts: [CurvePoint], _ x: Double) -> Double {
        let p = (pts.isEmpty ? identityPoints : pts).sorted { $0.x < $1.x }
        if x <= p[0].x { return p[0].y }
        if x >= p[p.count - 1].x { return p[p.count - 1].y }
        for i in 1..<p.count where x <= p[i].x {
            let a = p[i - 1], b = p[i]
            let t = (b.x - a.x) == 0 ? 0 : (x - a.x) / (b.x - a.x)
            return a.y + (b.y - a.y) * t
        }
        return x
    }

    public func encoded() -> String? {
        (try? JSONEncoder().encode(self)).flatMap { String(data: $0, encoding: .utf8) }
    }
}

/// Failable JSON init kept in an extension so the memberwise initializer survives.
extension GradeCurve {
    public init?(json: String) {
        guard let data = json.data(using: .utf8),
              let decoded = try? JSONDecoder().decode(GradeCurve.self, from: data) else { return nil }
        self = decoded
    }
}

/// Resolve-style hue curves: each maps source hue (0…1, cyclic) to one adjustment
/// channel. Pure model; the hue-curve kernel (CoreImage/Direct2D) renders it.
public struct HueCurves: Codable, Sendable, Equatable {
    public var hueVsHue: [CurvePoint] = []   // → hue rotation
    public var hueVsSat: [CurvePoint] = []   // → saturation scale
    public var hueVsLum: [CurvePoint] = []   // → luminance shift

    public enum Channel: String, CaseIterable, Identifiable, Sendable {
        case hue = "Hue", sat = "Sat", lum = "Luma"
        public var id: String { rawValue }
    }

    public static let neutralY = 0.5
    public static let effectType = "color.hueCurves"
    public static let defaultPoints: [CurvePoint] = (0..<6).map { CurvePoint(x: Double($0) / 6, y: neutralY) }

    public init() {}
    public init(hueVsHue: [CurvePoint] = [], hueVsSat: [CurvePoint] = [], hueVsLum: [CurvePoint] = []) {
        self.hueVsHue = hueVsHue; self.hueVsSat = hueVsSat; self.hueVsLum = hueVsLum
    }

    public func points(_ c: Channel) -> [CurvePoint] {
        switch c { case .hue: hueVsHue; case .sat: hueVsSat; case .lum: hueVsLum }
    }

    public mutating func set(_ c: Channel, _ pts: [CurvePoint]) {
        switch c { case .hue: hueVsHue = pts; case .sat: hueVsSat = pts; case .lum: hueVsLum = pts }
    }

    public static func isNeutral(_ pts: [CurvePoint]) -> Bool {
        pts.isEmpty || pts.allSatisfy { abs($0.y - neutralY) < 1e-4 }
    }

    /// All curves flat → no effect to render or persist.
    public var isIdentity: Bool { [hueVsHue, hueVsSat, hueVsLum].allSatisfy(Self.isNeutral) }

    /// Cyclic piecewise-linear eval — wraps across the hue seam so the curve is seamless at 0/1.
    public static func eval(_ pts: [CurvePoint], _ x: Double) -> Double {
        let p = (pts.isEmpty ? defaultPoints : pts).sorted { $0.x < $1.x }
        guard let first = p.first, let last = p.last else { return neutralY }
        if x < first.x { return lerp(CurvePoint(x: last.x - 1, y: last.y), first, x) }
        for i in 1..<p.count where x <= p[i].x { return lerp(p[i - 1], p[i], x) }
        return lerp(last, CurvePoint(x: first.x + 1, y: first.y), x)
    }

    private static func lerp(_ a: CurvePoint, _ b: CurvePoint, _ x: Double) -> Double {
        let t = (b.x - a.x) == 0 ? 0 : (x - a.x) / (b.x - a.x)
        return a.y + (b.y - a.y) * t
    }

    public func encoded() -> String? {
        (try? JSONEncoder().encode(self)).flatMap { String(data: $0, encoding: .utf8) }
    }

    public static func read(from effects: [Effect]) -> HueCurves {
        guard let json = effects.first(where: { $0.type == effectType })?.params["curves"]?.string
        else { return HueCurves() }
        return HueCurves(json: json) ?? HueCurves()
    }

    /// Write `self` into `effects` (canonical order), or remove it when there's nothing to keep.
    public mutating func upsert(into effects: inout [Effect]) {
        let existing = effects.firstIndex { $0.type == Self.effectType }
        guard !isIdentity, let json = encoded() else {
            if let existing { effects.remove(at: existing) }
            return
        }
        if let existing {
            effects[existing].params["curves"] = EffectParam(string: json)
        } else {
            var effect = Effect(type: Self.effectType)
            effect.params["curves"] = EffectParam(string: json)
            effects.insert(effect, at: EffectOrdering.insertIndex(effects, for: Self.effectType))
        }
    }
}

/// Failable JSON init kept in an extension so the memberwise initializer survives.
extension HueCurves {
    public init?(json: String) {
        guard let data = json.data(using: .utf8),
              let decoded = try? JSONDecoder().decode(HueCurves.self, from: data) else { return nil }
        self = decoded
    }
}
