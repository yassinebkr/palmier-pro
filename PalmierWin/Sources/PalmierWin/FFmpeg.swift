import CFFmpeg

/// Swift-friendly FFmpeg lifecycle. libavformat/libavcodec flat-C surface — the
/// decode/export API is added incrementally; this is the seed that proves the
/// binding links and the runtime DLL resolves.
public enum FFmpeg {
    public static var versionInfo: String { String(cString: av_version_info()) }
    public static var libavformatVersion: UInt32 { avformat_version() }
}
