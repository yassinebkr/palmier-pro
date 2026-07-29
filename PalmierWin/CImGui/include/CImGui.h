// Flat-C wrapper around Dear ImGui + Vulkan/Win32 backends for Swift binding.
// ImGui is C++; this wrapper exposes a minimal flat-C API so Swift can call
// it without C++ interop. Compiled as C++ (the .cpp TU wraps C++ calls).
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Opaque handles — cast to/from ImGui types in the .cpp.
typedef struct ImGuiCtx ImGuiCtx;

// Init: creates the ImGui context + Win32 platform + Vulkan backend.
// Pass the existing Vulkan device handles so ImGui renders into our swapchain.
// Returns NULL on failure.
ImGuiCtx* cimgui_init(void* instance, void* hwnd,
                      void* physical_device, void* device,
                      uint32_t queue_family, void* queue,
                      uint32_t image_count, uint32_t min_image_count,
                      uint32_t color_format,  // VkFormat_B8G8R8A8_UNORM = 44
                      void* render_pass,      // VkRenderPass (optional, NULL for dynamic)
                      void* descriptor_pool); // VkDescriptorPool

void cimgui_shutdown(ImGuiCtx* ctx);

// Per-frame: begin ImGui frame (call after Win32 message pump).
void cimgui_new_frame(ImGuiCtx* ctx);

// Record ImGui draw data into the given command buffer. Call between
// vkCmdBeginRenderPass and vkCmdEndRenderPass.
void cimgui_render(ImGuiCtx* ctx, void* command_buffer);

// --- Minimal widget API (enough for the MVP editor UI) ---

void cimgui_begin(const char* name, int* p_open);
void cimgui_end(void);

void cimgui_text(const char* fmt);
void cimgui_text_colored(float r, float g, float b, const char* fmt);
void cimgui_separator(void);
void cimgui_spacing(void);

int cimgui_button(const char* label, float w, float h);
int cimgui_button_small(const char* label);

int cimgui_slider_float(const char* label, float* v, float v_min, float v_max);
int cimgui_slider_int(const char* label, int* v, int v_min, int v_max);
int cimgui_checkbox(const char* label, int* v);
int cimgui_input_text(const char* label, char* buf, int buf_size);

void cimgui_image(void* texture_id, float w, float h);

// Begin a child region (for panels).
void cimgui_begin_child(const char* name, float w, float h);
void cimgui_end_child(void);

// Window flags helpers.
void cimgui_set_next_window_pos(float x, float y);
void cimgui_set_next_window_size(float w, float h);

#ifdef __cplusplus
}
#endif
