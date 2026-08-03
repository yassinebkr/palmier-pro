import CMiniaudio
import Foundation

/// Timeline audio output: a miniaudio WASAPI playback device (f32 stereo
/// 48 kHz) whose data callback mixes the audio clips under the advancing
/// play position.
///
/// **Nothing slow runs on the audio thread.** A feeder thread owns every
/// decoder and keeps each clip's ring buffer topped up ahead of the playhead;
/// the callback only drains those rings, applies gain and sums. Opening a
/// media file takes tens of milliseconds against a ~10 ms buffer, so doing it
/// in the callback — as this did — dropped audio every time a new clip came
/// under the playhead, and blocked the UI thread whenever it happened to be
/// syncing the clip list at the same moment.
///
/// Control state is guarded by `stateLock`, which is only ever held for
/// scalar updates. Ring buffers have their own locks, held for a memcpy.
/// v1 limitation: clip speed is assumed 1.0 for audio (retimed clips play
/// their video retimed, audio at 1x).
/// Shared between the control, feeder and audio threads; every mutable field
/// is behind `stateLock` or a ring's own lock.
public final class WinAudioEngine: @unchecked Sendable {
    /// One volume keyframe: clip-relative frame → dB (linear interp in the mixer).
    public struct VolumePoint: Sendable, Equatable {
        public let frame: Int
        public let db: Double
        public init(frame: Int, db: Double) {
            self.frame = frame
            self.db = db
        }
    }

    public struct ClipSource: Sendable, Equatable {
        public let id: String
        public let mediaRef: String
        public let startFrame: Int
        public let durationFrames: Int
        public let trimStartFrame: Int
        public let volume: Double
        public let fadeInFrames: Int
        public let fadeOutFrames: Int
        public let volumeKeyframes: [VolumePoint]
        public init(id: String, mediaRef: String, startFrame: Int, durationFrames: Int,
                    trimStartFrame: Int, volume: Double,
                    fadeInFrames: Int = 0, fadeOutFrames: Int = 0,
                    volumeKeyframes: [VolumePoint] = []) {
            self.id = id
            self.mediaRef = mediaRef
            self.startFrame = startFrame
            self.durationFrames = durationFrames
            self.trimStartFrame = trimStartFrame
            self.volume = volume
            self.fadeInFrames = fadeInFrames
            self.fadeOutFrames = fadeOutFrames
            self.volumeKeyframes = volumeKeyframes
        }

        /// Keyframe gain at a clip-relative frame (linear interp between
        /// points, held flat outside; smooth interpolation plays as linear).
        func keyframeGain(atFrame rel: Double) -> Double {
            guard !volumeKeyframes.isEmpty else { return 1 }
            if rel <= Double(volumeKeyframes[0].frame) {
                return VolumeScaleGain(volumeKeyframes[0].db)
            }
            for i in 1..<volumeKeyframes.count {
                let prev = volumeKeyframes[i - 1], next = volumeKeyframes[i]
                if rel <= Double(next.frame) {
                    let span = Double(next.frame - prev.frame)
                    let t = span > 0 ? (rel - Double(prev.frame)) / span : 1
                    return VolumeScaleGain(prev.db + (next.db - prev.db) * t)
                }
            }
            return VolumeScaleGain(volumeKeyframes[volumeKeyframes.count - 1].db)
        }
    }

    static let sampleRate = FFmpegAudioDecoder.sampleRate
    static let channels = FFmpegAudioDecoder.channels

    /// Decoded samples waiting for the audio thread. One producer (the feeder)
    /// and one consumer (the callback); the lock is held only across index
    /// arithmetic and a memcpy, never across I/O.
    private final class AudioRing {
        private var storage: [Float]
        private var count = 0
        private var readIndex = 0
        private let lock = NSLock()
        /// Source sample the *next* value written will correspond to. The
        /// feeder owns it; the callback reads it to know what it is draining.
        private(set) var writePosition: Int64 = 0

        init(capacityFrames: Int, channels: Int) {
            storage = [Float](repeating: 0, count: capacityFrames * channels)
        }

        var available: Int {
            lock.lock(); defer { lock.unlock() }
            return count
        }

        var free: Int {
            lock.lock(); defer { lock.unlock() }
            return storage.count - count
        }

        /// Drops everything buffered and restarts at `position` (a seek).
        func reset(to position: Int64) {
            lock.lock(); defer { lock.unlock() }
            count = 0
            readIndex = 0
            writePosition = position
        }

        /// Feeder side. `values` is interleaved and its length is the amount.
        func write(_ values: UnsafeBufferPointer<Float>, sourceFrames: Int) {
            lock.lock(); defer { lock.unlock() }
            let room = min(values.count, storage.count - count)
            guard room > 0 else { return }
            var writeIndex = (readIndex + count) % storage.count
            for i in 0..<room {
                storage[writeIndex] = values[i]
                writeIndex = writeIndex + 1 == storage.count ? 0 : writeIndex + 1
            }
            count += room
            writePosition += Int64(sourceFrames)
        }

        /// Callback side. Returns how many interleaved values were taken.
        func read(into out: UnsafeMutablePointer<Float>, values wanted: Int) -> Int {
            lock.lock(); defer { lock.unlock() }
            let taken = min(wanted, count)
            for i in 0..<taken {
                out[i] = storage[readIndex]
                readIndex = readIndex + 1 == storage.count ? 0 : readIndex + 1
            }
            count -= taken
            return taken
        }
    }

    private final class ClipPlayback {
        let source: ClipSource
        let ring: AudioRing
        /// Feeder-thread only, never touched by the callback.
        var decoder: FFmpegAudioDecoder?
        /// Whether the ring is aligned to a known timeline position. A fresh
        /// ring sits at 0, which is not where the clip starts.
        var positioned = false
        /// Earliest retry after a failed open; nil when there is none pending.
        var retryAfter: Date?
        /// Next source position the decoder will produce, in output samples.
        var decoderPosition: Int64 = -1
        init(source: ClipSource, capacityFrames: Int, channels: Int) {
            self.source = source
            ring = AudioRing(capacityFrames: capacityFrames, channels: channels)
        }
    }

    /// How far ahead of the playhead the feeder keeps each clip buffered.
    private static let bufferSeconds = 0.5

    private let stateLock = NSLock()
    private var clips: [ClipPlayback] = []
    private var playing = false
    private var positionSamples: Int64 = 0
    /// Feeder-thread scratch for decoded samples on their way into a ring.
    private var scratch = [Float](repeating: 0, count: 8192 * 2)
    /// Audio-thread scratch for samples on their way out of a ring.
    private var mixBuffer = [Float](repeating: 0, count: 8192 * 2)
    /// Bumped on every seek so the feeder discards work for the old position.
    private var seekGeneration = 0
    /// Last seek generation the feeder acted on. Feeder thread only.
    private var feederGeneration = -1

    private var feeder: Thread?
    private var running = true

    private let device: UnsafeMutablePointer<ma_device>
    private let timelineFps: Int

    public init?(timelineFps: Int = 30) {
        self.timelineFps = timelineFps
        device = .allocate(capacity: 1)
        var config = ma_device_config_init(ma_device_type_playback)
        config.playback.format = ma_format_f32
        config.playback.channels = ma_uint32(Self.channels)
        config.sampleRate = ma_uint32(Self.sampleRate)
        config.dataCallback = audioEngineDataCallback
        config.pUserData = Unmanaged.passUnretained(self).toOpaque()
        guard ma_device_init(nil, &config, device) == MA_SUCCESS else {
            device.deallocate()
            return nil
        }
        guard ma_device_start(device) == MA_SUCCESS else {
            ma_device_uninit(device)
            device.deallocate()
            return nil
        }
        // The strong reference is re-taken per iteration and dropped again, so
        // the last outside reference going away actually ends the thread.
        // Holding one across the whole loop — `[weak self] in self?.feed()` —
        // kept the engine alive forever, so deinit never ran, the device was
        // never uninitialised and audio outlived the project that owned it.
        let thread = Thread { [weak self] in
            while true {
                var alive = false
                if let engine = self { alive = engine.feedOnce() }
                guard alive else { return }   // no strong reference held while sleeping
                Thread.sleep(forTimeInterval: 0.01)
            }
        }
        thread.name = "palmier.audio.feeder"
        thread.start()
        feeder = thread
    }

    /// Stops output and ends the feeder. Idempotent; the engine is unusable
    /// afterwards. Called from the owning context's deinit so teardown is
    /// deterministic rather than whenever the last reference happens to drop.
    public func stop() {
        stateLock.lock()
        let wasRunning = running
        running = false
        stateLock.unlock()
        guard wasRunning else { return }
        ma_device_uninit(device)  // returns once the callback is no longer running
    }

    deinit {
        stop()
        device.deallocate()
    }

    /// Replaces the mixed clip set (call after any timeline edit). Keeps
    /// decoders and buffered audio for clips whose identity and placement are
    /// unchanged, so an unrelated edit does not interrupt what is playing.
    public func update(clips newSources: [ClipSource]) {
        let capacity = Int(Self.bufferSeconds * Double(Self.sampleRate))
        stateLock.lock()
        var existing = Dictionary(uniqueKeysWithValues: clips.map { ($0.source.id, $0) })
        clips = newSources.map { source in
            if let old = existing.removeValue(forKey: source.id), old.source == source { return old }
            return ClipPlayback(source: source, capacityFrames: capacity, channels: Self.channels)
        }
        stateLock.unlock()
    }

    public func setPlaying(_ play: Bool, atFrame frame: Int) {
        stateLock.lock()
        playing = play
        positionSamples = Int64(frame) * Int64(Self.sampleRate) / Int64(timelineFps)
        seekGeneration += 1
        scrubSamplesLeft = 0   // pausing must not trail a leftover scrub blip
        stateLock.unlock()
    }

    /// Samples of audio played per paused seek — the classic scrub blip. Long
    /// enough to recognise speech, short enough to stay a tick.
    private static let scrubBurstSamples = Int64(Double(sampleRate) * 0.06)
    private var scrubSamplesLeft: Int64 = 0

    public func seek(toFrame frame: Int) {
        stateLock.lock()
        positionSamples = Int64(frame) * Int64(Self.sampleRate) / Int64(timelineFps)
        seekGeneration += 1
        // Paused scrubbing still speaks: each seek plays a short burst from
        // the new position, which is how a cut point is found by ear.
        if !playing { scrubSamplesLeft = Self.scrubBurstSamples }
        stateLock.unlock()
    }

    /// Feeder thread, one pass: keeps every clip near the playhead buffered
    /// ahead. All file opening, seeking and decoding happens here and nowhere
    /// else. Returns false when the engine has stopped and the thread should
    /// end.
    private func feedOnce() -> Bool {
        stateLock.lock()
        let alive = running
        let snapshot = clips
        let position = positionSamples
        let generation = seekGeneration
        stateLock.unlock()
        guard alive else { return false }

        // A seek invalidates everything buffered: it belongs to the old
        // position and would play as a jump backwards.
        let seeked = generation != feederGeneration
        feederGeneration = generation

        for clip in snapshot {
            let clipStart = samples(fromFrame: clip.source.startFrame)
            let clipEnd = samples(fromFrame: clip.source.startFrame + clip.source.durationFrames)
            let horizon = position + Int64(Self.bufferSeconds * Double(Self.sampleRate))
            // Out of range: drop the decoder so idle clips cost nothing.
            guard clipStart < horizon, clipEnd > position else {
                if clip.decoder != nil || clip.positioned {
                    clip.decoder = nil
                    clip.decoderPosition = -1
                    clip.positioned = false
                    clip.ring.reset(to: 0)
                }
                continue
            }
            // A clip reaching the horizon during playback has to be placed at
            // the playhead just as a seek would place it. Without this its ring
            // is still at its initial 0, so the clip buffers from the file's
            // first sample and ignores its trim — heard as the wrong audio at
            // every clip's entry.
            if seeked || !clip.positioned {
                clip.ring.reset(to: max(position, clipStart))
                clip.decoderPosition = -1
                clip.positioned = true
            }
            topUp(clip, clipStart: clipStart, clipEnd: clipEnd)
        }
        return true
    }

    /// Fills one clip's ring up to its capacity. Feeder thread only.
    private func topUp(_ clip: ClipPlayback, clipStart: Int64, clipEnd: Int64) {
        if clip.decoder == nil {
            // A failed open is retried, slowly. Giving up permanently muted a
            // clip for the rest of the session over a file that was briefly
            // locked or still being written by a generation job.
            if let retryAfter = clip.retryAfter, retryAfter > Date() { return }
            guard let decoder = try? FFmpegAudioDecoder(path: clip.source.mediaRef) else {
                clip.retryAfter = Date().addingTimeInterval(1)
                return
            }
            clip.retryAfter = nil
            clip.decoder = decoder
        }
        guard let decoder = clip.decoder else { return }

        while clip.ring.free >= Self.channels * 1024 {
            let timelinePosition = clip.ring.writePosition
            guard timelinePosition < clipEnd else { return }
            let trim = samples(fromFrame: clip.source.trimStartFrame)
            let wanted = (timelinePosition - clipStart) + trim
            if clip.decoderPosition != wanted {
                decoder.seek(toSeconds: Double(wanted) / Double(Self.sampleRate))
                clip.decoderPosition = wanted
            }
            let request = min(1024, Int(clipEnd - timelinePosition))
            guard request > 0 else { return }
            if scratch.count < request * Self.channels {
                scratch = [Float](repeating: 0, count: request * Self.channels)
            }
            let got = scratch.withUnsafeMutableBufferPointer {
                decoder.read(into: $0.baseAddress!, sampleFrames: request)
            }
            guard got > 0 else { return }   // end of source
            clip.decoderPosition += Int64(got)
            scratch.withUnsafeBufferPointer {
                clip.ring.write(UnsafeBufferPointer(start: $0.baseAddress!, count: got * Self.channels),
                                sourceFrames: got)
            }
        }
    }

    private func samples(fromFrame frame: Int) -> Int64 {
        Int64(frame) * Int64(Self.sampleRate) / Int64(timelineFps)
    }

    /// Mixes `frameCount` output frames into `out` (called on the audio thread).
    /// Audio thread. Drains each clip's ring, applies gain and sums. No file
    /// access, no seeking, no decoding, and no allocation on this path.
    fileprivate func render(into out: UnsafeMutablePointer<Float>, frameCount: Int) {
        // Output starts zeroed (MA_NO_... config keeps default silence fill).
        memset(out, 0, frameCount * Self.channels * MemoryLayout<Float>.size)
        stateLock.lock()
        let snapshot = clips
        let rangeStart = positionSamples
        // A scrub burst is playback for a bounded run of samples; it advances
        // the position like play does, so consecutive callbacks continue the
        // sound instead of repeating the same chunk.
        let bursting = !playing && scrubSamplesLeft > 0
        let isPlaying = playing || bursting
        if isPlaying {
            positionSamples = rangeStart + Int64(frameCount)
            if bursting { scrubSamplesLeft = max(0, scrubSamplesLeft - Int64(frameCount)) }
        }
        stateLock.unlock()
        guard isPlaying else { return }
        let rangeEnd = rangeStart + Int64(frameCount)

        if mixBuffer.count < frameCount * Self.channels {
            // Only ever grows on a buffer-size change, not per callback.
            mixBuffer = [Float](repeating: 0, count: frameCount * Self.channels)
        }

        for clip in snapshot {
            let clipStart = Int64(clip.source.startFrame) * Int64(Self.sampleRate) / Int64(timelineFps)
            let clipEnd = Int64(clip.source.startFrame + clip.source.durationFrames)
                * Int64(Self.sampleRate) / Int64(timelineFps)
            guard clipStart < rangeEnd, clipEnd > rangeStart else { continue }

            let mixStart = max(clipStart, rangeStart)
            let mixEnd = min(clipEnd, rangeEnd)
            let mixFrames = Int(mixEnd - mixStart)
            guard mixFrames > 0 else { continue }
            let outOffset = Int(mixStart - rangeStart)

            // Whatever the feeder has managed to buffer. Short means it is
            // still catching up after a seek; silence beats a stall.
            let got = mixBuffer.withUnsafeMutableBufferPointer {
                clip.ring.read(into: $0.baseAddress!, values: mixFrames * Self.channels)
            } / Self.channels
            guard got > 0 else { continue }

            // Static volume × keyframe envelope × linear fade envelope, all
            // evaluated at the chunk's midpoint (≤ ~10 ms — inaudible steps).
            var gain = Float(clip.source.volume)
            let midSample = mixStart + Int64(got) / 2 - clipStart
            let rel = Double(midSample) * Double(timelineFps) / Double(Self.sampleRate)
            if !clip.source.volumeKeyframes.isEmpty {
                gain *= Float(clip.source.keyframeGain(atFrame: rel))
            }
            if clip.source.fadeInFrames > 0 || clip.source.fadeOutFrames > 0 {
                var envelope = 1.0
                if clip.source.fadeInFrames > 0 {
                    envelope = min(envelope, min(1, rel / Double(clip.source.fadeInFrames)))
                }
                if clip.source.fadeOutFrames > 0 {
                    let remaining = Double(clip.source.durationFrames) - rel
                    envelope = min(envelope, max(0, min(1, remaining / Double(clip.source.fadeOutFrames))))
                }
                gain *= Float(envelope)
            }
            mixBuffer.withUnsafeBufferPointer { src in
                for i in 0..<(got * Self.channels) {
                    out[outOffset * Self.channels + i] += src[i] * gain
                }
            }
        }
    }
}

/// dB → linear gain (mirrors PalmierCore's VolumeScale without the import).
@inline(__always)
private func VolumeScaleGain(_ db: Double) -> Double {
    pow(10.0, db / 20.0)
}

/// C data callback: routes to the engine instance via pUserData.
private func audioEngineDataCallback(_ device: UnsafeMutablePointer<ma_device>?,
                                     _ output: UnsafeMutableRawPointer?,
                                     _ input: UnsafeRawPointer?,
                                     _ frameCount: ma_uint32) {
    guard let device, let output, let userData = device.pointee.pUserData else { return }
    let engine = Unmanaged<WinAudioEngine>.fromOpaque(userData).takeUnretainedValue()
    engine.render(into: output.assumingMemoryBound(to: Float.self), frameCount: Int(frameCount))
}
