// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace dvl {

// What vkCreateDevice changes before the call down (design 6.2). Pure: the layer's vkCreateDevice (device.cpp) asks the
// layer below for the inputs and applies the plan.

inline constexpr uint32_t kNvidiaVendorId = 0x10de;

// The device extensions the back-buffer import needs (design 6.2). Monado asks for the first four itself, the fifth is
// optional for it; the layer adds whichever are missing.
inline constexpr const char* kImportExtensions[] = {
    VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME,
    VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME,
    VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME,
    VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME,
    VK_KHR_IMAGE_FORMAT_LIST_EXTENSION_NAME,
};

// The create info the layer passes down instead of the application's. Move-only: `queues` points into `priorities`.
struct DevicePlan {
    DevicePlan() = default;
    DevicePlan(DevicePlan&&) = default;
    DevicePlan& operator=(DevicePlan&&) = default;
    DevicePlan(const DevicePlan&) = delete;
    DevicePlan& operator=(const DevicePlan&) = delete;

    // The application's queue create infos (their pNext chains kept), one of them enlarged or one added for the
    // private queue. pQueuePriorities of a changed or added info points into `priorities`.
    std::vector<VkDeviceQueueCreateInfo> queues;
    std::vector<std::vector<float>> priorities;
    // The application's extensions without VK_EXT_display_control (the layer's own), plus the import extensions it
    // did not ask for. The names are the application's strings or kImportExtensions'.
    std::vector<const char*> extensions;
    // Where the private queue is: get it with vkGetDeviceQueue(private_family, private_index). private_family is
    // VK_QUEUE_FAMILY_IGNORED if no family had room (the device then has no private queue).
    uint32_t private_family = VK_QUEUE_FAMILY_IGNORED;
    uint32_t private_index = 0;
    bool new_info = false; // The private queue has a create info of its own (the last of `queues`).

    // For the log: import extensions the device does not offer (not added, so the call down does not fail; the
    // import will), and whether the application asked for VK_EXT_display_control.
    std::vector<const char*> missing_extensions;
    bool display_control = false;
};

// The plan for a device whose physical device has vendor `vendor_id`, queue families `families` and device extensions
// `available_ext` (as the layer below lists them). nullopt unless the vendor is NVIDIA: that device is passed down
// untouched. Otherwise (design 6.2):
//   - the private queue: pQueueCreateInfos[0] gets one more queue if its flags are 0 and its family has room, with the
//     highest priority the info already has and its pNext (Monado's global priority) kept; else a new info (flags 0,
//     one queue, priority 1.0) for the first family that is not listed, has queues and does graphics, compute or
//     transfer; else none;
//   - each of kImportExtensions that is not asked for and is available is added, once;
//   - VK_EXT_display_control is removed.
std::optional<DevicePlan> plan_device(const VkDeviceCreateInfo& ci, const std::vector<VkQueueFamilyProperties>& families,
                                      const std::vector<std::string>& available_ext, uint32_t vendor_id);

} // namespace dvl
