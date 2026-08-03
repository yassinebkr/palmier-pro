import Foundation
import PalmierCore

/// Geometry for the selected clip's on-screen manipulation frame: the outline
/// and its eight resize handles, expressed as unit-quad placements in the same
/// normalized canvas space the compositor draws layers in.
///
/// The engine draws this rather than the shell because the preview is a native
/// child HWND — nothing in the Avalonia tree can paint over it (airspace).
/// Sizes are given in *displayed* pixels and converted against the presented
/// surface, so the outline stays two pixels wide at any zoom or canvas size.
public enum SelectionOverlay {
    /// Line thickness and handle side, in pixels of the presented surface.
    public static let edgePixels: Double = 2
    public static let handlePixels: Double = 9

    /// How far the rotate knob sits below the top edge, in the same pixels.
    /// It lives *inside* the frame: a clip filling the canvas has no room
    /// outside it, and a rotate affordance you cannot reach is not one.
    public static let rotateOffsetPixels: Double = 20

    /// The knob's distance from the centre along the clip's local -Y, in
    /// normalized units. Shared rule: the shell hit-tests where this draws.
    public static func rotateKnobOffset(height: Double, surfaceHeight: Double) -> Double {
        guard surfaceHeight >= 1 else { return 0 }
        return max(0, height / 2 - min(rotateOffsetPixels / surfaceHeight, height * 0.3))
    }

    /// One quad to draw: the unit square [0,1]² mapped into canvas space.
    public struct Quad: Equatable, Sendable {
        public let matrix: Mat3
        public let opacity: Double
    }

    /// Outline + handles for `transform`, in the clip's own rotated frame.
    /// `surface` is the presented surface size in pixels.
    public static func quads(for transform: Transform, surface: Size2D) -> [Quad] {
        guard surface.width >= 1, surface.height >= 1 else { return [] }
        let ex = edgePixels / surface.width
        let ey = edgePixels / surface.height
        let hx = handlePixels / surface.width
        let hy = handlePixels / surface.height

        let (left, top) = transform.topLeft
        let w = transform.width
        let h = transform.height
        let right = left + w
        let bottom = top + h
        let rotate = rotation(around: transform.centerX, transform.centerY, degrees: transform.rotation)

        var quads: [Quad] = []
        func add(x: Double, y: Double, width: Double, height: Double, opacity: Double) {
            guard width > 0, height > 0 else { return }
            let scale = Mat3(a: width, b: 0, c: 0, d: height, tx: 0, ty: 0)
            let place = scale.concatenating(Mat3(a: 1, b: 0, c: 0, d: 1, tx: x, ty: y))
            quads.append(Quad(matrix: place.concatenating(rotate), opacity: opacity))
        }

        // Outline: four bars centred on the box edges, so the frame reads the
        // same whichever side the clip is scaled from.
        let edgeAlpha = 0.9
        add(x: left - ex / 2, y: top - ey / 2, width: w + ex, height: ey, opacity: edgeAlpha)
        add(x: left - ex / 2, y: bottom - ey / 2, width: w + ex, height: ey, opacity: edgeAlpha)
        add(x: left - ex / 2, y: top - ey / 2, width: ex, height: h + ey, opacity: edgeAlpha)
        add(x: right - ex / 2, y: top - ey / 2, width: ex, height: h + ey, opacity: edgeAlpha)

        // Handles: corners then edge midpoints, in the order the shell hit-tests.
        for (cx, cy) in handleCentres(left: left, top: top, right: right, bottom: bottom) {
            add(x: cx - hx / 2, y: cy - hy / 2, width: hx, height: hy, opacity: 1)
        }

        // Rotate knob: a stem down from the top edge to a wider grip, so the
        // gesture is visible without hunting for a modifier.
        let knobY = transform.centerY - rotateKnobOffset(height: h, surfaceHeight: surface.height)
        if knobY > top {
            add(x: transform.centerX - ex / 2, y: top, width: ex, height: knobY - top, opacity: 0.7)
            add(x: transform.centerX - hx * 0.7, y: knobY - hy * 0.35,
                width: hx * 1.4, height: hy * 0.7, opacity: 1)
        }
        return quads
    }

    /// The eight handle centres in unrotated canvas space: four corners
    /// (NW, NE, SE, SW) then four edge midpoints (N, E, S, W).
    public static func handleCentres(left: Double, top: Double,
                                     right: Double, bottom: Double) -> [(Double, Double)] {
        let midX = (left + right) / 2
        let midY = (top + bottom) / 2
        return [
            (left, top), (right, top), (right, bottom), (left, bottom),
            (midX, top), (right, midY), (midX, bottom), (left, midY),
        ]
    }

    /// Rotation by `degrees` about (cx, cy), clockwise — matching `Transform`.
    private static func rotation(around cx: Double, _ cy: Double, degrees: Double) -> Mat3 {
        guard degrees != 0 else { return .identity }
        let rad = degrees * .pi / 180
        let toOrigin = Mat3(a: 1, b: 0, c: 0, d: 1, tx: -cx, ty: -cy)
        let rot = Mat3(a: cos(rad), b: sin(rad), c: -sin(rad), d: cos(rad), tx: 0, ty: 0)
        let back = Mat3(a: 1, b: 0, c: 0, d: 1, tx: cx, ty: cy)
        return toOrigin.concatenating(rot).concatenating(back)
    }
}
