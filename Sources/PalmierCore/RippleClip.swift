/// Structural view of a clip sufficient for ripple/overwrite computation.
/// Keeps the engines decoupled from the concrete timeline model so the core
/// stays portable and testable without importing the app or any UI framework.
public protocol RippleClip {
    var id: String { get }
    var startFrame: Int { get }
    var endFrame: Int { get }
    var trimStartFrame: Int { get }
    var speed: Double { get }
}

// `Clip` reads exactly the fields `RippleClip` requires; the conformance lives
// in core (not the app re-export) so the engines work for any core consumer,
// including the Windows package that depends on PalmierCore directly.
extension Clip: RippleClip {}
