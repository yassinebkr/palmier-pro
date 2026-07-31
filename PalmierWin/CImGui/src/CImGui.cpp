// Flat-C wrapper around Dear ImGui + Vulkan/Win32 backends.
// Compiled as C++ — wraps ImGui's C++ API in extern "C" functions for Swift.
#include "CImGui.h"

#include "imgui.h"
#include "imgui_impl_win32.h"
#include "imgui_impl_vulkan.h"

// Win32 message handler forward-declaration (imgui_impl_win32 needs it).
extern "C" IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

struct ImGuiCtx {
    VkDescriptorPool descriptorPool = VK_NULL_HANDLE;
    bool ownsDescriptorPool = false;
};

ImGuiCtx* cimgui_init(void* instance, void* hwnd,
                      void* physical_device, void* device,
                      uint32_t queue_family, void* queue,
                      uint32_t image_count, uint32_t min_image_count,
                      uint32_t color_format,
                      void* render_pass,
                      void* descriptor_pool) {
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGui::StyleColorsDark();

    // Win32 platform backend.
    if (!ImGui_ImplWin32_Init(hwnd)) { return nullptr; }

    // Vulkan renderer backend.
    ImGui_ImplVulkan_InitInfo info = {};
    info.ApiVersion = VK_API_VERSION_1_3;
    info.Instance = (VkInstance)instance;
    info.PhysicalDevice = (VkPhysicalDevice)physical_device;
    info.Device = (VkDevice)device;
    info.QueueFamily = queue_family;
    info.Queue = (VkQueue)queue;
    info.ImageCount = image_count;
    info.MinImageCount = min_image_count;
    info.MinAllocationSize = 1024 * 1024;
    info.DescriptorPool = (VkDescriptorPool)descriptor_pool;
    info.PipelineInfoMain.RenderPass = (VkRenderPass)render_pass;
    // Color format: the PipelineInfoMain struct carries it for pipeline creation.
    // (The old ImGui_ImplVulkan_Init(info, color_format) two-arg form is gone;
    // set it on the attachment description if using dynamic rendering.)
    (void)color_format;

    if (!ImGui_ImplVulkan_Init(&info)) {
        ImGui_ImplWin32_Shutdown();
        ImGui::DestroyContext();
        return nullptr;
    }

    auto ctx = new ImGuiCtx();
    ctx->descriptorPool = (VkDescriptorPool)descriptor_pool;
    return ctx;
}

void cimgui_shutdown(ImGuiCtx* ctx) {
    if (!ctx) return;
    ImGui_ImplVulkan_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();
    delete ctx;
}

void cimgui_new_frame(ImGuiCtx* ctx) {
    ImGui_ImplWin32_NewFrame();
    ImGui_ImplVulkan_NewFrame();
    ImGui::NewFrame();
}

void cimgui_render(ImGuiCtx* ctx, void* command_buffer) {
    ImGui::Render();
    ImGui_ImplVulkan_RenderDrawData(ImGui::GetDrawData(), (VkCommandBuffer)command_buffer);
}

// --- Widgets ---

void cimgui_begin(const char* name, int* p_open) {
    ImGui::Begin(name, (bool*)p_open);
}
void cimgui_begin_flags(const char* name, int* p_open, int flags) {
    ImGui::Begin(name, (bool*)p_open, (ImGuiWindowFlags)flags);
}
void cimgui_end(void) { ImGui::End(); }
void cimgui_text(const char* fmt) { ImGui::Text("%s", fmt); }
void cimgui_text_colored(float r, float g, float b, const char* fmt) {
    ImGui::TextColored(ImVec4(r, g, b, 1.0f), "%s", fmt);
}
void cimgui_separator(void) { ImGui::Separator(); }
void cimgui_spacing(void) { ImGui::Spacing(); }
int cimgui_button(const char* label, float w, float h) { return ImGui::Button(label, ImVec2(w, h)); }
int cimgui_button_small(const char* label) { return ImGui::SmallButton(label); }
int cimgui_slider_float(const char* label, float* v, float v_min, float v_max) {
    return ImGui::SliderFloat(label, v, v_min, v_max);
}
int cimgui_slider_int(const char* label, int* v, int v_min, int v_max) {
    return ImGui::SliderInt(label, v, v_min, v_max);
}
int cimgui_checkbox(const char* label, int* v) { return ImGui::Checkbox(label, (bool*)v); }
int cimgui_input_text(const char* label, char* buf, int buf_size) {
    return ImGui::InputText(label, buf, (size_t)buf_size);
}
void cimgui_image(void* texture_id, float w, float h) {
    ImGui::Image((ImTextureID)texture_id, ImVec2(w, h));
}

// Register a Vulkan texture with ImGui's backend. Returns a descriptor set
// allocated against ImGui's own pipeline layout — the ONLY texture id form
// safe to pass to cimgui_image / cimgui_dl_add_image. A VkDescriptorSet made
// for another pipeline's layout will crash the driver at submit.
// image_layout: VkImageLayout (VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL = 5).
void* cimgui_add_texture(void* sampler, void* image_view, int image_layout) {
    return (void*)ImGui_ImplVulkan_AddTexture((VkSampler)sampler, (VkImageView)image_view,
                                              (VkImageLayout)image_layout);
}
void cimgui_begin_child(const char* name, float w, float h) {
    ImGui::BeginChild(name, ImVec2(w, h));
}
void cimgui_end_child(void) { ImGui::EndChild(); }
void cimgui_set_next_window_pos(float x, float y) {
    ImGui::SetNextWindowPos(ImVec2(x, y));
}
void cimgui_set_next_window_size(float w, float h) {
    ImGui::SetNextWindowSize(ImVec2(w, h));
}

// --- Style API ---
void cimgui_set_style_color(int col, float r, float g, float b, float a) {
    if (col >= 0 && col < ImGuiCol_COUNT)
        ImGui::GetStyle().Colors[col] = ImVec4(r, g, b, a);
}
void cimgui_set_style_rounding(int var, float v) {
    switch (var) {
        case ImGuiStyleVar_WindowRounding:    ImGui::GetStyle().WindowRounding = v; break;
        case ImGuiStyleVar_ChildRounding:     ImGui::GetStyle().ChildRounding = v; break;
        case ImGuiStyleVar_FrameRounding:     ImGui::GetStyle().FrameRounding = v; break;
        case ImGuiStyleVar_PopupRounding:     ImGui::GetStyle().PopupRounding = v; break;
        case ImGuiStyleVar_GrabRounding:      ImGui::GetStyle().GrabRounding = v; break;
        case ImGuiStyleVar_ScrollbarRounding: ImGui::GetStyle().ScrollbarRounding = v; break;
        case ImGuiStyleVar_TabRounding:       ImGui::GetStyle().TabRounding = v; break;
    }
}
void cimgui_set_style_padding(int var, float x, float y) {
    switch (var) {
        case ImGuiStyleVar_WindowPadding:    ImGui::GetStyle().WindowPadding = ImVec2(x, y); break;
        case ImGuiStyleVar_FramePadding:     ImGui::GetStyle().FramePadding = ImVec2(x, y); break;
        case ImGuiStyleVar_ItemSpacing:      ImGui::GetStyle().ItemSpacing = ImVec2(x, y); break;
        case ImGuiStyleVar_ItemInnerSpacing: ImGui::GetStyle().ItemInnerSpacing = ImVec2(x, y); break;
    }
}
void cimgui_push_style_color(int col, float r, float g, float b, float a) {
    ImGui::PushStyleColor(col, ImVec4(r, g, b, a));
}
void cimgui_pop_style_color(int count) { ImGui::PopStyleColor(count); }
void cimgui_push_style_var(int var, float v) { ImGui::PushStyleVar(var, v); }
void cimgui_push_style_var2(int var, float x, float y) { ImGui::PushStyleVar(var, ImVec2(x, y)); }
void cimgui_pop_style_var(int count) { ImGui::PopStyleVar(count); }

// --- Draw list API ---
void cimgui_dl_add_rect_filled(float x0, float y0, float x1, float y1, uint32_t col, float rounding) {
    ImGui::GetWindowDrawList()->AddRectFilled(ImVec2(x0, y0), ImVec2(x1, y1), col, rounding);
}
void cimgui_dl_add_rect(float x0, float y0, float x1, float y1, uint32_t col, float rounding, float thickness) {
    ImGui::GetWindowDrawList()->AddRect(ImVec2(x0, y0), ImVec2(x1, y1), col, rounding, 0, thickness);
}
void cimgui_dl_add_line(float x0, float y0, float x1, float y1, uint32_t col, float thickness) {
    ImGui::GetWindowDrawList()->AddLine(ImVec2(x0, y0), ImVec2(x1, y1), col, thickness);
}
void cimgui_dl_add_text(float x, float y, uint32_t col, const char* text) {
    ImGui::GetWindowDrawList()->AddText(ImVec2(x, y), col, text);
}
void cimgui_dl_add_image(void* tex, float x0, float y0, float x1, float y1) {
    ImGui::GetWindowDrawList()->AddImage((ImTextureID)tex, ImVec2(x0, y0), ImVec2(x1, y1));
}

// --- Layout API ---
void cimgui_same_line(float offset) { ImGui::SameLine(offset); }
void cimgui_dummy(float w, float h) { ImGui::Dummy(ImVec2(w, h)); }
void cimgui_new_line(void) { ImGui::NewLine(); }

// --- Tab bars ---
int cimgui_begin_tab_bar(const char* id) { return ImGui::BeginTabBar(id, ImGuiTabBarFlags_None) ? 1 : 0; }
void cimgui_end_tab_bar(void) { ImGui::EndTabBar(); }
int cimgui_begin_tab_item(const char* label) { return ImGui::BeginTabItem(label) ? 1 : 0; }
void cimgui_end_tab_item(void) { ImGui::EndTabItem(); }

// --- Input / interaction ---
int cimgui_invisible_button(const char* id, float w, float h) {
    return ImGui::InvisibleButton(id, ImVec2(w, h)) ? 1 : 0;
}
int cimgui_is_item_hovered(void) { return ImGui::IsItemHovered() ? 1 : 0; }
int cimgui_is_item_clicked(int button) { return ImGui::IsItemClicked(button) ? 1 : 0; }
int cimgui_is_item_active(void) { return ImGui::IsItemActive() ? 1 : 0; }
int cimgui_is_mouse_clicked(int button) { return ImGui::IsMouseClicked(button) ? 1 : 0; }
int cimgui_is_mouse_double_clicked(int button) { return ImGui::IsMouseDoubleClicked(button) ? 1 : 0; }
int cimgui_is_mouse_dragging(int button) { return ImGui::IsMouseDragging(button) ? 1 : 0; }
void cimgui_get_mouse_drag_delta(int button, float* x, float* y) {
    ImVec2 d = ImGui::GetMouseDragDelta(button);
    if (x) *x = d.x; if (y) *y = d.y;
}
int cimgui_selectable(const char* label, int selected) {
    return ImGui::Selectable(label, selected != 0) ? 1 : 0;
}

// --- Cursor / size helpers ---
void cimgui_get_cursor_screen_pos(float* x, float* y) {
    ImVec2 p = ImGui::GetCursorScreenPos();
    if (x) *x = p.x; if (y) *y = p.y;
}
void cimgui_set_cursor_screen_pos(float x, float y) {
    ImGui::SetCursorScreenPos(ImVec2(x, y));
}
void cimgui_get_content_region_avail(float* w, float* h) {
    ImVec2 s = ImGui::GetContentRegionAvail();
    if (w) *w = s.x; if (h) *h = s.y;
}
void cimgui_get_window_size(float* w, float* h) {
    ImVec2 s = ImGui::GetWindowSize();
    if (w) *w = s.x; if (h) *h = s.y;
}
void cimgui_get_window_pos(float* x, float* y) {
    ImVec2 p = ImGui::GetWindowPos();
    if (x) *x = p.x; if (y) *y = p.y;
}
void cimgui_get_display_size(float* w, float* h) {
    ImVec2 s = ImGui::GetIO().DisplaySize;
    if (w) *w = s.x; if (h) *h = s.y;
}
void cimgui_get_mouse_pos(float* x, float* y) {
    ImVec2 p = ImGui::GetIO().MousePos;
    if (x) *x = p.x; if (y) *y = p.y;
}

// --- Fonts ---
void* cimgui_add_font_from_file(const char* path, float size_pixels) {
    return (void*)ImGui::GetIO().Fonts->AddFontFromFileTTF(path, size_pixels);
}
void cimgui_push_font(void* font, float size) {
    ImGui::PushFont((ImFont*)font, size);
}
void cimgui_pop_font(void) { ImGui::PopFont(); }

// --- Color helper ---
uint32_t cimgui_pack_color(float r, float g, float b, float a) {
    return IM_COL32((int)(r * 255), (int)(g * 255), (int)(b * 255), (int)(a * 255));
}
