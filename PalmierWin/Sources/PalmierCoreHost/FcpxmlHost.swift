import CFFmpeg
import Foundation
import PalmierCore

// FCPXML export ABI: serializes the project's active timeline to Final Cut
// Pro XML (v1.10, the broadest version Resolve 18+ / FCP 10.6+ accept) and
// writes it atomically. The document shape mirrors the macOS exporter in
// Sources/PalmierPro/Export/FCPXMLExporter.swift (Resolve dialect): sequence
// format + asset resources, one library/event/project/sequence whose spine is
// a single gap with every clip connected on a lane, rational times against
// the timeline fps, linked A/V pairs merged into one asset-clip, and one-sided
// A/V media gated through a compound ref-clip (Resolve honors srcEnable only
// there).
//
// Transports: placement/trims, speed (timeMap), lane order, enabled state,
// static position/scale/rotation/flip, crop, static opacity, static volume,
// text clips as Basic Title elements with font/size/color/alignment/stroke.
// Dropped: keyframes, fades, nested sequences, lottie, effects/color, text
// animations/backgrounds, embedded source timecodes.

/// Writes the active timeline of `project` as FCPXML v1.10 to `path`
/// (NUL-terminated UTF-8). Returns 1 on success, 0 on failure (invalid
/// arguments, empty timeline, unwritable destination).
@_cdecl("palmier_export_fcpxml")
public func palmierExportFcpxml(_ projectHandle: UnsafeMutableRawPointer?,
                                _ path: UnsafePointer<CChar>?) -> Int32 {
    guard let projectHandle, let path else { return 0 }
    let project = Unmanaged<ProjectContext>.fromOpaque(projectHandle).takeUnretainedValue()
    let outputPath = String(cString: path)
    let timeline = project.snapshot()
    guard timeline.totalFrames > 0 else { return 0 }
    let size = project.renderSize
    let xml = FcpxmlWriter(timeline: timeline, width: size.width, height: size.height).render()
    guard let data = xml.data(using: .utf8) else { return 0 }
    return writeFcpxmlAtomically(data, to: outputPath) ? 1 : 0
}

/// Stages the document next to the destination, then swaps it in, so a
/// failed write never leaves a truncated file at the user's path.
private func writeFcpxmlAtomically(_ data: Data, to outputPath: String) -> Bool {
    let directory = (outputPath as NSString).deletingLastPathComponent
    let tempPath = (directory as NSString)
        .appendingPathComponent(".palmier-fcpxml-\(UUID().uuidString).tmp")
    let fm = FileManager.default
    do {
        try data.write(to: URL(fileURLWithPath: tempPath))
        if fm.fileExists(atPath: outputPath) { try fm.removeItem(atPath: outputPath) }
        try fm.moveItem(atPath: tempPath, toPath: outputPath)
        return true
    } catch {
        try? fm.removeItem(atPath: tempPath)
        return false
    }
}

private final class FcpxmlWriter {
    private struct EmittableClip {
        let clip: Clip
        let lane: Int
        let enabled: Bool
    }

    /// Probed source properties; nil when the file could not be opened.
    private struct MediaInfo {
        var width = 0
        var height = 0
        var fps = 0.0
        var durationSeconds = 0.0
        var hasVideo = false
        var hasAudio = false
    }

    private struct Resource {
        let path: String
        let assetId: String
        var formatId: String?
        var compoundId: String?
        var durationFrames: Int
        var hasVideo: Bool
        var hasAudio: Bool
        var info: MediaInfo?
    }

    private let timeline: Timeline
    private let fps: Int
    private let seqWidth: Int
    private let seqHeight: Int
    private let sequenceFormatId = "r1"
    private let titleEffectId = "titleBasic"
    private var resourceIndex: [String: Int] = [:]
    private var resources: [Resource] = []
    private var probeCache: [String: MediaInfo?] = [:]
    private var nextTextStyleId = 1
    // Synced pairs export as one asset-clip while retaining audio volume.
    private var linkedAudioForVideo: [String: Clip] = [:]
    private var redundantAudioClipIds: Set<String> = []
    private var usedCompoundIds: Set<String> = []

    /// `width`/`height` are the project's render size: the sequence the XML
    /// describes composites at that canvas, whatever the timeline defaults say.
    init(timeline: Timeline, width: Int, height: Int) {
        self.timeline = timeline
        self.fps = max(1, timeline.fps)
        self.seqWidth = width
        self.seqHeight = height
    }

    func render() -> String {
        let clips = emittableClips()
        collectResources(from: clips)
        indexLinkedPairs(clips)
        markUsedCompounds(clips)
        let hasTitles = clips.contains { $0.clip.mediaType == .text }
        let root = FcpxmlNode(name: "fcpxml", attributes: [("version", "1.10")], children: [
            resourcesNode(hasTitles: hasTitles),
            FcpxmlNode(name: "library", children: [
                FcpxmlNode(name: "event", attributes: [("name", "Palmier Export")], children: [
                    projectNode(clips: clips),
                ]),
            ]),
        ])
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE fcpxml>\n" + renderFcpxml(root, indent: 0)
    }

    private func projectNode(clips: [EmittableClip]) -> FcpxmlNode {
        let duration = time(frames: timeline.totalFrames)
        return FcpxmlNode(name: "project", attributes: [("name", timeline.name)], children: [
            FcpxmlNode(name: "sequence", attributes: [
                ("format", sequenceFormatId),
                ("duration", duration),
                ("tcStart", "0s"),
                ("tcFormat", "NDF"),
                ("audioLayout", "stereo"),
                ("audioRate", "48k"),
            ], children: [
                FcpxmlNode(name: "spine", children: [
                    FcpxmlNode(name: "gap", attributes: [
                        ("name", "Timeline"),
                        ("offset", "0s"),
                        ("start", "0s"),
                        ("duration", duration),
                    ], children: storyNodes(for: clips)),
                ]),
            ]),
        ])
    }

    // MARK: Resources

    private func resourcesNode(hasTitles: Bool) -> FcpxmlNode {
        var children: [FcpxmlNode] = [
            FcpxmlNode(name: "format", attributes: [
                ("id", sequenceFormatId),
                ("name", formatName(width: seqWidth, height: seqHeight, fps: Double(fps)) ?? "FFVideoFormatRateUndefined"),
                ("frameDuration", frameDuration(forFPS: Double(fps))),
                ("width", "\(seqWidth)"),
                ("height", "\(seqHeight)"),
                ("colorSpace", "1-1-1 (Rec. 709)"),
            ]),
        ]
        if hasTitles {
            children.append(FcpxmlNode(name: "effect", attributes: [
                ("id", titleEffectId),
                ("name", "Basic Title"),
                ("uid", ".../Titles.localized/Bumper:Opener.localized/Basic Title.localized/Basic Title.moti"),
            ]))
        }
        children += resources.compactMap(formatNode)
        children += resources.map(assetNode)
        children += resources.compactMap(compoundClipNode)
        return FcpxmlNode(name: "resources", children: children)
    }

    private func collectResources(from clips: [EmittableClip]) {
        var order: [String] = []
        for item in clips {
            let clip = item.clip
            guard clip.mediaType != .text else { continue }
            let path = clip.mediaRef
            let info = mediaInfo(for: path)
            // Video resources include source audio when present.
            let isVisual = clip.mediaType != .audio
            let isAudio = clip.mediaType == .audio || (isVisual && info?.hasAudio == true)
            let clipFrames = max(0, clip.sourceDurationFrames)
            let probedFrames = max(0, secondsToFrames(info?.durationSeconds ?? 0))
            if let i = resourceIndex[path] {
                resources[i].hasVideo = resources[i].hasVideo || isVisual
                resources[i].hasAudio = resources[i].hasAudio || isAudio
                resources[i].durationFrames = max(resources[i].durationFrames, clipFrames, probedFrames)
                continue
            }
            resourceIndex[path] = resources.count
            resources.append(Resource(
                path: path,
                assetId: "asset\(resources.count + 1)",
                formatId: nil,
                compoundId: nil,
                durationFrames: max(clipFrames, probedFrames),
                hasVideo: isVisual,
                hasAudio: isAudio,
                info: info
            ))
        }
        // Derive ids only after every clip merged into the resource: an audio
        // clip can be visited before its video sibling. Only A/V sources need
        // srcEnable gating through a compound.
        for i in resources.indices {
            let id = i + 1
            if resources[i].hasVideo, resources[i].info?.width ?? 0 > 0 {
                resources[i].formatId = "r\(id + 1)"
            }
            if resources[i].hasVideo && resources[i].hasAudio {
                resources[i].compoundId = "media\(id)"
            }
        }
    }

    private func formatNode(for resource: Resource) -> FcpxmlNode? {
        guard let formatId = resource.formatId, let info = resource.info else { return nil }
        let rawFPS = info.fps > 0 ? info.fps : Double(fps)
        return FcpxmlNode(name: "format", attributes: [
            ("id", formatId),
            ("name", formatName(width: info.width, height: info.height, fps: rawFPS)
                ?? "FFVideoFormat\(info.width)x\(info.height)p\(rateSuffix(forFPS: rawFPS))"),
            ("frameDuration", frameDuration(forFPS: rawFPS)),
            ("width", "\(info.width)"),
            ("height", "\(info.height)"),
            ("colorSpace", "1-1-1 (Rec. 709)"),
        ])
    }

    private func assetNode(for resource: Resource) -> FcpxmlNode {
        var attrs: [(String, String)] = [
            ("id", resource.assetId),
            ("name", fileName(for: resource)),
            ("start", "0s"),
            ("duration", time(frames: resource.durationFrames)),
        ]
        if resource.hasVideo {
            attrs.append(("hasVideo", "1"))
            attrs.append(("videoSources", "1"))
            if let formatId = resource.formatId {
                attrs.append(("format", formatId))
            }
        }
        if resource.hasAudio {
            // Default audio metadata does not affect relinking.
            attrs.append(("hasAudio", "1"))
            attrs.append(("audioSources", "1"))
            attrs.append(("audioChannels", "2"))
            attrs.append(("audioRate", "48000"))
        }
        return FcpxmlNode(name: "asset", attributes: attrs, children: [
            FcpxmlNode(name: "media-rep", attributes: [
                ("kind", "original-media"),
                ("src", mediaSrc(for: resource)),
            ]),
        ])
    }

    private func compoundClipNode(for resource: Resource) -> FcpxmlNode? {
        guard let compoundId = resource.compoundId, usedCompoundIds.contains(compoundId) else { return nil }
        let dur = time(frames: resource.durationFrames)
        let innerClip = FcpxmlNode(name: "asset-clip", attributes: [
            ("ref", resource.assetId),
            ("name", fileName(for: resource)),
            ("duration", dur),
            ("start", "0s"),
            ("offset", "0s"),
            ("format", resource.formatId ?? sequenceFormatId),
        ])
        let sequence = FcpxmlNode(name: "sequence", attributes: [
            ("format", resource.formatId ?? sequenceFormatId),
            ("duration", dur),
            ("tcStart", "0s"),
            ("tcFormat", "NDF"),
        ], children: [FcpxmlNode(name: "spine", children: [innerClip])])
        return FcpxmlNode(name: "media", attributes: [
            ("id", compoundId),
            ("name", fileName(for: resource)),
        ], children: [sequence])
    }

    // MARK: Clips

    private func storyNodes(for clips: [EmittableClip]) -> [FcpxmlNode] {
        clips
            .filter { !redundantAudioClipIds.contains($0.clip.id) }
            .sorted {
                if $0.clip.startFrame != $1.clip.startFrame { return $0.clip.startFrame < $1.clip.startFrame }
                return $0.lane < $1.lane
            }
            .compactMap { item in
                item.clip.mediaType == .text ? titleNode(for: item) : assetClipNode(for: item)
            }
    }

    private func assetClipNode(for item: EmittableClip) -> FcpxmlNode? {
        let clip = item.clip
        guard let i = resourceIndex[clip.mediaRef] else { return nil }
        let resource = resources[i]
        let linkedAudio = linkedAudioForVideo[clip.id]

        // Resolve honors srcEnable only on ref-clips.
        if let compoundId = resource.compoundId, linkedAudio == nil {
            let videoOnly = clip.mediaType != .audio
            let attrs: [(String, String)] = [
                ("ref", compoundId),
                ("name", fileName(for: resource)),
                ("lane", "\(item.lane)"),
                ("offset", time(frames: clip.startFrame)),
                ("start", clipStart(for: clip)),
                ("duration", time(frames: clip.durationFrames)),
                ("enabled", item.enabled ? "1" : "0"),
                ("srcEnable", videoOnly ? "video" : "audio"),
            ]
            // Child order is fixed by the DTD.
            let children: [FcpxmlNode?] = videoOnly
                ? [timeMapNode(for: clip, mediaFrames: resource.durationFrames),
                   cropNode(for: clip),
                   FcpxmlNode(name: "adjust-conform", attributes: [("type", "fit")]),
                   transformNode(for: clip),
                   blendNode(for: clip)]
                : [timeMapNode(for: clip, mediaFrames: resource.durationFrames),
                   volumeNode(for: clip)]
            return FcpxmlNode(name: "ref-clip", attributes: attrs, children: children.compactMap { $0 })
        }

        let visual = clip.mediaType != .audio
        let attrs: [(String, String)] = [
            ("ref", resource.assetId),
            ("name", fileName(for: resource)),
            ("lane", "\(item.lane)"),
            ("offset", time(frames: clip.startFrame)),
            ("start", clipStart(for: clip)),
            ("duration", time(frames: clip.durationFrames)),
            ("enabled", item.enabled ? "1" : "0"),
        ]
        let children: [FcpxmlNode?] = [
            timeMapNode(for: clip, mediaFrames: resource.durationFrames),
            visual ? cropNode(for: clip) : nil,
            visual ? FcpxmlNode(name: "adjust-conform", attributes: [("type", "fit")]) : nil,
            visual ? transformNode(for: clip) : nil,
            visual ? blendNode(for: clip) : nil,
            resource.hasAudio ? volumeNode(for: linkedAudio ?? clip) : nil,
        ]
        // Final Cut writes stills as video elements.
        return FcpxmlNode(name: clip.mediaType == .image ? "video" : "asset-clip",
                          attributes: attrs, children: children.compactMap { $0 })
    }

    private func titleNode(for item: EmittableClip) -> FcpxmlNode? {
        let clip = item.clip
        guard let content = clip.textContent, !content.isEmpty else { return nil }
        let style = clip.textStyle ?? TextStyle()
        let styleId = "textStyle\(nextTextStyleId)"
        nextTextStyleId += 1

        var textNodes: [FcpxmlNode] = [
            FcpxmlNode(name: "text", children: [
                FcpxmlNode(name: "text-style", attributes: [("ref", styleId)], text: style.displayText(content)),
            ]),
            FcpxmlNode(name: "text-style-def", attributes: [("id", styleId)], children: [
                FcpxmlNode(name: "text-style", attributes: textStyleAttributes(for: style)),
            ]),
            FcpxmlNode(name: "adjust-conform", attributes: [("type", "fit")]),
            FcpxmlNode(name: "adjust-transform", attributes: [
                ("scale", "\(formatNumber(style.widthScale)) \(formatNumber(style.heightScale))"),
                ("anchor", "0 0"),
                ("position", positionValue(for: clip.transform)),
            ]),
        ]
        if let blend = blendNode(for: clip) { textNodes.append(blend) }
        return FcpxmlNode(name: "title", attributes: [
            ("ref", titleEffectId),
            ("name", content),
            ("lane", "\(item.lane)"),
            ("offset", time(frames: clip.startFrame)),
            ("start", "0s"),
            ("duration", time(frames: clip.durationFrames)),
            ("enabled", item.enabled ? "1" : "0"),
        ], children: textNodes)
    }

    // MARK: Clip adjustments

    private func blendNode(for clip: Clip) -> FcpxmlNode? {
        guard clip.opacity < 0.9995 else { return nil }
        return FcpxmlNode(name: "adjust-blend", attributes: [("amount", formatNumber(clip.opacity))])
    }

    private func transformNode(for clip: Clip) -> FcpxmlNode? {
        let t = clip.transform
        let base = scaleValue(for: clip)
        let moved = abs(t.centerX - 0.5) > 0.0005 || abs(t.centerY - 0.5) > 0.0005
        let rotated = abs(t.rotation) > 0.005
        let scaled = base != "1 1"
        guard moved || rotated || scaled else { return nil }

        let fit = fitFractions(for: clip)
        var attrs: [(String, String)] = [("scale", base)]
        if rotated { attrs.append(("rotation", formatNumber(-t.rotation))) }
        attrs.append(("anchor", "0 0"))
        attrs.append(("position", positionValue(for: t, fit: fit)))
        return FcpxmlNode(name: "adjust-transform", attributes: attrs)
    }

    /// Removes aspect-fit scaling from the exported user scale.
    private func scaleValue(for clip: Clip) -> String {
        let fit = fitFractions(for: clip)
        var sx = clip.transform.width / fit.w
        var sy = clip.transform.height / fit.h
        if clip.transform.flipHorizontal { sx = -sx }
        if clip.transform.flipVertical { sy = -sy }
        return "\(formatNumber(sx)) \(formatNumber(sy))"
    }

    /// Uses Resolve trim units when source dimensions are known.
    private func cropNode(for clip: Clip) -> FcpxmlNode? {
        let c = clip.crop
        guard !c.isIdentity else { return nil }
        var lr = 100.0, tb = 100.0
        if let info = mediaInfo(for: clip.mediaRef), info.width > 0, info.height > 0 {
            let fit = min(Double(seqWidth) / Double(info.width), Double(seqHeight) / Double(info.height))
            lr = Double(info.width) * 100.0 / Double(seqHeight)
            tb = 100.0 / fit
        }
        return FcpxmlNode(name: "adjust-crop", attributes: [("mode", "trim")], children: [
            FcpxmlNode(name: "trim-rect", attributes: [
                ("top", formatNumber(c.top * tb)),
                ("right", formatNumber(c.right * lr)),
                ("bottom", formatNumber(c.bottom * tb)),
                ("left", formatNumber(c.left * lr)),
            ]),
        ])
    }

    private func volumeNode(for clip: Clip) -> FcpxmlNode? {
        guard abs(clip.volume - 1.0) > 0.0005 else { return nil }
        let db = clip.volume > 0 ? 20.0 * log10(clip.volume) : -96.0
        return FcpxmlNode(name: "adjust-volume", attributes: [("amount", formatNumber(db))])
    }

    /// Uses the time-map output axis for retimed clips.
    private func clipStart(for clip: Clip) -> String {
        guard abs(clip.speed - 1.0) > 0.001 else { return time(frames: clip.trimStartFrame) }
        let (p, q) = rationalSpeed(clip.speed)
        return rationalTime(num: clip.trimStartFrame * q, den: fps * p)
    }

    /// Maps the full asset so retimed clips do not end on black frames.
    private func timeMapNode(for clip: Clip, mediaFrames: Int) -> FcpxmlNode? {
        guard abs(clip.speed - 1.0) > 0.001, mediaFrames > 0 else { return nil }
        let (p, q) = rationalSpeed(clip.speed)
        return FcpxmlNode(name: "timeMap", attributes: [("frameSampling", "floor")], children: [
            FcpxmlNode(name: "timept", attributes: [
                ("time", "0s"), ("value", "0s"), ("interp", "linear"),
            ]),
            FcpxmlNode(name: "timept", attributes: [
                ("time", rationalTime(num: mediaFrames * q, den: fps * p)),
                ("value", time(frames: mediaFrames)),
                ("interp", "linear"),
            ]),
        ])
    }

    private func textStyleAttributes(for style: TextStyle) -> [(String, String)] {
        let style = style.scaledVisualStyle
        var attrs: [(String, String)] = [
            ("font", fontFamilyFallback(style.fontName)),
            ("fontFace", fontFace(isBold: style.isBold, isItalic: style.isItalic)),
            ("fontSize", formatNumber(style.fontSize)),
            ("fontColor", colorString(style.color)),
            ("alignment", style.alignment.rawValue),
        ]
        if style.border.enabled {
            attrs.append(("strokeColor", colorString(style.border.color)))
            attrs.append(("strokeWidth", formatNumber(max(0, style.border.width))))
        }
        return attrs
    }

    // MARK: Discovery

    private func indexLinkedPairs(_ clips: [EmittableClip]) {
        var byGroup: [String: (videos: [EmittableClip], audios: [EmittableClip])] = [:]
        for item in clips {
            guard let group = item.clip.linkGroupId else { continue }
            if item.clip.mediaType == .audio {
                byGroup[group, default: ([], [])].audios.append(item)
            } else {
                byGroup[group, default: ([], [])].videos.append(item)
            }
        }
        for (_, pair) in byGroup {
            guard pair.videos.count == 1, pair.audios.count == 1 else { continue }
            let v = pair.videos[0], a = pair.audios[0]
            guard v.clip.mediaRef == a.clip.mediaRef, v.enabled == a.enabled,
                  v.clip.startFrame == a.clip.startFrame, v.clip.durationFrames == a.clip.durationFrames,
                  v.clip.trimStartFrame == a.clip.trimStartFrame, abs(v.clip.speed - a.clip.speed) < 0.0001
            else { continue }
            linkedAudioForVideo[v.clip.id] = a.clip
            redundantAudioClipIds.insert(a.clip.id)
        }
    }

    // Emit compound resources only when referenced.
    private func markUsedCompounds(_ clips: [EmittableClip]) {
        for item in clips where !redundantAudioClipIds.contains(item.clip.id) {
            guard let i = resourceIndex[item.clip.mediaRef],
                  let compoundId = resources[i].compoundId,
                  linkedAudioForVideo[item.clip.id] == nil else { continue }
            usedCompoundIds.insert(compoundId)
        }
    }

    private func emittableClips() -> [EmittableClip] {
        let visualTrackCount = timeline.tracks.filter { $0.type.isVisual }.count
        var visualOrdinal = 0
        var audioOrdinal = 0
        var clips: [EmittableClip] = []

        for track in timeline.tracks {
            let lane: Int
            let enabled: Bool
            if track.type.isVisual {
                lane = visualTrackCount - visualOrdinal
                enabled = !track.hidden
                visualOrdinal += 1
            } else if track.type == .audio {
                lane = -(audioOrdinal + 1)
                enabled = !track.muted
                audioOrdinal += 1
            } else {
                continue
            }
            clips += track.clips
                .filter(isEmittable)
                .sorted { $0.startFrame < $1.startFrame }
                .map { EmittableClip(clip: $0, lane: lane, enabled: enabled) }
        }
        return clips
    }

    private func isEmittable(_ clip: Clip) -> Bool {
        guard clip.durationFrames > 0 else { return false }
        switch clip.mediaType {
        case .text:
            return clip.textContent?.isEmpty == false
        case .lottie, .sequence:
            return false
        case .audio, .video, .image:
            return FileManager.default.fileExists(atPath: clip.mediaRef)
        }
    }

    // MARK: Probing

    private func mediaInfo(for path: String) -> MediaInfo? {
        if let cached = probeCache[path] { return cached }
        let info = Self.probe(path)
        probeCache[path] = info
        return info
    }

    /// Reads container facts via libavformat without decoding.
    private static func probe(_ path: String) -> MediaInfo? {
        var fmtCtx: UnsafeMutablePointer<AVFormatContext>? = nil
        guard path.withCString({ avformat_open_input(&fmtCtx, $0, nil, nil) }) == 0, let fmt = fmtCtx else { return nil }
        defer { var f: UnsafeMutablePointer<AVFormatContext>? = fmt; avformat_close_input(&f) }
        guard avformat_find_stream_info(fmt, nil) >= 0 else { return nil }

        var info = MediaInfo()
        info.hasAudio = av_find_best_stream(fmt, AVMEDIA_TYPE_AUDIO, -1, -1, nil, 0) >= 0
        let vidx = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, nil, 0)
        if vidx >= 0,
           let streamsBase = fmt.pointee.streams,
           Int(vidx) < Int(fmt.pointee.nb_streams),
           let stream = streamsBase[Int(vidx)],
           let par = stream.pointee.codecpar {
            info.hasVideo = true
            info.width = Int(par.pointee.width)
            info.height = Int(par.pointee.height)
            let rate = stream.pointee.avg_frame_rate
            if rate.num > 0 && rate.den > 0 { info.fps = Double(rate.num) / Double(rate.den) }
        }
        // Container duration is in AV_TIME_BASE (microseconds) units.
        let duration = fmt.pointee.duration
        if duration > 0 { info.durationSeconds = Double(duration) / 1_000_000 }
        return info.hasVideo || info.hasAudio ? info : nil
    }

    // MARK: Value formatting

    private func secondsToFrames(_ seconds: Double) -> Int {
        Int((seconds * Double(fps)).rounded())
    }

    private func time(frames: Int) -> String {
        rationalTime(num: frames, den: fps)
    }

    private func rationalTime(num: Int, den: Int) -> String {
        guard num != 0 else { return "0s" }
        let g = gcd(abs(num), abs(den))
        let n = num / g, d = den / g
        return d == 1 ? "\(n)s" : "\(n)/\(d)s"
    }

    /// Approximates user-entered speed with a small exact fraction.
    private func rationalSpeed(_ speed: Double) -> (p: Int, q: Int) {
        var best = (p: 1, q: 1), bestErr = Double.infinity
        for q in 1...1000 {
            let p = Int((speed * Double(q)).rounded())
            guard p > 0 else { continue }
            let err = abs(speed - Double(p) / Double(q))
            if err < bestErr { best = (p, q); bestErr = err; if err == 0 { break } }
        }
        return best
    }

    // Resolve relinks by the full filename, including its extension.
    private func fileName(for resource: Resource) -> String {
        (resource.path as NSString).lastPathComponent
    }

    // Resolve cannot relink sub-delimiters encoded as XML entities.
    private func mediaSrc(for resource: Resource) -> String {
        fileURLString(for: resource.path).map { ch in
            "'!$&()*+,;=".contains(ch) ? String(format: "%%%02X", ch.asciiValue ?? 0) : String(ch)
        }.joined()
    }

    private func fileURLString(for path: String) -> String {
        URL(fileURLWithPath: path).absoluteString
    }

    private func positionValue(for transform: Transform, fit: (w: Double, h: Double) = (1, 1)) -> String {
        let unit = Double(seqHeight) / 100.0
        let x = (transform.centerX - 0.5) * Double(seqWidth) / unit / fit.w
        let y = (0.5 - transform.centerY) * Double(seqHeight) / unit / fit.h
        return "\(formatNumber(x)) \(formatNumber(y))"
    }

    /// Returns per-axis conform-fit fractions, or 1×1 without source dimensions.
    private func fitFractions(for clip: Clip) -> (w: Double, h: Double) {
        guard let info = mediaInfo(for: clip.mediaRef), info.width > 0, info.height > 0 else { return (1, 1) }
        let sourceAspect = Double(info.width) / Double(info.height)
        let frameAspect = Double(seqWidth) / Double(seqHeight)
        return sourceAspect >= frameAspect
            ? (1, frameAspect / sourceAspect)
            : (sourceAspect / frameAspect, 1)
    }

    private func fontFamilyFallback(_ fontName: String) -> String {
        fontName.split(separator: "-", maxSplits: 1).first.map(String.init) ?? fontName
    }

    private func fontFace(isBold: Bool, isItalic: Bool) -> String {
        switch (isBold, isItalic) {
        case (true, true): return "Bold Italic"
        case (true, false): return "Bold"
        case (false, true): return "Italic"
        case (false, false): return "Regular"
        }
    }

    private func colorString(_ color: TextStyle.RGBA) -> String {
        "\(formatNumber(color.r)) \(formatNumber(color.g)) \(formatNumber(color.b)) \(formatNumber(color.a))"
    }

    private func formatName(width: Int, height: Int, fps rawFPS: Double) -> String? {
        let rate = rateSuffix(forFPS: rawFPS)
        switch (width, height) {
        case (1280, 720): return "FFVideoFormat720p\(rate)"
        case (1920, 1080): return "FFVideoFormat1080p\(rate)"
        case (3840, 2160): return "FFVideoFormat3840x2160p\(rate)"
        case (4096, 2160): return "FFVideoFormat4096x2160p\(rate)"
        default: return nil
        }
    }

    private func rateSuffix(forFPS rawFPS: Double) -> String {
        let rounded = max(1, Int(rawFPS.rounded()))
        let ntscRate = Double(rounded) * 1000.0 / 1001.0
        if abs(rawFPS - ntscRate) < abs(rawFPS - Double(rounded)) {
            let fps100 = Int((ntscRate * 100.0).rounded())
            return "\(fps100 / 100)\(String(format: "%02d", fps100 % 100))"
        }
        return "\(rounded)"
    }

    private func frameDuration(forFPS rawFPS: Double) -> String {
        let rounded = max(1, Int(rawFPS.rounded()))
        let ntscRate = Double(rounded) * 1000.0 / 1001.0
        if abs(rawFPS - ntscRate) < abs(rawFPS - Double(rounded)) {
            return "1001/\(rounded * 1000)s"
        }
        return "1/\(rounded)s"
    }

    private func formatNumber(_ value: Double) -> String {
        let rounded = (value * 10000).rounded() / 10000
        if rounded == rounded.rounded() { return "\(Int(rounded))" }
        var s = String(format: "%.4f", rounded)
        while s.last == "0" { s.removeLast() }
        if s.last == "." { s.removeLast() }
        return s
    }

    private func gcd(_ a: Int, _ b: Int) -> Int {
        var x = a, y = b
        while y != 0 {
            let r = x % y
            x = y
            y = r
        }
        return max(1, x)
    }
}

private struct FcpxmlNode {
    let name: String
    var attributes: [(String, String)] = []
    var text: String? = nil
    var children: [FcpxmlNode] = []
}

private func renderFcpxml(_ node: FcpxmlNode, indent: Int) -> String {
    let pad = String(repeating: " ", count: indent)
    let attrs = node.attributes.map { " \($0.0)=\"\(escapeFcpxml($0.1))\"" }.joined()
    if let text = node.text {
        return "\(pad)<\(node.name)\(attrs)>\(escapeFcpxml(text))</\(node.name)>"
    }
    guard !node.children.isEmpty else { return "\(pad)<\(node.name)\(attrs)/>" }
    let inner = node.children.map { renderFcpxml($0, indent: indent + 2) }.joined(separator: "\n")
    return "\(pad)<\(node.name)\(attrs)>\n\(inner)\n\(pad)</\(node.name)>"
}

private func escapeFcpxml(_ s: String) -> String {
    s.replacingOccurrences(of: "&", with: "&amp;")
     .replacingOccurrences(of: "<", with: "&lt;")
     .replacingOccurrences(of: ">", with: "&gt;")
     .replacingOccurrences(of: "\"", with: "&quot;")
     .replacingOccurrences(of: "'", with: "&apos;")
}
