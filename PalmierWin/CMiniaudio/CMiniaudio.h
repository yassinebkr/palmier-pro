// miniaudio flat-C surface for Swift binding: WASAPI output behind a pure C
// API (COM stays inside the C translation unit — the same wall-dodge as
// FFmpeg/Vulkan; see docs/windows-media-engine-design.md). Decode/encode and
// the high-level engine are compiled out: FFmpeg owns decode, PalmierCoreHost
// owns mixing.
#pragma once
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#include "miniaudio.h"
