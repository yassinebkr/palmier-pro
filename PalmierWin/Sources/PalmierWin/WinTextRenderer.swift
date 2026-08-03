import CSTBTrueType
import PalmierCore
import Foundation

/// Renders text to a BGRA bitmap using stb_truetype (flat-C, no COM/DirectWrite).
/// The Windows port's counterpart to macOS's CoreText-based TextFrameRenderer.
/// Single-line, single-font (Arial Bold from C:\Windows\Fonts); honours the
/// style's size, color, and horizontal alignment. Multi-line wrapping,
/// tracking, shadows, and per-word animation come later — the portable
/// TextStyle carries the data.
public final class WinTextRenderer {
    public enum TextError: Error, Sendable {
        case fontLoadFailed(String)
    }

    private var fontInfo: stbtt_fontinfo
    private let fontData: Data

    public init(fontPath: String = #"C:\Windows\Fonts\arialbd.ttf"#) throws {
        guard let data = FileManager.default.contents(atPath: fontPath) else {
            throw TextError.fontLoadFailed(fontPath)
        }
        self.fontData = data
        var info = stbtt_fontinfo()
        let ok = data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) -> Int32 in
            guard let base = raw.baseAddress else { return 0 }
            let buf = base.assumingMemoryBound(to: UInt8.self)
            let offset = stbtt_GetFontOffsetForIndex(buf, 0)
            return stbtt_InitFont(&info, buf, offset)
        }
        guard ok != 0 else { throw TextError.fontLoadFailed(fontPath) }
        self.fontInfo = info
    }

    /// Renders `text` at `fontSize` pixels into a `canvasWidth × canvasHeight`
    /// BGRA bitmap (tightly-packed, width*height*4 bytes). Returns nil on error.
    /// The text is centered vertically; `alignment` places it horizontally.
    /// MVP — wrapping, tracking, shadow, and border layout come later.
    public func render(_ text: String, fontSize: Float, color: TextStyle.RGBA,
                       alignment: TextStyle.Alignment,
                       canvasWidth: Int, canvasHeight: Int) -> Data? {
        guard canvasWidth > 0, canvasHeight > 0, !text.isEmpty else { return nil }
        let scale = stbtt_ScaleForPixelHeight(&fontInfo, fontSize)

        // Font vertical metrics for baseline placement.
        var ascent: Int32 = 0, descent: Int32 = 0, lineGap: Int32 = 0
        stbtt_GetFontVMetrics(&fontInfo, &ascent, &descent, &lineGap)
        let scaledAscent = Float(ascent) * scale

        // Measure the line width to place it horizontally.
        var advanceWidth: Float = 0
        for scalar in text.unicodeScalars {
            let glyph = stbtt_FindGlyphIndex(&fontInfo, Int32(scalar.value))
            var ax: Int32 = 0, lsb: Int32 = 0
            stbtt_GetGlyphHMetrics(&fontInfo, glyph, &ax, &lsb)
            advanceWidth += Float(ax) * scale
        }
        let startX: Float
        switch alignment {
        case .left: startX = 0
        case .center: startX = (Float(canvasWidth) - advanceWidth) * 0.5
        case .right: startX = Float(canvasWidth) - advanceWidth
        }
        let penStart = max(0, startX)
        let baselineY = Float(canvasHeight) * 0.5 - fontSize * 0.5 + scaledAscent

        let blue = UInt8(min(1, max(0, color.b)) * 255)
        let green = UInt8(min(1, max(0, color.g)) * 255)
        let red = UInt8(min(1, max(0, color.r)) * 255)
        let alpha = min(1, max(0, color.a))

        var bitmap = Data(count: canvasWidth * canvasHeight * 4)
        var penX = penStart
        for scalar in text.unicodeScalars {
            let glyph = stbtt_FindGlyphIndex(&fontInfo, Int32(scalar.value))
            var x0: Int32 = 0, y0: Int32 = 0, x1: Int32 = 0, y1: Int32 = 0
            stbtt_GetGlyphBitmapBox(&fontInfo, glyph, scale, scale, &x0, &y0, &x1, &y1)
            let gw = Int(x1 - x0)
            let gh = Int(y1 - y0)
            if gw > 0 && gh > 0 {
                var glyphBitmap = [UInt8](repeating: 0, count: gw * gh)
                glyphBitmap.withUnsafeMutableBufferPointer { gbuf in
                    stbtt_MakeGlyphBitmap(
                        &fontInfo, gbuf.baseAddress, Int32(gw), Int32(gh), Int32(gw),
                        scale, scale, glyph
                    )
                }
                let destX = Int(penX) + Int(x0)
                let destY = Int(baselineY) + Int(y0)
                for gy in 0..<gh {
                    let dy = destY + gy
                    guard dy >= 0 && dy < canvasHeight else { continue }
                    for gx in 0..<gw {
                        let dx = destX + gx
                        guard dx >= 0 && dx < canvasWidth else { continue }
                        let a = glyphBitmap[gy * gw + gx]
                        guard a > 0 else { continue }
                        let idx = (dy * canvasWidth + dx) * 4
                        bitmap[idx] = blue
                        bitmap[idx + 1] = green
                        bitmap[idx + 2] = red
                        bitmap[idx + 3] = UInt8(Float(a) * Float(alpha))  // glyph coverage × style alpha
                    }
                }
            }
            var ax: Int32 = 0, lsb: Int32 = 0
            stbtt_GetGlyphHMetrics(&fontInfo, glyph, &ax, &lsb)
            penX += Float(ax) * scale
        }
        return bitmap
    }
}
