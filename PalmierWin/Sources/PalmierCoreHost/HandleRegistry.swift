import Foundation

/// Tracks live opaque handles handed to the .NET shell. A destroy against an
/// already-destroyed or never-valid pointer must be a no-op — releasing an
/// Unmanaged reference twice is a use-after-free inside the host process.
/// @unchecked Sendable: all mutable state sits behind the lock.
final class HandleRegistry: @unchecked Sendable {
    static let shared = HandleRegistry()

    private let lock = NSLock()
    private var live = Set<UInt>()

    func register(_ handle: UnsafeMutableRawPointer) {
        lock.lock()
        live.insert(UInt(bitPattern: handle))
        lock.unlock()
    }

    /// True when the handle was live (and is now retired); false means stale
    /// or foreign, and the caller must not touch it.
    func unregister(_ handle: UnsafeMutableRawPointer) -> Bool {
        lock.lock()
        let removed = live.remove(UInt(bitPattern: handle)) != nil
        lock.unlock()
        return removed
    }
}

/// Reads an optional C string as a path; nil for null or empty — both are
/// caller bugs, never a file to open or write.
func nonEmptyPath(_ path: UnsafePointer<CChar>?) -> String? {
    guard let path else { return nil }
    let value = String(cString: path)
    return value.isEmpty ? nil : value
}
