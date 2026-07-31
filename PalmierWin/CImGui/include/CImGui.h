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
void cimgui_begin_flags(const char* name, int* p_open, int flags);  // ImGuiWindowFlags_
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

// Register a texture with ImGui's Vulkan backend; returns the texture id to
// use with cimgui_image / cimgui_dl_add_image. image_layout is a VkImageLayout
// value (VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL = 5).
void* cimgui_add_texture(void* sampler, void* image_view, int image_layout);

// Begin a child region (for panels).
void cimgui_begin_child(const char* name, float w, float h);
void cimgui_end_child(void);

// Window flags helpers.
void cimgui_set_next_window_pos(float x, float y);
void cimgui_set_next_window_size(float w, float h);

// --- Style API (port AppTheme colors + rounding into ImGui) ---
// All enum indices are the real ImGuiCol_ / ImGuiStyleVar_ values from the
// vendored imgui.h (1.92.9). The .cpp switches are keyed on the enum names.

void cimgui_set_style_color(int col, float r, float g, float b, float a);
void cimgui_set_style_rounding(int var, float v);  // ImGuiStyleVar_
void cimgui_set_style_padding(int var, float x, float y);

void cimgui_push_style_color(int col, float r, float g, float b, float a);
void cimgui_pop_style_color(int count);
void cimgui_push_style_var(int var, float v);
void cimgui_push_style_var2(int var, float x, float y);  // for ItemSpacing etc.
void cimgui_pop_style_var(int count);

// --- Draw list API (for custom timeline rendering) ---
// Operates on the current window's draw list. Color is packed ImU32 (0xAABBGGRR).
void cimgui_dl_add_rect_filled(float x0, float y0, float x1, float y1,
                               uint32_t col, float rounding);
void cimgui_dl_add_rect(float x0, float y0, float x1, float y1,
                        uint32_t col, float rounding, float thickness);
void cimgui_dl_add_line(float x0, float y0, float x1, float y1,
                        uint32_t col, float thickness);
void cimgui_dl_add_text(float x, float y, uint32_t col, const char* text);
void cimgui_dl_add_image(void* tex, float x0, float y0, float x1, float y1);

// --- Layout API ---
void cimgui_same_line(float offset);
void cimgui_dummy(float w, float h);
void cimgui_new_line(void);

// Tab bars (for inspector/media panel tabs).
int cimgui_begin_tab_bar(const char* id);
void cimgui_end_tab_bar(void);
int cimgui_begin_tab_item(const char* label);
void cimgui_end_tab_item(void);

// --- Input / interaction API ---
int cimgui_invisible_button(const char* id, float w, float h);
int cimgui_is_item_hovered(void);
int cimgui_is_item_clicked(int button);
int cimgui_is_item_active(void);
int cimgui_is_mouse_clicked(int button);
int cimgui_is_mouse_double_clicked(int button);
int cimgui_is_mouse_dragging(int button);
void cimgui_get_mouse_drag_delta(int button, float* x, float* y);
int cimgui_selectable(const char* label, int selected);

// --- Cursor / size helpers ---
void cimgui_get_cursor_screen_pos(float* x, float* y);
void cimgui_set_cursor_screen_pos(float x, float y);
void cimgui_get_content_region_avail(float* w, float* h);
void cimgui_get_window_size(float* w, float* h);
void cimgui_get_window_pos(float* x, float* y);
void cimgui_get_display_size(float* w, float* h);  // io.DisplaySize (swapchain size)
void cimgui_get_mouse_pos(float* x, float* y);     // io.MousePos

// --- Fonts ---
// Load a TTF file. Returns an opaque ImFont* (pass to push_font).
void* cimgui_add_font_from_file(const char* path, float size_pixels);
void cimgui_push_font(void* font, float size);
void cimgui_pop_font(void);

// --- Color helpers ---
// Pack RGBA floats into ImU32 (0xAABBGGRR format used by ImGui draw list).
uint32_t cimgui_pack_color(float r, float g, float b, float a);

#ifdef __cplusplus
}
#endif
