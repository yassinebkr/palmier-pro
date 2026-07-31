import CImGui
import CVulkan
import PalmierCore
import Foundation

/// Styled Palmier Pro editor shell rendered via ImGui. Ports the exact AppTheme
/// palette, recreates the macOS panel layout (media sidebar, video preview,
/// inspector, AI agent, timeline), and custom-draws the timeline with clip blocks
/// + playhead via ImGui's draw-list API.
///
/// ImGui master has no docking — panels are positioned manually via child windows.
/// The layout matches the macOS "default" preset: top row [Media | Preview |
/// Inspector/Agent], bottom = [Timeline].
public final class WinEditorUI: @unchecked Sendable {
    // AppTheme color constants (exact values from Sources/PalmierPro/UI/AppTheme.swift).
    // Stored as RGBA floats for ImGui's style system.
    private enum Theme {
        // Backgrounds (neutral grayscale).
        static let base:    (Float, Float, Float) = (10/255, 10/255, 10/255)       // #0A0A0A
        static let surface: (Float, Float, Float) = (22/255, 22/255, 22/255)       // #161616
        static let raised:  (Float, Float, Float) = (30/255, 30/255, 30/255)       // #1E1E1E
        static let prominent: (Float, Float, Float) = (44/255, 44/255, 44/255)     // #2C2C2C

        // Accent.
        static let timecode: (Float, Float, Float) = (0.95, 0.6, 0.2)              // #F29933
        static let primary:  (Float, Float, Float) = (0.961, 0.937, 0.894)         // warm off-white

        // Status.
        static let error:   (Float, Float, Float) = (229/255, 79/255, 79/255)      // #E54F4F
        static let success: (Float, Float, Float) = (79/255, 184/255, 95/255)      // #4FB85F

        // Track type colors (clip fills).
        static let video:  (Float, Float, Float) = (29/255, 88/255, 120/255)       // #1D5878
        static let audio:  (Float, Float, Float) = (46/255, 119/255, 101/255)      // #2E7765
        static let text:   (Float, Float, Float) = (113/255, 84/255, 134/255)      // #715486
        static let image:  (Float, Float, Float) = (113/255, 84/255, 134/255)      // #715486

        // Text alpha levels.
        static let textPrimary:   Float = 1.0
        static let textSecondary: Float = 0.80
        static let textTertiary:  Float = 0.62
        static let textMuted:     Float = 0.34

        // Border alphas (applied to white).
        static let borderPrimary: Float = 0.16
        static let borderSubtle:  Float = 0.12
        static let borderDivider: Float = 0.44
    }

    // ImGuiCol enum values (vendored imgui.h 1.92.9).
    private enum Col {
        static let text: Int32 = 0
        static let textDisabled: Int32 = 1
        static let windowBg: Int32 = 2
        static let childBg: Int32 = 3
        static let popupBg: Int32 = 4
        static let border: Int32 = 5
        static let borderShadow: Int32 = 6
        static let frameBg: Int32 = 7
        static let frameBgHovered: Int32 = 8
        static let frameBgActive: Int32 = 9
        static let titleBg: Int32 = 10
        static let titleBgActive: Int32 = 11
        static let titleBgCollapsed: Int32 = 12
        static let menuBarBg: Int32 = 13
        static let scrollbarBg: Int32 = 14
        static let scrollbarGrab: Int32 = 15
        static let scrollbarGrabHovered: Int32 = 16
        static let scrollbarGrabActive: Int32 = 17
        static let checkMark: Int32 = 18
        static let sliderGrab: Int32 = 20
        static let sliderGrabActive: Int32 = 21
        static let button: Int32 = 22
        static let buttonHovered: Int32 = 23
        static let buttonActive: Int32 = 24
        static let header: Int32 = 25
        static let headerHovered: Int32 = 26
        static let headerActive: Int32 = 27
        static let separator: Int32 = 28
        static let separatorHovered: Int32 = 29
        static let separatorActive: Int32 = 30
        static let tabHovered: Int32 = 35
        static let tab: Int32 = 36
        static let tabSelected: Int32 = 37
        static let tabSelectedOverline: Int32 = 38
        static let tabDimmed: Int32 = 39
        static let tabDimmedSelected: Int32 = 40
        static let tabDimmedSelectedOverline: Int32 = 41
    }

    // ImGuiStyleVar indices (vendored imgui.h 1.92.9).
    private enum Var {
        static let windowPadding: Int32 = 2
        static let windowRounding: Int32 = 3
        static let childRounding: Int32 = 7
        static let popupRounding: Int32 = 9
        static let framePadding: Int32 = 11
        static let frameRounding: Int32 = 12
        static let itemSpacing: Int32 = 14
        static let itemInnerSpacing: Int32 = 15
        static let scrollbarRounding: Int32 = 19
        static let grabRounding: Int32 = 22
        static let tabRounding: Int32 = 25
    }

    // ImGuiWindowFlags (vendored imgui.h 1.92.9).
    private enum WinFlags {
        static let noTitleBar: Int32 = 1
        static let noResize: Int32 = 2
        static let noMove: Int32 = 4
        static let noScrollbar: Int32 = 8
        static let noScrollWithMouse: Int32 = 16
        static let noCollapse: Int32 = 32
        static let noSavedSettings: Int32 = 256
        static let noBringToFrontOnFocus: Int32 = 8192
        /// Chromeless, fixed panel.
        static let panel: Int32 = noTitleBar | noResize | noMove | noScrollbar | noScrollWithMouse | noCollapse | noSavedSettings
        /// Root editor window — panel flags plus "stay behind everything".
        static let root: Int32 = panel | noBringToFrontOnFocus
    }

    // Editor state.
    public var timeline: Timeline
    public var playheadFrame: Int = 0
    public var selectedClipID: String? = nil
    public var pixelsPerFrame: Float = 4.0
    public var videoTextureID: UnsafeMutableRawPointer? = nil  // VkDescriptorSet for cimgui_image
    public var videoAspect: Float = 16.0 / 9.0                 // source w/h, set by the caller
    /// True while the user is dragging the timeline scrub area — playback must
    /// not overwrite the playhead during a scrub.
    public private(set) var isScrubbing = false

    // Layout constants (from Constants.swift).
    private let sidebarWidth: Float = 280
    private let inspectorWidth: Float = 340
    private let agentWidth: Float = 280
    private let timelineHeight: Float = 240
    private let toolbarHeight: Float = 38
    private let trackHeaderWidth: Float = 100
    private let trackHeight: Float = 50
    private let rulerHeight: Float = 24
    private let panelGap: Float = 5

    private var font: UnsafeMutableRawPointer? = nil
    private var windowW: Float = 1280
    private var windowH: Float = 720

    public init(timeline: Timeline) {
        self.timeline = timeline
        applyPalmierStyle()
        loadFont()
    }

    // MARK: - Style

    private func applyPalmierStyle() {
        let s = Theme.surface, r = Theme.raised, p = Theme.prominent
        let tc = Theme.timecode

        // Backgrounds.
        cimgui_set_style_color(Col.windowBg, s.0, s.1, s.2, 1)
        cimgui_set_style_color(Col.childBg, s.0, s.1, s.2, 0)
        cimgui_set_style_color(Col.popupBg, r.0, r.1, r.2, 0.98)
        cimgui_set_style_color(Col.titleBg, r.0, r.1, r.2, 1)
        cimgui_set_style_color(Col.titleBgActive, r.0, r.1, r.2, 1)
        cimgui_set_style_color(Col.menuBarBg, s.0, s.1, s.2, 1)
        cimgui_set_style_color(Col.scrollbarBg, s.0, s.1, s.2, 0)
        cimgui_set_style_color(Col.scrollbarGrab, p.0, p.1, p.2, 0.8)
        cimgui_set_style_color(Col.scrollbarGrabHovered, p.0, p.1, p.2, 1)

        // Frames + inputs.
        cimgui_set_style_color(Col.frameBg, r.0, r.1, r.2, 1)
        cimgui_set_style_color(Col.frameBgHovered, p.0, p.1, p.2, 1)
        cimgui_set_style_color(Col.frameBgActive, p.0, p.1, p.2, 1)

        // Buttons — prominent surface.
        cimgui_set_style_color(Col.button, p.0, p.1, p.2, 1)
        cimgui_set_style_color(Col.buttonHovered, tc.0, tc.1, tc.2, 0.3)
        cimgui_set_style_color(Col.buttonActive, tc.0, tc.1, tc.2, 0.5)

        // Headers (list items, selectables).
        cimgui_set_style_color(Col.header, tc.0, tc.1, tc.2, 0.15)
        cimgui_set_style_color(Col.headerHovered, tc.0, tc.1, tc.2, 0.1)
        cimgui_set_style_color(Col.headerActive, tc.0, tc.1, tc.2, 0.2)

        // Tabs.
        cimgui_set_style_color(Col.tab, 0, 0, 0, 0)
        cimgui_set_style_color(Col.tabHovered, tc.0, tc.1, tc.2, 0.15)
        cimgui_set_style_color(Col.tabSelected, s.0, s.1, s.2, 1)
        cimgui_set_style_color(Col.tabSelectedOverline, tc.0, tc.1, tc.2, 1)

        // Accent elements.
        cimgui_set_style_color(Col.checkMark, tc.0, tc.1, tc.2, 1)
        cimgui_set_style_color(Col.sliderGrab, tc.0, tc.1, tc.2, 0.8)
        cimgui_set_style_color(Col.sliderGrabActive, tc.0, tc.1, tc.2, 1)

        // Text — white with alpha levels.
        cimgui_set_style_color(Col.text, 1, 1, 1, Theme.textPrimary)
        cimgui_set_style_color(Col.textDisabled, 1, 1, 1, Theme.textMuted)

        // Borders — white@16%.
        cimgui_set_style_color(Col.border, 1, 1, 1, Theme.borderPrimary)
        cimgui_set_style_color(Col.separator, 1, 1, 1, Theme.borderPrimary)

        // Rounding (matches AppTheme: WindowRounding=sm=6, FrameRounding=xsSm=4, TabRounding=sm=6).
        cimgui_set_style_rounding(Var.windowRounding, 6)
        cimgui_set_style_rounding(Var.childRounding, 6)
        cimgui_set_style_rounding(Var.frameRounding, 4)
        cimgui_set_style_rounding(Var.popupRounding, 6)
        cimgui_set_style_rounding(Var.grabRounding, 4)
        cimgui_set_style_rounding(Var.tabRounding, 6)

        // Padding/spacing (AppTheme Spacing: sm=6, smMd=8).
        cimgui_set_style_padding(Var.windowPadding, 6, 6)
        cimgui_set_style_padding(Var.framePadding, 6, 4)
        cimgui_set_style_padding(Var.itemSpacing, 6, 6)
        cimgui_set_style_padding(Var.itemInnerSpacing, 8, 4)
    }

    private func loadFont() {
        // Segoe UI is the closest Windows system font to SF Pro.
        if let f = cimgui_add_font_from_file(#"C:\Windows\Fonts\segoeui.ttf"#, 13) {
            font = f
        }
    }

    // MARK: - Frame

    /// Builds the full editor UI for one frame. Call between ui.newFrame() and
    /// ui.render(cmd). The editor fills the whole window with positioned panels.
    public func buildFrame() {
        // Root window covers the whole swapchain; panel geometry derives from
        // io.DisplaySize, not from any window's rect.
        var ww: Float = 0, wh: Float = 0
        cimgui_get_display_size(&ww, &wh)
        if ww > 0 { windowW = ww }
        if wh > 0 { windowH = wh }

        if let font { cimgui_push_font(font, 13) }

        // Fullscreen background — base #0A0A0A fills gaps between panels.
        cimgui_push_style_color(Col.windowBg, Theme.base.0, Theme.base.1, Theme.base.2, 1)
        cimgui_set_next_window_pos(0, 0)
        cimgui_set_next_window_size(windowW, windowH)
        var open: Int32 = 1
        "##Editor".withCString { cimgui_begin_flags($0, &open, WinFlags.root) }

        // Layout: the editor area excludes the timeline at the bottom.
        let editorH = windowH - timelineHeight - panelGap
        let timelineY = editorH + panelGap

        // Left sidebar (media library).
        drawMediaPanel(0, 0, sidebarWidth, editorH)

        // Center (video preview).
        let previewX = sidebarWidth + panelGap
        let previewW = windowW - sidebarWidth - inspectorWidth - agentWidth - panelGap * 3
        drawPreviewPanel(previewX, 0, previewW, editorH)

        // Right (inspector).
        let inspectorX = sidebarWidth + previewW + panelGap * 2
        drawInspectorPanel(inspectorX, 0, inspectorWidth, editorH)

        // Far right (AI agent).
        let agentX = inspectorX + inspectorWidth + panelGap
        drawAgentPanel(agentX, 0, agentWidth, editorH)

        // Bottom (timeline).
        drawTimelinePanel(0, timelineY, windowW, timelineHeight)

        cimgui_end()
        cimgui_pop_style_color(1)
        if font != nil { cimgui_pop_font() }
    }

    // MARK: - Panels

    private func drawMediaPanel(_ x: Float, _ y: Float, _ w: Float, _ h: Float) {
        cimgui_set_next_window_pos(x, y)
        cimgui_set_next_window_size(w, h)
        var open: Int32 = 1
        "Media##media".withCString { cimgui_begin_flags($0, &open, WinFlags.panel) }

        if cimgui_begin_tab_bar("##media_tabs") != 0 {
            if cimgui_begin_tab_item("Media") != 0 {
                "Import media to get started.".withCString { cimgui_text($0) }
                cimgui_spacing()
                if cimgui_button("Import", 120, 30) != 0 {}
                cimgui_end_tab_item()
            }
            if cimgui_begin_tab_item("Captions") != 0 {
                "No captions yet.".withCString { cimgui_text($0) }
                cimgui_end_tab_item()
            }
            if cimgui_begin_tab_item("Audio") != 0 {
                "No audio tracks.".withCString { cimgui_text($0) }
                cimgui_end_tab_item()
            }
            cimgui_end_tab_bar()
        }
        cimgui_end()
    }

    private func drawPreviewPanel(_ x: Float, _ y: Float, _ w: Float, _ h: Float) {
        cimgui_set_next_window_pos(x, y)
        cimgui_set_next_window_size(w, h)
        var open: Int32 = 1
        "Preview##preview".withCString { cimgui_begin_flags($0, &open, WinFlags.panel) }

        // Black preview canvas (AppTheme: preview = pure black).
        var cx: Float = 0, cy: Float = 0
        cimgui_get_cursor_screen_pos(&cx, &cy)
        var availW: Float = 0, availH: Float = 0
        cimgui_get_content_region_avail(&availW, &availH)
        cimgui_dl_add_rect_filled(cx, cy, cx + availW, cy + availH, cimgui_pack_color(0, 0, 0, 1), 0)

        // Video frame (if loaded) — centered, aspect-fit inside the canvas.
        if let tex = videoTextureID {
            let vidW = min(availW, availH * videoAspect)
            let vidH = vidW / videoAspect
            let offX = (availW - vidW) * 0.5
            let offY = (availH - vidH) * 0.5
            cimgui_dl_add_image(tex, cx + offX, cy + offY, cx + offX + vidW, cy + offY + vidH)
        } else {
            cimgui_spacing()
            "No video loaded".withCString { cimgui_text_colored(Theme.textMuted, Theme.textMuted, Theme.textMuted, $0) }
        }
        cimgui_dummy(availW, availH)

        // Timecode overlay (amber, bottom-left of preview).
        let tc = String(format: "%02d:%02d:%02d", playheadFrame / (30 * 60), (playheadFrame / 30) % 60, playheadFrame % 30)
        cimgui_dl_add_text(cx + 10, cy + availH - 24, cimgui_pack_color(Theme.timecode.0, Theme.timecode.1, Theme.timecode.2, 0.9), tc)

        cimgui_end()
    }

    private func drawInspectorPanel(_ x: Float, _ y: Float, _ w: Float, _ h: Float) {
        cimgui_set_next_window_pos(x, y)
        cimgui_set_next_window_size(w, h)
        var open: Int32 = 1
        "Inspector##inspector".withCString { cimgui_begin_flags($0, &open, WinFlags.panel) }

        if let clipID = selectedClipID,
           let clip = timeline.tracks.flatMap({ $0.clips }).first(where: { $0.id == clipID }) {
            // Show the selected clip's properties.
            "Transform".withCString { cimgui_text($0) }
            cimgui_separator()
            var cx = Float(clip.transform.centerX)
            "Center X".withCString { _ = cimgui_slider_float($0, &cx, 0, 1) }
            var cy = Float(clip.transform.centerY)
            "Center Y".withCString { _ = cimgui_slider_float($0, &cy, 0, 1) }
            var cw = Float(clip.transform.width)
            "Width".withCString { _ = cimgui_slider_float($0, &cw, 0.1, 2) }
            var ch = Float(clip.transform.height)
            "Height".withCString { _ = cimgui_slider_float($0, &ch, 0.1, 2) }
            var rot = Float(clip.transform.rotation)
            "Rotation".withCString { _ = cimgui_slider_float($0, &rot, -180, 180) }
            cimgui_separator()

            // Effects section.
            "Effects".withCString { cimgui_text($0) }
            if let effects = clip.effects {
                for eff in effects where eff.enabled {
                    var enabled: Int32 = eff.enabled ? 1 : 0
                    "\(eff.type)".withCString { _ = cimgui_checkbox($0, &enabled) }
                }
            } else {
                "No effects applied.".withCString { cimgui_text_colored(Theme.textMuted, Theme.textMuted, Theme.textMuted, $0) }
            }
        } else {
            "Select a clip to inspect.".withCString { cimgui_text_colored(Theme.textMuted, Theme.textMuted, Theme.textMuted, $0) }
        }

        cimgui_end()
    }

    private func drawAgentPanel(_ x: Float, _ y: Float, _ w: Float, _ h: Float) {
        cimgui_set_next_window_pos(x, y)
        cimgui_set_next_window_size(w, h)
        var open: Int32 = 1
        "Agent##agent".withCString { cimgui_begin_flags($0, &open, WinFlags.panel) }

        cimgui_spacing()
        "How can I help you edit?".withCString { cimgui_text($0) }
        cimgui_spacing()

        // Starter prompt capsule buttons (on raised surface).
        cimgui_begin_child("##prompts", 0, 200)
        let prompts = ["Trim the silence", "Add subtitles", "Color grade", "Add background music", "Create a highlight reel"]
        for prompt in prompts {
            if cimgui_button(prompt, Float(Int(w) - 20), 32) != 0 {
                "Running: \(prompt)...".withCString { cimgui_text($0) }
            }
            cimgui_spacing()
        }
        cimgui_end_child()

        cimgui_spacing()
        cimgui_separator()
        cimgui_spacing()

        // Input box placeholder.
        "Type a message...".withCString { cimgui_text_colored(Theme.textMuted, Theme.textMuted, Theme.textMuted, $0) }

        cimgui_end()
    }

    // MARK: - Timeline (custom-drawn)

    private func drawTimelinePanel(_ x: Float, _ y: Float, _ w: Float, _ h: Float) {
        cimgui_set_next_window_pos(x, y)
        cimgui_set_next_window_size(w, h)
        var open: Int32 = 1
        "Timeline##timeline".withCString { cimgui_begin_flags($0, &open, WinFlags.panel) }

        // Toolbar bar (height=38, surface background, border-primary bottom hairline).
        var tbx: Float = 0, tby: Float = 0
        cimgui_get_cursor_screen_pos(&tbx, &tby)
        cimgui_dl_add_rect_filled(tbx, tby, tbx + w, tby + toolbarHeight,
                                 cimgui_pack_color(Theme.raised.0, Theme.raised.1, Theme.raised.2, 1), 0)
        cimgui_dl_add_line(tbx, tby + toolbarHeight, tbx + w, tby + toolbarHeight,
                          cimgui_pack_color(1, 1, 1, Theme.borderPrimary), 1)
        cimgui_dummy(w, toolbarHeight)

        cimgui_new_line()

        // Track area.
        let trackAreaY = tby + toolbarHeight + rulerHeight
        let trackAreaH = h - toolbarHeight - rulerHeight - 10
        let trackScrollW = w - trackHeaderWidth

        // Ruler.
        drawRuler(tbx + trackHeaderWidth, tby + toolbarHeight, trackScrollW, rulerHeight)

        // Draw each track.
        for (trackIndex, track) in timeline.tracks.enumerated() {
            let ty = trackAreaY + Float(trackIndex) * (trackHeight + 1)
            let bgColor = Theme.surface
            // Track background.
            cimgui_dl_add_rect_filled(tbx, ty, tbx + w, ty + trackHeight,
                                     cimgui_pack_color(bgColor.0, bgColor.1, bgColor.2, 1), 0)

            // Track header.
            let trackLabel: String
            switch track.type {
            case .video: trackLabel = "Video \(trackIndex + 1)"
            case .audio: trackLabel = "Audio \(trackIndex + 1)"
            default: trackLabel = "Track \(trackIndex + 1)"
            }
            cimgui_dl_add_text(tbx + 8, ty + 8, cimgui_pack_color(1, 1, 1, Theme.textSecondary), trackLabel)

            // Clips.
            for clip in track.clips {
                let clipX = tbx + trackHeaderWidth + Float(clip.startFrame) * pixelsPerFrame
                let clipW = max(8, Float(clip.durationFrames) * pixelsPerFrame)
                let clipColor = clipTypeColor(clip)
                let isSelected = clip.id == selectedClipID

                // Clip block: rounded rect filled with track-type color.
                cimgui_dl_add_rect_filled(clipX, ty + 4, clipX + clipW, ty + trackHeight - 4,
                                         cimgui_pack_color(clipColor.0, clipColor.1, clipColor.2, 1), 4)

                // Selected: white stroke.
                if isSelected {
                    cimgui_dl_add_rect(clipX, ty + 4, clipX + clipW, ty + trackHeight - 4,
                                      cimgui_pack_color(1, 1, 1, 1), 4, 1.5)
                }

                // Clip label (only if wide enough).
                if clipW > 30 {
                    let label = clip.id
                    cimgui_dl_add_text(clipX + 6, ty + 8,
                                      cimgui_pack_color(1, 1, 1, 0.9), label)
                }
            }

            // Track separator.
            cimgui_dl_add_line(tbx, ty + trackHeight, tbx + w, ty + trackHeight,
                              cimgui_pack_color(1, 1, 1, Theme.borderPrimary), 1)
        }

        // Playhead: red vertical line + triangle.
        let phX = tbx + trackHeaderWidth + Float(playheadFrame) * pixelsPerFrame
        cimgui_dl_add_line(phX, tby + toolbarHeight, phX, tby + h,
                          cimgui_pack_color(1, 0.231, 0.188, 1), 1)
        // Triangle at top.
        cimgui_dl_add_rect_filled(phX - 4, tby + toolbarHeight, phX + 4, tby + toolbarHeight + 8,
                                 cimgui_pack_color(1, 0.231, 0.188, 1), 0)

        // Scrubbing: invisible button over the ruler + track area. Mouse X maps
        // directly to a frame; click also hit-tests clips for selection.
        cimgui_set_cursor_screen_pos(tbx + trackHeaderWidth, tby + toolbarHeight)
        _ = cimgui_invisible_button("##scrub", trackScrollW, rulerHeight + trackAreaH)
        isScrubbing = cimgui_is_item_active() != 0
        if isScrubbing {
            var mx: Float = 0, my: Float = 0
            cimgui_get_mouse_pos(&mx, &my)
            let relX = max(0, min(trackScrollW, mx - (tbx + trackHeaderWidth)))
            playheadFrame = max(0, min(timeline.totalFrames, Int(relX / pixelsPerFrame)))

            // Clip hit-test on the initial click (mouse Y picks the track).
            if cimgui_is_mouse_clicked(0) != 0 {
                let trackIndex = Int((my - trackAreaY) / (trackHeight + 1))
                if trackIndex >= 0, trackIndex < timeline.tracks.count {
                    let frameAtClick = Int(relX / pixelsPerFrame)
                    selectedClipID = timeline.tracks[trackIndex].clips.first(where: {
                        frameAtClick >= $0.startFrame && frameAtClick < $0.startFrame + $0.durationFrames
                    })?.id
                }
            }
        }

        cimgui_end()
    }

    private func drawRuler(_ x: Float, _ y: Float, _ w: Float, _ h: Float) {
        cimgui_dl_add_rect_filled(x, y, x + w, y + h,
                                 cimgui_pack_color(Theme.raised.0, Theme.raised.1, Theme.raised.2, 1), 0)
        // Tick marks every 30 frames (1 second at 30fps).
        let totalFrames = Float(timeline.totalFrames)
        let seconds = Int(totalFrames / 30) + 1
        for s in 0...seconds {
            let tickX = x + Float(s * 30) * pixelsPerFrame
            if tickX > x + w { break }
            cimgui_dl_add_line(tickX, y + h - 8, tickX, y + h,
                              cimgui_pack_color(1, 1, 1, Theme.borderDivider), 1)
            if s % 5 == 0 {
                let label = String(format: "%d:%02d", s / 60, s % 60)
                cimgui_dl_add_text(tickX + 2, y + 4,
                                  cimgui_pack_color(Theme.timecode.0, Theme.timecode.1, Theme.timecode.2, 0.8), label)
            }
        }
    }

    private func clipTypeColor(_ clip: Clip) -> (Float, Float, Float) {
        switch clip.mediaType {
        case .video: return Theme.video
        case .audio: return Theme.audio
        case .text: return Theme.text
        case .image: return Theme.image
        default: return Theme.video
        }
    }
}
