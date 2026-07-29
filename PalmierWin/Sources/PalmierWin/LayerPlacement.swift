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
    public static func pushConstants(
        for layer: LayerPlan,
        frame: Int,
        renderSize: Size2D,
        opacity: Double
    ) -> PushConstants {
        let t = layer.clip.transformAt(frame: frame)
        let natW = layer.natSize.width
        let natH = layer.natSize.height
        guard natW > 0, natH > 0, renderSize.width > 0, renderSize.height > 0 else {
            return PushConstants(a: 1, b: 0, c: 0, d: 1, tx: 0, ty: 0, opacity: 0)
        }

        let tl = t.topLeft
        let rw = renderSize.width
        let rh = renderSize.height
        let sx = (rw / natW) * t.width * (t.flipHorizontal ? -1 : 1) / rw
        let sy = (rh / natH) * t.height * (t.flipVertical ? -1 : 1) / rh
        let txRaw = (t.flipHorizontal ? tl.x + t.width : tl.x) * rw
        let tyRaw = (t.flipVertical ? tl.y + t.height : tl.y) * rh

        // Build in pixel space first (scale → translate → rotate), matching the
        // macOS concatenation order, then fold preferredTransform (innermost)
        // and normalize translations by renderSize.
        let scale = Mat3(a: sx, b: 0, c: 0, d: sy, tx: 0, ty: 0)
        let translate = Mat3(a: 1, b: 0, c: 0, d: 1, tx: txRaw, ty: tyRaw)
        // scale.concatenating(translate) applies scale first, then translate.
        let placed = scale.concatenating(translate)

        let rotated = t.rotation != 0 ? placed.concatenating(rotation(t: t, renderSize: renderSize)) : placed
        // preferredTransform applies first (innermost): full = preferred.concatenating(placed)
        let full = layer.preferredTransform.concatenating(rotated)

        let cx = t.centerX * rw
        let cy = t.centerY * rh
        _ = cx; _ = cy  // rotation() recomputes these from t directly

        // Normalize the translation by renderSize so the shader maps [0,1]² →
        // normalized device coords. Scale and the linear part are already
        // dimensionless (sx/sy divided by renderSize above).
        return PushConstants(
            a: f(full.a), b: f(full.b), c: f(full.c), d: f(full.d),
            tx: f(full.tx / rw), ty: f(full.ty / rh),
            opacity: f(min(1, max(0, opacity)))
        )
    }

    /// Rotation around the clip's center, in pixel space. Matches
    /// `canvasRotationTransform`: translate(-center) → rotate(θ) → translate(center).
    /// `concatenating` order: apply self first, so this is
    /// `translateToCenter.concatenating(rotate).concatenating(translateBack)`.
    private static func rotation(t: Transform, renderSize: Size2D) -> Mat3 {
        let cx = t.centerX * renderSize.width
        let cy = t.centerY * renderSize.height
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
