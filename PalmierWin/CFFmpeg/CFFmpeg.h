// FFmpeg flat-C surface for Swift binding (libavformat + libavcodec + libavutil
// + libswscale). Headers are pure C — verified to parse under Swift's Clang
// importer and to link/run (avformat_network_init OK).
#pragma once
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavutil/avutil.h>
#include <libswscale/swscale.h>
