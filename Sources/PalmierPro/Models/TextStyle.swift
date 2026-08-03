import AppKit
import CoreText
import PalmierCore
import SwiftUI

// TextStyle lives in PalmierCore (the portable data model). The app extends it
// here with AppKit-bound rendering helpers (resolvedFont, nsColor, paragraphStyle,
// scaledVisualStyle). This typealias makes the core type the single TextStyle
// across both modules — any bare `TextStyle` in PalmierPro resolves to
// PalmierCore.TextStyle, matching Clip.textStyle / TextAnimation and avoiding
// the cross-module assignment failures that surfaced when upstream #419 added a
// `TextStyle()` constructor in an app-side context.
typealias TextStyle = PalmierCore.TextStyle

extension TextStyle {
    static var caption: TextStyle { TextStyle(fontSize: AppTheme.Caption.defaultFontSize) }
}

// MARK: - Rendering helpers

extension TextStyle.RGBA {
    mutating func setRGB(from color: Self) {
        r = color.r
        g = color.g
        b = color.b
    }

    var nsColor: NSColor {
        NSColor(
            srgbRed: CGFloat(r),
            green: CGFloat(g),
            blue: CGFloat(b),
            alpha: CGFloat(a)
        )
    }

    var swiftUIColor: Color {
        Color(.sRGB, red: r, green: g, blue: b, opacity: a)
    }

    init(_ color: Color) {
        let ns = NSColor(color).usingColorSpace(.sRGB) ?? .black
        self.init(
            r: Double(ns.redComponent),
            g: Double(ns.greenComponent),
            b: Double(ns.blueComponent),
            a: Double(ns.alphaComponent)
        )
    }

    /// Accepts `#RGB`, `#RRGGBB`, or `#RRGGBBAA`. Leading `#` optional.
    init?(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        if s.hasPrefix("#") { s.removeFirst() }
        let chars = Array(s)
        func component(_ start: Int, _ len: Int) -> Double? {
            let slice = String(chars[start..<start + len])
            let byteStr = len == 1 ? slice + slice : slice
            guard let n = UInt8(byteStr, radix: 16) else { return nil }
            return Double(n) / 255.0
        }
        switch chars.count {
        case 3:
            guard let r = component(0, 1), let g = component(1, 1), let b = component(2, 1) else { return nil }
            self.init(r: r, g: g, b: b, a: 1)
        case 6:
            guard let r = component(0, 2), let g = component(2, 2), let b = component(4, 2) else { return nil }
            self.init(r: r, g: g, b: b, a: 1)
        case 8:
            guard let r = component(0, 2), let g = component(2, 2),
                  let b = component(4, 2), let a = component(6, 2) else { return nil }
            self.init(r: r, g: g, b: b, a: a)
        default:
            return nil
        }
    }
}

extension TextStyle {
    nonisolated(unsafe) private static let resolvedFontCache: NSCache<NSString, NSFont> = {
        let cache = NSCache<NSString, NSFont>()
        cache.countLimit = 512
        return cache
    }()

    var scaledVisualStyle: TextStyle {
        guard fontScale != 1 else { return self }
        var style = self
        style.fontSize *= fontScale
        style.tracking *= fontScale
        style.lineSpacing *= fontScale
        style.shadow.offsetX *= fontScale
        style.shadow.offsetY *= fontScale
        style.shadow.blur *= fontScale
        style.border.width *= fontScale
        style.background.paddingX *= fontScale
        style.background.paddingY *= fontScale
        style.background.cornerRadius *= fontScale
        style.background.offsetX *= fontScale
        style.background.offsetY *= fontScale
        style.background.outlineWidth *= fontScale
        style.fontScale = 1
        return style
    }

    func resolvedFont(size: CGFloat) -> NSFont {
        let key = "\(fontName.utf8.count):\(fontName)|\(Double(size).bitPattern)|\(isBold)|\(isItalic)|\(widthScale.bitPattern)|\(heightScale.bitPattern)" as NSString
        if let cached = Self.resolvedFontCache.object(forKey: key) { return cached }

        let namedBase = NSFont(name: fontName, size: size)
        let base = namedBase ?? NSFont.systemFont(ofSize: size)
        var resolved = Self.font(base, size: size, bold: isBold, italic: isItalic)
        if widthScale != 1 || heightScale != 1 {
            var transform = CGAffineTransform(scaleX: CGFloat(widthScale), y: CGFloat(heightScale))
            resolved = CTFontCreateCopyWithAttributes(resolved as CTFont, size, &transform, nil) as NSFont
        }
        // A bundled font may not be registered yet; do not cache its fallback.
        if namedBase != nil { Self.resolvedFontCache.setObject(resolved, forKey: key) }
        return resolved
    }

    var nsColor: NSColor { color.nsColor }

    func paragraphStyle(size: CGFloat, alignment override: NSTextAlignment? = nil) -> NSParagraphStyle {
        let p = NSMutableParagraphStyle()
        if let override {
            p.alignment = override
        } else {
            switch alignment {
            case .left: p.alignment = .left
            case .center: p.alignment = .center
            case .right: p.alignment = .right
            }
        }
        p.lineBreakMode = .byWordWrapping
        let scaledSpacing = lineSpacing * Double(size) / max(1, fontSize * fontScale)
        p.lineSpacing = CGFloat(scaledSpacing.isFinite ? scaledSpacing : 0)
        return p
    }

    func displayText(_ text: String) -> String {
        fontCase.apply(to: text)
    }

    /// Two-pass outlines need an opaque fill; translucent fills would show the undercoat through them.
    var drawsGlyphOutline: Bool {
        border.enabled && border.width > 0 && color.a >= 1
    }

    /// `includeColor: false` for bounding measurement (color doesn't affect size).
    func attributes(size: CGFloat, includeColor: Bool = true) -> [NSAttributedString.Key: Any] {
        var attrs = baseAttributes(size: size)
        if isUnderlined { attrs[.underlineStyle] = NSUnderlineStyle.single.rawValue }
        if isStruckThrough { attrs[.strikethroughStyle] = NSUnderlineStyle.single.rawValue }
        if includeColor { attrs[.foregroundColor] = nsColor }
        if border.enabled, border.width > 0, !drawsGlyphOutline {
            attrs[.strokeWidth] = NSNumber(value: -100 * max(0, border.width) / max(1, fontSize * fontScale))
            if includeColor { attrs[.strokeColor] = border.color.nsColor }
        }
        return attrs
    }

    /// Stroke-only undercoat at 2× width; the fill drawn on top covers the inner half, leaving `border.width` outward.
    func outlineUndercoatAttributes(size: CGFloat) -> [NSAttributedString.Key: Any] {
        var attrs = baseAttributes(size: size)
        attrs[.strokeWidth] = NSNumber(value: 200 * max(0, border.width) / max(1, fontSize * fontScale))
        attrs[.strokeColor] = border.color.nsColor
        return attrs
    }

    func glyphBorderPadding(fontSize: CGFloat) -> CGFloat {
        ceil(fontSize * CGFloat(max(0, border.width)) / CGFloat(max(1, self.fontSize * fontScale)))
    }

    private func baseAttributes(size: CGFloat) -> [NSAttributedString.Key: Any] {
        [
            .font: resolvedFont(size: size),
            .paragraphStyle: paragraphStyle(size: size),
            .kern: tracking * Double(size) / max(1, fontSize * fontScale),
        ]
    }

    private static func font(_ font: NSFont, size: CGFloat, bold: Bool, italic: Bool) -> NSFont {
        var traits = CTFontGetSymbolicTraits(font as CTFont)
        if bold { traits.insert(.traitBold) } else { traits.remove(.traitBold) }
        if italic { traits.insert(.traitItalic) } else { traits.remove(.traitItalic) }

        let mask: CTFontSymbolicTraits = [.traitBold, .traitItalic]
        let descriptor = CTFontCopyFontDescriptor(font as CTFont)
        guard let resolvedDescriptor = CTFontDescriptorCreateCopyWithSymbolicTraits(descriptor, traits, mask) else {
            return font
        }
        return CTFontCreateWithFontDescriptor(resolvedDescriptor, size, nil) as NSFont
    }
}
