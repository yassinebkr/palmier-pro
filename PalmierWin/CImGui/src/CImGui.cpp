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
