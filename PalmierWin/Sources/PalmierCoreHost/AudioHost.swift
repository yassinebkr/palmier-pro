import Foundation
import PalmierCore
import PalmierWin

// Audio playback ABI: one WASAPI output (via miniaudio) per project. The
// shell's video clock stays master; audio follows play/pause/seek commands.

/// Retained audio state behind the opaque handle.
final class AudioContext {
    let engine: WinAudioEngine
    let project: ProjectContext
    var syncedGeneration = -1
    init(engine: WinAudioEngine, project: ProjectContext) {
        self.engine = engine
        self.project = project
    }

    /// Output stops when the handle is released, not whenever the last
    /// reference happens to drop.
    deinit { engine.stop() }

    /// Pushes the current audible clip set into the mixer when the project
    /// changed since the last sync. Muted tracks contribute nothing.
    func syncIfNeeded() {
        let (timeline, generation) = project.renderSnapshotWithGeneration()
        guard generation != syncedGeneration else { return }
        syncedGeneration = generation

        var sources: [WinAudioEngine.ClipSource] = []
        for track in timeline.tracks where track.type == .audio && !track.muted {
            let trackGain = VolumeScale.linearFromDb(track.gainDb)
            for clip in track.clips {
                let volumePoints = (clip.volumeTrack?.keyframes ?? []).map {
                    WinAudioEngine.VolumePoint(frame: $0.frame, db: $0.value)
                }
                sources.append(WinAudioEngine.ClipSource(
                    id: clip.id, mediaRef: clip.mediaRef,
                    startFrame: clip.startFrame, durationFrames: clip.durationFrames,
                    trimStartFrame: clip.trimStartFrame, volume: clip.volume,
                    trackGain: trackGain,
                    fadeInFrames: clip.fadeInFrames, fadeOutFrames: clip.fadeOutFrames,
                    volumeKeyframes: volumePoints))
            }
        }
        engine.update(clips: sources)
    }
}

/// Creates the audio output for `project` (device starts immediately,
/// silent until playing). Returns NULL when no output device is available —
/// the app keeps working without audio.
@_cdecl("palmier_audio_create")
public func palmierAudioCreate(_ projectHandle: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let projectHandle else { return nil }
    let project = Unmanaged<ProjectContext>.fromOpaque(projectHandle).takeUnretainedValue()
    guard let engine = WinAudioEngine() else { return nil }
    let ctx = AudioContext(engine: engine, project: project)
    ctx.syncIfNeeded()
    let handle = Unmanaged.passRetained(ctx).toOpaque()
    HandleRegistry.shared.register(handle)
    return handle
}

@_cdecl("palmier_audio_destroy")
public func palmierAudioDestroy(_ handle: UnsafeMutableRawPointer?) {
    guard let handle, HandleRegistry.shared.unregister(handle) else { return }
    Unmanaged<AudioContext>.fromOpaque(handle).release()
}

/// Starts (playing=1) or stops (0) audible playback from `frame`.
@_cdecl("palmier_audio_set_playing")
public func palmierAudioSetPlaying(_ handle: UnsafeMutableRawPointer?, _ playing: Int32, _ frame: Int32) -> Int32 {
    guard let handle, frame >= 0 else { return 0 }
    let ctx = Unmanaged<AudioContext>.fromOpaque(handle).takeUnretainedValue()
    ctx.syncIfNeeded()
    ctx.engine.setPlaying(playing != 0, atFrame: Int(frame))
    return 1
}

/// Re-aligns the audio position to `frame` (scrub while playing).
@_cdecl("palmier_audio_seek")
public func palmierAudioSeek(_ handle: UnsafeMutableRawPointer?, _ frame: Int32) -> Int32 {
    guard let handle, frame >= 0 else { return 0 }
    let ctx = Unmanaged<AudioContext>.fromOpaque(handle).takeUnretainedValue()
    ctx.syncIfNeeded()
    ctx.engine.seek(toFrame: Int(frame))
    return 1
}

/// Re-syncs the mixer with the project after an edit.
@_cdecl("palmier_audio_sync")
public func palmierAudioSync(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let handle else { return 0 }
    Unmanaged<AudioContext>.fromOpaque(handle).takeUnretainedValue().syncIfNeeded()
    return 1
}

/// Test seam: the track gain folded into `clipId`'s mix entry at the current
/// sync, or NaN when the clip is not in the mix.
@_cdecl("palmier_audio_clip_track_gain")
public func palmierAudioClipTrackGain(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?) -> Double {
    guard let handle, let clipId else { return .nan }
    let ctx = Unmanaged<AudioContext>.fromOpaque(handle).takeUnretainedValue()
    ctx.syncIfNeeded()
    return ctx.engine.trackGain(forClipId: String(cString: clipId)) ?? .nan
}
