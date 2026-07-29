import Foundation
import PalmierCore

/// Computes the push-constant placement for one layer's draw: the `Mat3`
/// affine that maps the unit quad [0,1]² → normalized device coords [0,1]²
/// (top-left origin), matching the macOS CoreImage placement exactly.
///
/// Port of `CompositionBuilder.affineTransform` + `canvasRotationTransform`
/// (Sources/PalmierPro/Preview/CompositionBuilder.swift), operating in
/// PalmierCore's portable `Mat3` instead of CGAffineTransform. The macOS path
/// builds the pixel-space placement and lets CoreImage render; here we
/// pre-divide translations by renderSize so the shader can map [0,1]² → clip
/// space without an extra renderSize uniform.
///
/// `preferredTransform` is the source's intrinsic transform (e.g. rotated
/// phone footage); it's applied first (innermost), then the canvas placement.
public enum LayerPlacement {
    public struct PushConstants {
        public let a: Float
        public let b: Float
        public let c: Float
        public let d: Float
        public let tx: Float
        public let ty: Float
        public let opacity: Float
    }

    /// Returns the 7-float push-constant block for one layer at `frame`.
    /// `opacity` is the layer's premultiplied alpha (0..1).
    ///
    /// The shader applies this Mat3 to the unit quad [0,1]² to get normalized
    /// device coords [0,1]² (top-left origin; the vert shader flips Y to clip
    /// space). Everything here is in normalized canvas coords — no pixel math.
    /// Order matches macOS: scale → translate → rotate-around-center, with
    /// `preferredTransform` applied innermost (source intrinsic transform).
    public static func pushConstants(
        for layer: LayerPlan,
        frame: Int,
        renderSize: Size2D,
        opacity: Double
    ) -> PushConstants {
        let t = layer.clip.transformAt(frame: frame)

        let tl = t.topLeft
        let sx = t.width * (t.flipHorizontal ? -1 : 1)
        let sy = t.height * (t.flipVertical ? -1 : 1)
        let tx = t.flipHorizontal ? tl.x + t.width : tl.x
        let ty = t.flipVertical ? tl.y + t.height : tl.y

        // scale then translate (concatenating applies self first).
        let scale = Mat3(a: sx, b: 0, c: 0, d: sy, tx: 0, ty: 0)
        let translate = Mat3(a: 1, b: 0, c: 0, d: 1, tx: tx, ty: ty)
        var placed = scale.concatenating(translate)

        // Rotate around the clip's center (in normalized coords).
        if t.rotation != 0 {
            placed = placed.concatenating(rotation(t: t))
        }

        // preferredTransform (source intrinsic) applies innermost.
        let full = layer.preferredTransform.concatenating(placed)

        return PushConstants(
            a: f(full.a), b: f(full.b), c: f(full.c), d: f(full.d),
            tx: f(full.tx), ty: f(full.ty),
            opacity: f(min(1, max(0, opacity)))
        )
    }

    /// Rotation around the clip's center, in normalized canvas coords.
    /// translate(-center) → rotate(θ) → translate(center).
    private static func rotation(t: Transform) -> Mat3 {
        let cx = t.centerX
        let cy = t.centerY
        let rad = t.rotation * .pi / 180
        let cosR = cos(rad)
        let sinR = sin(rad)
        let toOrigin = Mat3(a: 1, b: 0, c: 0, d: 1, tx: -cx, ty: -cy)
        let rot = Mat3(a: cosR, b: sinR, c: -1.0 * sinR, d: cosR, tx: 0, ty: 0)
        let back = Mat3(a: 1, b: 0, c: 0, d: 1, tx: cx, ty: cy)
        return toOrigin.concatenating(rot).concatenating(back)
    }

    private static func f(_ d: Double) -> Float { Float(d) }
}
