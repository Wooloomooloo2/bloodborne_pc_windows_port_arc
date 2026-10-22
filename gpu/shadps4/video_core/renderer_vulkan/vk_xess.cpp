// SPDX-License-Identifier: GPL-2.0-or-later
#include "video_core/renderer_vulkan/vk_xess.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <string>

#include "video_core/renderer_vulkan/vk_instance.h"
#include "video_core/renderer_vulkan/vk_scheduler.h"

#ifdef BBPORT_XESS
#include <windows.h>
#include <xess/xess_vk.h>
#endif

namespace Vulkan {

#ifdef BBPORT_XESS

namespace {

/// libxess.dll entry points: the SDK's import library is MSVC-only, and a missing DLL must
/// leave the other upscalers working.
struct Api {
    HMODULE module = nullptr;
    decltype(&xessGetVersion) GetVersion = nullptr;
    decltype(&xessVKGetRequiredInstanceExtensions) GetRequiredInstanceExtensions = nullptr;
    decltype(&xessVKGetRequiredDeviceExtensions) GetRequiredDeviceExtensions = nullptr;
    decltype(&xessVKGetRequiredDeviceFeatures) GetRequiredDeviceFeatures = nullptr;
    decltype(&xessVKCreateContext) CreateContext = nullptr;
    decltype(&xessVKBuildPipelines) BuildPipelines = nullptr;
    decltype(&xessVKInit) Init = nullptr;
    decltype(&xessVKExecute) Execute = nullptr;
    decltype(&xessDestroyContext) DestroyContext = nullptr;
    decltype(&xessGetProperties) GetProperties = nullptr;
    decltype(&xessSetLoggingCallback) SetLoggingCallback = nullptr;
    decltype(&xessIsOptimalDriver) IsOptimalDriver = nullptr;
};
Api api;
bool loaded = false;
char version[32] = "unavailable";

std::wstring ModuleDir(HMODULE module) {
    wchar_t path[MAX_PATH * 2];
    const DWORD n = GetModuleFileNameW(module, path, DWORD(std::size(path)));
    if (n == 0 || n >= std::size(path)) return {};
    std::wstring dir(path, n);
    const auto slash = dir.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring{} : dir.substr(0, slash);
}

std::wstring Widen(const char* text) {
    const int n = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring wide(size_t(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text, -1, wide.data(), n);
    return wide;
}

template <typename T>
bool Resolve(T& fn, const char* name) {
    fn = reinterpret_cast<T>(reinterpret_cast<void*>(GetProcAddress(api.module, name)));
    if (!fn) std::printf("Upscaler: XeSS: libxess.dll has no %s\n", name);
    return fn != nullptr;
}

bool LoadApi() {
    std::vector<std::wstring> dirs;
    if (const char* env = std::getenv("BB_XESS_DIR"); env && env[0]) dirs.push_back(Widen(env));
    dirs.push_back(ModuleDir(nullptr)); // bbport.exe
    HMODULE self = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&LoadApi), &self)) {
        dirs.push_back(ModuleDir(self)); // bbgpu.dll
    }
    bool present = false;
    for (const auto& dir : dirs) {
        if (dir.empty()) continue;
        const std::wstring path = dir + L"\\libxess.dll";
        if (GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) continue;
        present = true;
        api.module = LoadLibraryExW(path.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (api.module) break;
    }
    if (!api.module) {
        if (present) {
            std::printf("Upscaler: XeSS: libxess.dll failed to load (error %lu); it needs the "
                        "Microsoft Visual C++ 2015-2022 x64 runtime\n", GetLastError());
        } else {
            std::printf("Upscaler: XeSS unavailable (no libxess.dll; tools/fetch_xess_sdk.ps1)\n");
        }
        return false;
    }
    const bool ok = Resolve(api.GetVersion, "xessGetVersion") &&
        Resolve(api.GetRequiredInstanceExtensions, "xessVKGetRequiredInstanceExtensions") &&
        Resolve(api.GetRequiredDeviceExtensions, "xessVKGetRequiredDeviceExtensions") &&
        Resolve(api.GetRequiredDeviceFeatures, "xessVKGetRequiredDeviceFeatures") &&
        Resolve(api.CreateContext, "xessVKCreateContext") &&
        Resolve(api.BuildPipelines, "xessVKBuildPipelines") && Resolve(api.Init, "xessVKInit") &&
        Resolve(api.Execute, "xessVKExecute") &&
        Resolve(api.DestroyContext, "xessDestroyContext") &&
        Resolve(api.GetProperties, "xessGetProperties") &&
        Resolve(api.SetLoggingCallback, "xessSetLoggingCallback") &&
        Resolve(api.IsOptimalDriver, "xessIsOptimalDriver");
    if (!ok) {
        FreeLibrary(api.module);
        api = {};
        return false;
    }
    xess_version_t v{};
    if (api.GetVersion(&v) == XESS_RESULT_SUCCESS) {
        std::snprintf(version, sizeof(version), "%u.%u.%u", v.major, v.minor, v.patch);
    }
    std::printf("Upscaler: XeSS %s loaded\n", version);
    return true;
}

const char* ResultName(xess_result_t result) {
    switch (result) {
    case XESS_RESULT_WARNING_NONEXISTING_FOLDER: return "warning: nonexisting folder";
    case XESS_RESULT_WARNING_OLD_DRIVER: return "warning: old driver";
    case XESS_RESULT_SUCCESS: return "success";
    case XESS_RESULT_ERROR_UNSUPPORTED_DEVICE: return "unsupported device";
    case XESS_RESULT_ERROR_UNSUPPORTED_DRIVER: return "unsupported driver";
    case XESS_RESULT_ERROR_UNINITIALIZED: return "uninitialized";
    case XESS_RESULT_ERROR_INVALID_ARGUMENT: return "invalid argument";
    case XESS_RESULT_ERROR_DEVICE_OUT_OF_MEMORY: return "device out of memory";
    case XESS_RESULT_ERROR_DEVICE: return "device error";
    case XESS_RESULT_ERROR_NOT_IMPLEMENTED: return "not implemented";
    case XESS_RESULT_ERROR_INVALID_CONTEXT: return "invalid context";
    case XESS_RESULT_ERROR_OPERATION_IN_PROGRESS: return "operation in progress";
    case XESS_RESULT_ERROR_UNSUPPORTED: return "unsupported";
    case XESS_RESULT_ERROR_CANT_LOAD_LIBRARY: return "cannot load library";
    case XESS_RESULT_ERROR_WRONG_CALL_ORDER: return "wrong call order";
    default: return "unknown error";
    }
}

void LogCallback(const char* message, xess_logging_level_t level) {
    std::printf("Upscaler: XeSS %s: %s\n", level >= XESS_LOGGING_LEVEL_ERROR ? "error" : "warning",
                message);
}

struct ChainNode {
    VkStructureType sType;
    ChainNode* pNext;
};

/// XeSS may add the extension feature structs that Vulkan 1.2/1.3 promoted; the device chain
/// already has VkPhysicalDeviceVulkan12/13Features, and both forms together are invalid.
void FoldPromotedFeatures(void* features2) {
    auto* head = static_cast<ChainNode*>(features2);
    VkPhysicalDeviceVulkan12Features* vk12 = nullptr;
    VkPhysicalDeviceVulkan13Features* vk13 = nullptr;
    for (auto* node = head; node; node = node->pNext) {
        if (node->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES)
            vk12 = reinterpret_cast<VkPhysicalDeviceVulkan12Features*>(node);
        if (node->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES)
            vk13 = reinterpret_cast<VkPhysicalDeviceVulkan13Features*>(node);
    }
    for (auto* prev = head; prev && prev->pNext;) {
        auto* node = prev->pNext;
        bool fold = false;
        if (vk12 && node->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_FLOAT16_INT8_FEATURES) {
            const auto* f = reinterpret_cast<VkPhysicalDeviceShaderFloat16Int8Features*>(node);
            vk12->shaderFloat16 |= f->shaderFloat16;
            vk12->shaderInt8 |= f->shaderInt8;
            fold = true;
        } else if (vk12 && node->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_8BIT_STORAGE_FEATURES) {
            const auto* f = reinterpret_cast<VkPhysicalDevice8BitStorageFeatures*>(node);
            vk12->storageBuffer8BitAccess |= f->storageBuffer8BitAccess;
            vk12->uniformAndStorageBuffer8BitAccess |= f->uniformAndStorageBuffer8BitAccess;
            vk12->storagePushConstant8 |= f->storagePushConstant8;
            fold = true;
        } else if (vk13 &&
                   node->sType ==
                       VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_INTEGER_DOT_PRODUCT_FEATURES) {
            const auto* f = reinterpret_cast<VkPhysicalDeviceShaderIntegerDotProductFeatures*>(node);
            vk13->shaderIntegerDotProduct |= f->shaderIntegerDotProduct;
            fold = true;
        } else if (vk13 &&
                   node->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_SIZE_CONTROL_FEATURES) {
            const auto* f = reinterpret_cast<VkPhysicalDeviceSubgroupSizeControlFeatures*>(node);
            vk13->subgroupSizeControl |= f->subgroupSizeControl;
            vk13->computeFullSubgroups |= f->computeFullSubgroups;
            fold = true;
        }
        if (fold) {
            prev->pNext = node->pNext; // XeSS owns the node
        } else {
            prev = node;
        }
    }
}

/// Bloodborne's presets (1.0, 1.5, 1.7, 2.0, 3.0) on XeSS 1.3+ quality settings: the setting
/// whose dynamic range covers this frame's ratio (even-rounded sizes can land just past one).
xess_quality_settings_t QualityFor(u32 w, u32 h, u32 ow, u32 oh) {
    const double ratio = std::max(double(ow) / w, double(oh) / h);
    if (ratio <= 1.001) return XESS_QUALITY_SETTING_AA;
    static constexpr std::pair<double, xess_quality_settings_t> table[] = {
        {1.5, XESS_QUALITY_SETTING_ULTRA_QUALITY}, {1.7, XESS_QUALITY_SETTING_QUALITY},
        {2.0, XESS_QUALITY_SETTING_BALANCED},      {2.3, XESS_QUALITY_SETTING_PERFORMANCE},
        {3.0, XESS_QUALITY_SETTING_ULTRA_PERFORMANCE}};
    for (const auto& [max, quality] : table) {
        if (ratio <= max + 1e-3) return quality;
    }
    return XESS_QUALITY_SETTING_ULTRA_PERFORMANCE;
}

const char* QualityName(xess_quality_settings_t quality) {
    switch (quality) {
    case XESS_QUALITY_SETTING_AA: return "Native AA";
    case XESS_QUALITY_SETTING_ULTRA_QUALITY_PLUS: return "Ultra Quality Plus";
    case XESS_QUALITY_SETTING_ULTRA_QUALITY: return "Ultra Quality";
    case XESS_QUALITY_SETTING_QUALITY: return "Quality";
    case XESS_QUALITY_SETTING_BALANCED: return "Balanced";
    case XESS_QUALITY_SETTING_PERFORMANCE: return "Performance";
    default: return "Ultra Performance";
    }
}

bool HasStencil(vk::Format format) {
    return format == vk::Format::eD32SfloatS8Uint || format == vk::Format::eD24UnormS8Uint ||
           format == vk::Format::eD16UnormS8Uint;
}

xess_vk_image_view_info Describe(const XessUpscaler::Image& image, VkImageAspectFlags aspect) {
    return {
        .imageView = VkImageView(image.view),
        .image = VkImage(image.image),
        .subresourceRange = {aspect, 0, 1, 0, 1},
        .format = VkFormat(image.format),
        .width = image.width,
        .height = image.height,
    };
}

} // namespace

namespace Xess {

bool Load() {
    static std::once_flag once;
    std::call_once(once, [] { loaded = LoadApi(); });
    return loaded;
}

bool DeviceExtensions(vk::Instance instance, vk::PhysicalDevice physical_device,
                      std::vector<const char*>& extensions) {
    if (!Load()) return false;
    uint32_t count = 0;
    const char* const* names = nullptr;
    uint32_t api_version = 0;
    if (api.GetRequiredInstanceExtensions(&count, &names, &api_version) == XESS_RESULT_SUCCESS &&
        count) {
        // The instance is created before the renderer knows about XeSS; none are expected
        // (Vulkan 1.3 core covers what XeSS 3 asks for).
        for (uint32_t i = 0; i < count; ++i) {
            std::printf("Upscaler: XeSS asks for instance extension %s\n", names[i]);
        }
    }
    count = 0;
    names = nullptr;
    const auto result = api.GetRequiredDeviceExtensions(VkInstance(instance),
                                                        VkPhysicalDevice(physical_device),
                                                        &count, &names);
    if (result != XESS_RESULT_SUCCESS) {
        std::printf("Upscaler: XeSS not supported by this device (%s)\n", ResultName(result));
        return false;
    }
    extensions.assign(names, names + count);
    return true;
}

bool ChainDeviceFeatures(vk::Instance instance, vk::PhysicalDevice physical_device,
                         void* features2) {
    if (!Load()) return false;
    void* chain = features2;
    const auto result = api.GetRequiredDeviceFeatures(VkInstance(instance),
                                                      VkPhysicalDevice(physical_device), &chain);
    if (result != XESS_RESULT_SUCCESS) {
        std::printf("Upscaler: XeSS device features unavailable (%s)\n", ResultName(result));
        return false;
    }
    FoldPromotedFeatures(features2);
    return true;
}

const char* VersionString() {
    return version;
}

} // namespace Xess

struct XessUpscaler::Impl {
    const Instance& instance;
    Scheduler& scheduler;
    xess_context_handle_t context = nullptr;
    bool initialized = false;
    u32 out_width = 0, out_height = 0;
    xess_quality_settings_t quality{};
    u32 flags = 0;
    std::string problem;
    bool fatal = false;

    Impl(const Instance& instance_, Scheduler& scheduler_)
        : instance{instance_}, scheduler{scheduler_} {}

    ~Impl() {
        if (context) {
            scheduler.Finish();
            api.DestroyContext(context);
        }
    }

    void Fail(std::string reason, bool permanent) {
        if (problem != reason) {
            std::printf("Upscaler: XeSS unavailable: %s\n", reason.c_str());
        }
        problem = std::move(reason);
        fatal |= permanent;
    }

    bool EnsureContext() {
        if (context) return true;
        if (!Xess::Load()) {
            Fail("libxess.dll not found (run tools/fetch_xess_sdk.ps1)", true);
            return false;
        }
        if (!instance.IsXessSupported()) {
            Fail("the Vulkan device lacks XeSS's extensions or features", true);
            return false;
        }
        const auto result = api.CreateContext(VkInstance(instance.GetInstance()),
                                              VkPhysicalDevice(instance.GetPhysicalDevice()),
                                              VkDevice(instance.GetDevice()), &context);
        if (result != XESS_RESULT_SUCCESS) {
            context = nullptr;
            Fail(std::string("context creation failed (") + ResultName(result) + ")", true);
            return false;
        }
        api.SetLoggingCallback(context, XESS_LOGGING_LEVEL_WARNING, LogCallback);
        if (api.IsOptimalDriver(context) == XESS_RESULT_WARNING_OLD_DRIVER) {
            std::printf("Upscaler: XeSS: the GPU driver is older than XeSS recommends\n");
        }
        return true;
    }

    bool EnsureInit(u32 ow, u32 oh, xess_quality_settings_t q, u32 f) {
        if (initialized && ow == out_width && oh == out_height && q == quality && f == flags) {
            return true;
        }
        // Re-initialising with command buffers in flight is not allowed.
        scheduler.Finish();
        initialized = false;
        xess_vk_init_params_t params{};
        params.outputResolution = {ow, oh};
        params.qualitySetting = q;
        params.initFlags = f;
        const auto result = api.Init(context, &params);
        if (result != XESS_RESULT_SUCCESS) {
            Fail(std::string("initialisation failed (") + ResultName(result) + ")",
                 result == XESS_RESULT_ERROR_UNSUPPORTED_DEVICE ||
                     result == XESS_RESULT_ERROR_UNSUPPORTED_DRIVER);
            return false;
        }
        xess_properties_t props{};
        const xess_2d_t out{ow, oh};
        api.GetProperties(context, &out, &props);
        std::printf("Upscaler: XeSS %s, %s, %ux%u output, %s input, %.1f MB temporary\n",
                    Xess::VersionString(), QualityName(q), ow, oh,
                    (f & XESS_INIT_FLAG_LDR_INPUT_COLOR) ? "tonemapped" : "HDR",
                    (props.tempBufferHeapSize + props.tempTextureHeapSize) / 1e6);
        initialized = true;
        out_width = ow;
        out_height = oh;
        quality = q;
        flags = f;
        return true;
    }

    bool Record(const Frame& frame) {
        if (fatal || !EnsureContext()) return false;
        const auto q = QualityFor(frame.render_width, frame.render_height, frame.output.width,
                                  frame.output.height);
        const u32 f = frame.hdr ? u32(XESS_INIT_FLAG_ENABLE_AUTOEXPOSURE)
                                : u32(XESS_INIT_FLAG_LDR_INPUT_COLOR);
        if (!EnsureInit(frame.output.width, frame.output.height, q, f)) return false;

        const auto cmdbuf = frame.cmdbuf;
        const vk::ImageAspectFlags depth_aspect =
            HasStencil(frame.depth.format)
                ? vk::ImageAspectFlagBits::eDepth | vk::ImageAspectFlagBits::eStencil
                : vk::ImageAspectFlags{vk::ImageAspectFlagBits::eDepth};
        // XeSS samples its inputs in ShaderReadOnlyOptimal; callers keep them in General.
        const auto inputs = [&](vk::ImageLayout from, vk::ImageLayout to,
                                vk::PipelineStageFlags2 src, vk::AccessFlags2 src_access,
                                vk::PipelineStageFlags2 dst, vk::AccessFlags2 dst_access) {
            std::array<vk::ImageMemoryBarrier2, 3> barriers{};
            const std::array<std::pair<vk::Image, vk::ImageAspectFlags>, 3> images{{
                {frame.color.image, vk::ImageAspectFlagBits::eColor},
                {frame.depth.image, depth_aspect},
                {frame.motion.image, vk::ImageAspectFlagBits::eColor}}};
            for (size_t i = 0; i < barriers.size(); ++i) {
                barriers[i] = {.srcStageMask = src, .srcAccessMask = src_access,
                               .dstStageMask = dst, .dstAccessMask = dst_access,
                               .oldLayout = from, .newLayout = to, .image = images[i].first,
                               .subresourceRange = {images[i].second, 0, 1, 0, 1}};
            }
            cmdbuf.pipelineBarrier2({.imageMemoryBarrierCount = u32(barriers.size()),
                                     .pImageMemoryBarriers = barriers.data()});
        };
        constexpr auto all = vk::PipelineStageFlagBits2::eAllCommands;
        constexpr auto mem = vk::AccessFlagBits2::eMemoryRead | vk::AccessFlagBits2::eMemoryWrite;
        inputs(vk::ImageLayout::eGeneral, vk::ImageLayout::eShaderReadOnlyOptimal, all, mem,
               vk::PipelineStageFlagBits2::eComputeShader, vk::AccessFlagBits2::eShaderRead);

        xess_vk_execute_params_t params{};
        params.colorTexture = Describe(frame.color, VK_IMAGE_ASPECT_COLOR_BIT);
        params.velocityTexture = Describe(frame.motion, VK_IMAGE_ASPECT_COLOR_BIT);
        params.depthTexture = Describe(frame.depth, VK_IMAGE_ASPECT_DEPTH_BIT);
        params.outputTexture = Describe(frame.output, VK_IMAGE_ASPECT_COLOR_BIT);
        params.jitterOffsetX = frame.jitter[0];
        params.jitterOffsetY = frame.jitter[1];
        params.exposureScale = 1.0f;
        params.resetHistory = frame.reset ? 1u : 0u;
        params.inputWidth = frame.render_width;
        params.inputHeight = frame.render_height;
        const auto result = api.Execute(context, VkCommandBuffer(cmdbuf), &params);

        inputs(vk::ImageLayout::eShaderReadOnlyOptimal, vk::ImageLayout::eGeneral,
               vk::PipelineStageFlagBits2::eComputeShader, vk::AccessFlagBits2::eShaderRead, all,
               mem);
        if (result != XESS_RESULT_SUCCESS) {
            Fail(std::string("execute failed (") + ResultName(result) + ")",
                 result == XESS_RESULT_ERROR_UNSUPPORTED_DEVICE ||
                     result == XESS_RESULT_ERROR_UNSUPPORTED_DRIVER);
            return false;
        }
        problem.clear();
        return true;
    }
};

XessUpscaler::XessUpscaler(const Instance& instance, Scheduler& scheduler)
    : impl{std::make_unique<Impl>(instance, scheduler)} {}

XessUpscaler::~XessUpscaler() = default;

bool XessUpscaler::Record(const Frame& frame) {
    return impl->Record(frame);
}

const char* XessUpscaler::Problem() const noexcept {
    return impl->problem.empty() ? nullptr : impl->problem.c_str();
}

bool XessUpscaler::Fatal() const noexcept {
    return impl->fatal;
}

#else // !BBPORT_XESS

namespace Xess {
bool Load() {
    return false;
}
bool DeviceExtensions(vk::Instance, vk::PhysicalDevice, std::vector<const char*>&) {
    return false;
}
bool ChainDeviceFeatures(vk::Instance, vk::PhysicalDevice, void*) {
    return false;
}
const char* VersionString() {
    return "unavailable";
}
} // namespace Xess

struct XessUpscaler::Impl {};

XessUpscaler::XessUpscaler(const Instance&, Scheduler&) {}
XessUpscaler::~XessUpscaler() = default;
bool XessUpscaler::Record(const Frame&) {
    return false;
}
const char* XessUpscaler::Problem() const noexcept {
    return "this build has no XeSS (run tools/fetch_xess_sdk.ps1 and rebuild)";
}
bool XessUpscaler::Fatal() const noexcept {
    return true;
}

#endif

} // namespace Vulkan
