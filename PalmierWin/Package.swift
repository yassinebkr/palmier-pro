// swift-tools-version: 6.0
import PackageDescription

// Windows-only package (see README in this dir). PalmierCore is a path target
// into a local copy (CI symlinks it). C* targets are filtered C wrappers —
// see CMediaFoundation/CMediaFoundation.h for why umbrella SDK includes fail.

let package = Package(
    name: "PalmierWin",
    targets: [
        .systemLibrary(name: "CMediaFoundation", path: "CMediaFoundation"),
        .target(name: "PalmierCore", path: "Sources/PalmierCore"),
        .target(
            name: "PalmierWin",
            dependencies: ["CMediaFoundation", "PalmierCore"],
            path: "Sources/PalmierWin"
        ),
        .executableTarget(
            name: "palmierwin-spike",
            dependencies: ["PalmierCore", "PalmierWin"],
            path: "Sources/palmierwin-spike"
        ),
    ]
)
