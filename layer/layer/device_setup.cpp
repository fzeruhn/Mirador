// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/device_setup.hpp"

#include <algorithm>
#include <cstring>

namespace dvl {
namespace {

bool listed(const std::vector<const char*>& names, const char* name)
{
    return std::any_of(names.begin(), names.end(), [&](const char* n) { return std::strcmp(n, name) == 0; });
}

bool available(const std::vector<std::string>& names, const char* name)
{
    return std::find(names.begin(), names.end(), name) != names.end();
}

bool family_listed(const std::vector<VkDeviceQueueCreateInfo>& queues, uint32_t family)
{
    return std::any_of(queues.begin(), queues.end(),
                       [&](const VkDeviceQueueCreateInfo& q) { return q.queueFamilyIndex == family; });
}

// Enlarges the first create info by one queue, if it may be (design 6.2: flags 0, and within the family's count).
bool enlarge_first(DevicePlan& plan, const std::vector<VkQueueFamilyProperties>& families)
{
    if (plan.queues.empty()) {
        return false;
    }
    VkDeviceQueueCreateInfo& first = plan.queues[0];
    if (first.flags != 0 || first.queueFamilyIndex >= families.size() || first.pQueuePriorities == nullptr ||
        first.queueCount >= families[first.queueFamilyIndex].queueCount) {
        return false;
    }
    std::vector<float> priorities(first.pQueuePriorities, first.pQueuePriorities + first.queueCount);
    const float highest = priorities.empty() ? 1.0f : *std::max_element(priorities.begin(), priorities.end());
    priorities.push_back(highest);
    plan.priorities.push_back(std::move(priorities));
    plan.private_family = first.queueFamilyIndex;
    plan.private_index = first.queueCount;
    first.queueCount += 1;
    first.pQueuePriorities = plan.priorities.back().data();
    return true;
}

// Adds a create info of its own for a family the application did not list (a family may appear only once).
bool add_info(DevicePlan& plan, const std::vector<VkQueueFamilyProperties>& families)
{
    constexpr VkQueueFlags kUsable = VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT | VK_QUEUE_TRANSFER_BIT;
    for (uint32_t family = 0; family < families.size(); family++) {
        if (family_listed(plan.queues, family) || families[family].queueCount == 0 ||
            (families[family].queueFlags & kUsable) == 0) {
            continue;
        }
        plan.priorities.push_back({1.0f});
        VkDeviceQueueCreateInfo info{};
        info.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
        info.queueFamilyIndex = family;
        info.queueCount = 1;
        info.pQueuePriorities = plan.priorities.back().data();
        plan.queues.push_back(info);
        plan.private_family = family;
        plan.private_index = 0;
        plan.new_info = true;
        return true;
    }
    return false;
}

} // namespace

std::optional<DevicePlan> plan_device(const VkDeviceCreateInfo& ci, const std::vector<VkQueueFamilyProperties>& families,
                                      const std::vector<std::string>& available_ext, uint32_t vendor_id)
{
    if (vendor_id != kNvidiaVendorId) {
        return std::nullopt;
    }
    DevicePlan plan;
    if (ci.queueCreateInfoCount > 0 && ci.pQueueCreateInfos != nullptr) {
        plan.queues.assign(ci.pQueueCreateInfos, ci.pQueueCreateInfos + ci.queueCreateInfoCount);
    }
    if (!enlarge_first(plan, families)) {
        add_info(plan, families);
    }

    for (uint32_t i = 0; i < ci.enabledExtensionCount; i++) {
        const char* name = ci.ppEnabledExtensionNames[i];
        if (std::strcmp(name, VK_EXT_DISPLAY_CONTROL_EXTENSION_NAME) == 0) {
            plan.display_control = true; // the layer's own: the driver lacks it
            continue;
        }
        plan.extensions.push_back(name);
    }
    for (const char* name : kImportExtensions) {
        if (listed(plan.extensions, name)) {
            continue;
        }
        if (available(available_ext, name)) {
            plan.extensions.push_back(name);
        } else {
            plan.missing_extensions.push_back(name);
        }
    }
    return plan;
}

} // namespace dvl
