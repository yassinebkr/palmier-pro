// swift-tools-version: 6.0
import PackageDescription
import Foundation

// Windows-only package (see README). PalmierCore is a path target
// (gitignored — symlinked in CI, copied locally). C* targets are filtered C
// wrappers or umbrella includes of flat-C headers; see
// docs/windows-media-engine-design.md for why flat-C (FFmpeg/Vulkan) and not
// COM (MF/D3D).
//
// Third-party native deps (Vulkan-Headers, vulkan-1.lib, BtbN FFmpeg) are
// fetched by fetch-deps.ps1 into ThirdParty/ (gitignored). The include/link
// paths below point there; run fetch-deps.ps1 before building. Paths are made
// absolute because lld-link resolves -L/-libpath relative to its own CWD, not
// the package root.
let root = FileManager.default.currentDirectoryPath
let vkInc = (root as NSString).appendingPathComponent("ThirdParty/Vulkan-Headers/include")
let vkLib = (root as NSString).appendingPathComponent("ThirdParty")
let ffInc = (root as NSString).appendingPathComponent("ThirdParty/ffmpeg/include")
let ffLib = (root as NSString).appendingPathComponent("ThirdParty/ffmpeg/lib")
let imguiInc = (root as NSString).appendingPathComponent("CImGui/deps/imgui")
let imguiBackInc = (root as NSString).appendingPathComponent("CImGui/deps/imgui/backends")

let package = Package(
    name: "PalmierWin",
    products: [
        // C ABI host for the .NET shell: exports @_cdecl functions over
        // PalmierCore + the Windows media engine (see Sources/PalmierCoreHost).
        .library(name: "PalmierCoreHost", type: .dynamic, targets: ["PalmierCoreHost"]),
    ],
    targets: [
        .systemLibrary(name: "CMediaFoundation", path: "CMediaFoundation"),
        .systemLibrary(name: "CVulkan", path: "CVulkan"),
        .systemLibrary(name: "CFFmpeg", path: "CFFmpeg"),
        // stb_truetype as a regular C target: the .c TU defines the
        // STB_TRUETYPE_IMPLEMENTATION (single-header library pattern), the
        // header declares the API for Swift.
        .target(name: "CSTBTrueType", path: "CSTBTrueType",
                publicHeadersPath: ".", cSettings: [
                    .headerSearchPath("."),
                ]),
        // Dear ImGui flat-C wrapper. C++ target — compiles the wrapper + ImGui
        // core + Vulkan/Win32 backends. Exposes extern "C" functions for Swift.
        .target(name: "CImGui", path: "CImGui",
                exclude: ["deps/imgui/.github", "deps/imgui/docs", "deps/imgui/examples",
                          "deps/imgui/backends/imgui_impl_opengl3*",
                          "deps/imgui/backends/imgui_impl_glfw*",
                          "deps/imgui/backends/imgui_impl_sdl*"],
                sources: ["src/CImGui.cpp",
                          "deps/imgui/imgui.cpp",
                          "deps/imgui/imgui_draw.cpp",
                          "deps/imgui/imgui_tables.cpp",
                          "deps/imgui/imgui_widgets.cpp",
                          "deps/imgui/imgui_demo.cpp",
                          "deps/imgui/backends/imgui_impl_vulkan.cpp",
                          "deps/imgui/backends/imgui_impl_win32.cpp"],
                publicHeadersPath: "include",
                cSettings: [
                    .headerSearchPath("include"),
                    .headerSearchPath("deps/imgui"),
                    .headerSearchPath("deps/imgui/backends"),
                    .headerSearchPath("../ThirdParty/Vulkan-Headers/include"),
                    .unsafeFlags(["-DVK_USE_PLATFORM_WIN32_KHR=1"]),
                ]),
        .target(name: "PalmierCore", path: "Sources/PalmierCore"),
        .target(
            name: "PalmierCoreHost",
            dependencies: ["PalmierCore", "PalmierWin"],
            path: "Sources/PalmierCoreHost",
            swiftSettings: [
                .unsafeFlags(["-I", vkInc, "-I", ffInc]),
            ],
            // The dynamic library is a final linked product — it needs the
            // same import libs as the spike exe.
            linkerSettings: [
                .unsafeFlags([
                    (vkLib as NSString).appendingPathComponent("vulkan-1.lib"),
                    "-lavformat", "-lavcodec", "-lavutil", "-lswscale",
                    "-L", ffLib,
                ]),
            ]
        ),
        .target(
            name: "PalmierWin",
            dependencies: ["CMediaFoundation", "CVulkan", "CFFmpeg", "CSTBTrueType", "CImGui", "PalmierCore"],
            path: "Sources/PalmierWin",
            swiftSettings: [
                .unsafeFlags(["-I", vkInc, "-I", ffInc]),
            ]
        ),
        .executableTarget(
            name: "palmierwin-spike",
            dependencies: ["PalmierCore", "PalmierWin", "CVulkan", "CImGui"],
            path: "Sources/palmierwin-spike",
            // linkerSettings belong on the final linked product (the exe), not
            // the library — SwiftPM only applies them when linking this target.
            linkerSettings: [
                // Pass the import libs as explicit inputs (absolute paths) rather
                // than -l + -L: lld-link's -l search has been flaky for these.
                .unsafeFlags([
                    (vkLib as NSString).appendingPathComponent("vulkan-1.lib"),
                    "-lavformat", "-lavcodec", "-lavutil", "-lswscale",
                    "-L", ffLib,
                ]),
            ]
        ),
    ]
)
