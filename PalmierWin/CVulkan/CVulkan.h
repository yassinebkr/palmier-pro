// Vulkan flat-C surface for Swift binding. The Khronos headers parse cleanly
// under Swift's Clang importer (unlike the COM-heavy MF/D3D SDK headers) —
// verified: CVulkan compiles and vkCreateInstance returns VK_SUCCESS.
#pragma once
#define VK_USE_PLATFORM_WIN32_KHR 1
#include <vulkan/vulkan.h>
