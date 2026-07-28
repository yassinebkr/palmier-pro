import AppKit
import CoreGraphics
import Foundation
import SwiftUI

// `Matte` and `MatteAspect` (model + sizing math) live in `PalmierCore`. This
// file holds only the CoreGraphics/AppKit rendering surface that extends them.

extension Matte {
    /// Renders a flat-color PNG matte. CoreGraphics-bound; the portable sizing
    /// math (`Matte.even`) is inherited from core.
    static func png(hex: String, width: Int, height: Int) throws -> Data {
        guard let color = TextStyle.RGBA(hex: hex) else { throw Error.renderFailed }
        let (ew, eh) = even(width, height)
        guard let ctx = CGContext(
            data: nil, width: ew, height: eh, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { throw Error.renderFailed }
        ctx.setFillColor(red: CGFloat(color.r), green: CGFloat(color.g), blue: CGFloat(color.b), alpha: 1)
        ctx.fill(CGRect(x: 0, y: 0, width: ew, height: eh))
        guard let image = ctx.makeImage(), let data = ImageEncoder.encodePNG(image) else { throw Error.renderFailed }
        return data
    }
}

extension Color {
    var matteHex: String {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .black
        return String(format: "#%02X%02X%02X", Int(c.redComponent * 255), Int(c.greenComponent * 255), Int(c.blueComponent * 255))
    }
}
