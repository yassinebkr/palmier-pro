import CMediaFoundation

/// Swift-friendly Media Foundation lifecycle. Wraps the C binding so callers
/// don't pass raw version/flag literals. `MFStartup`/`MFShutdown` must be
/// balanced; use this RAII guard rather than calling the C functions directly.
public enum MediaFoundation {
    /// True when MFStartup succeeded and MFShutdown has not yet been called.
    public static func start() -> Bool {
        // The MF_VERSION and MFSTARTUP_LITE #defines import as Int32 but
        // MFStartup takes UInt32 (ULONG); cast at the boundary.
        MFStartup(UInt32(MF_VERSION), UInt32(MFSTARTUP_LITE)) >= 0
    }

    public static func stop() {
        _ = MFShutdown()
    }
}

/// Balanced MFStartup/MFShutdown. `deinit` shuts down if `start()` succeeded.
public final class MediaFoundationSession: @unchecked Sendable {
    public let isActive: Bool

    public init() {
        self.isActive = MediaFoundation.start()
    }

    deinit {
        if isActive { MediaFoundation.stop() }
    }
}
