import Foundation

/// Minimal .cube 3D-LUT parser for the lutTetra effect pass, ported from
/// macOS `LUTLoader.parse` (Sources/PalmierPro/Compositing/LUTLoader.swift).
/// The .cube value order (r fastest, then g, then b) is exactly the shader's
/// 2D strip layout — node (r,g,b) at pixel (r, b·n+g), row 0 = top — so the
/// parsed values convert straight to a tightly-packed BGRA image.
public enum CubeLUTLoader {
    public struct CubeLUT {
        public let dimension: Int
        /// Tightly-packed BGRA8 strip, width n, height n².
        public let bgra: Data
    }

    /// Parses the .cube file at `path`. Returns nil on any format error
    /// (1D LUTs, wrong value count, dimension out of range).
    public static func load(path: String) -> CubeLUT? {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return nil }
        return parse(text)
    }

    static func parse(_ text: String) -> CubeLUT? {
        var dimension = 0
        var domainMin: [Float] = [0, 0, 0]
        var domainMax: [Float] = [1, 1, 1]
        var values: [Float] = []

        for rawLine in text.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty || line.hasPrefix("#") { continue }
            let parts = line.split(separator: " ", omittingEmptySubsequences: true)
            guard let first = parts.first else { continue }
            switch first.uppercased() {
            case "TITLE":
                continue
            case "LUT_1D_SIZE":
                return nil
            case "LUT_3D_SIZE":
                dimension = Int(parts.last.map(String.init) ?? "") ?? 0
            case "DOMAIN_MIN":
                domainMin = parts.dropFirst().compactMap { Float($0) }
            case "DOMAIN_MAX":
                domainMax = parts.dropFirst().compactMap { Float($0) }
            default:
                guard parts.count >= 3 else { continue }
                let rgb = parts.prefix(3).compactMap { Float($0) }
                guard rgb.count == 3 else { return nil }
                values.append(contentsOf: rgb)
            }
        }

        guard dimension > 1, dimension <= 128,
              values.count == dimension * dimension * dimension * 3,
              domainMin.count == 3, domainMax.count == 3 else { return nil }

        var bgra = [UInt8]()
        bgra.reserveCapacity(dimension * dimension * dimension * 4)
        for i in 0..<(values.count / 3) {
            for c in [2, 1, 0] {  // BGRA byte order
                let span = max(0.0001, domainMax[c] - domainMin[c])
                let v = min(1, max(0, (values[i * 3 + c] - domainMin[c]) / span))
                bgra.append(UInt8((v * 255).rounded()))
            }
            bgra.append(255)
        }
        return CubeLUT(dimension: dimension, bgra: Data(bgra))
    }
}
