// SPDX-License-Identifier: GPL-2.0-or-later
// bbport: Intel XeSS Super Resolution (upscaler=xess) as an alternative to the FSR contexts of
// TemporalUpscaler. It consumes the same inputs: color (HDR scene color at Native AA, the
// tonemapped frame on scaled presets), depth, motion vectors in render pixels (previous -
// current, undilated, at render size) and the jitter offset.
//
// libxess.dll (Intel XeSS SDK, tools/fetch_xess_sdk.ps1) is loaded at run time from BB_XESS_DIR
// or next to bbport.exe; builds without the SDK headers have no XeSS. On Arc GPUs it runs the
// XMX model, elsewhere the DP4a one. Its device extensions and features must be enabled when
// the Vulkan device is created (Xess::AddDeviceRequirements).

#pragma once

#include <array>
#include <memory>
#include <vector>
#include "common/types.h"
#include "video_core/renderer_vulkan/vk_common.h"

namespace Vulkan {

class Instance;
class Scheduler;

namespace Xess {
/// libxess.dll is present (loaded on the first call).
bool Load();
/// Before vkCreateDevice: the device extensions XeSS needs (all of them must be enabled).
/// False when XeSS cannot run on this device.
bool DeviceExtensions(vk::Instance instance, vk::PhysicalDevice physical_device,
                      std::vector<const char*>& extensions);
/// Before vkCreateDevice: merges XeSS's required features into the chain that starts at
/// `features2` (a VkPhysicalDeviceFeatures2 linked from VkDeviceCreateInfo::pNext).
bool ChainDeviceFeatures(vk::Instance instance, vk::PhysicalDevice physical_device,
                         void* features2);
/// The library version, "unavailable" without it.
const char* VersionString();
} // namespace Xess

class XessUpscaler {
public:
    XessUpscaler(const Instance& instance, Scheduler& scheduler);
    ~XessUpscaler();

    struct Image {
        vk::Image image;
        vk::ImageView view;
        vk::Format format;
        u32 width, height; ///< image size (the render area may be smaller)
    };
    struct Frame {
        vk::CommandBuffer cmdbuf;
        /// In layout General; read as ShaderReadOnlyOptimal during the pass, General after.
        Image color, depth, motion;
        /// In layout General, written by the pass.
        Image output;
        u32 render_width, render_height;
        int preset; ///< BbSettings::Preset
        std::array<float, 2> jitter;
        bool hdr, reset;
    };

    /// Records XeSS into `frame.cmdbuf`. False when it cannot run (Problem() says why).
    bool Record(const Frame& frame);

    [[nodiscard]] const char* Problem() const noexcept;
    /// The library, device support or context creation failed: no XeSS in this session.
    [[nodiscard]] bool Fatal() const noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};

} // namespace Vulkan
