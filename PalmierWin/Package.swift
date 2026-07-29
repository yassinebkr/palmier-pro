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

let package = Package(
    name: "PalmierWin",
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
        .target(name: "PalmierCore", path: "Sources/PalmierCore"),
        .target(
            name: "PalmierWin",
            dependencies: ["CMediaFoundation", "CVulkan", "CFFmpeg", "CSTBTrueType", "PalmierCore"],
            path: "Sources/PalmierWin",
            swiftSettings: [
                .unsafeFlags(["-I", vkInc, "-I", ffInc]),
            ]
        ),
        .executableTarget(
            name: "palmierwin-spike",
            dependencies: ["PalmierCore", "PalmierWin", "CVulkan"],
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
