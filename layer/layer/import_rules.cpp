// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/import_rules.hpp"

#include <algorithm>

namespace dvl {

std::optional<std::vector<VkFormat>> view_formats(VkFormat format, VkImageUsageFlags usage)
{
    const bool storage = (usage & VK_IMAGE_USAGE_STORAGE_BIT) != 0;
    if (format == VK_FORMAT_R8G8B8A8_UNORM) {
        if (storage) {
            return std::vector<VkFormat>{VK_FORMAT_R8G8B8A8_UNORM};
        }
        return std::vector<VkFormat>{VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB};
    }
    if (format == VK_FORMAT_R8G8B8A8_SRGB && !storage) {
        return std::vector<VkFormat>{VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB};
    }
    return std::nullopt;
}

std::string format_name(VkFormat format)
{
    switch (format) {
    case VK_FORMAT_R8G8B8A8_UNORM: return "R8G8B8A8_UNORM";
    case VK_FORMAT_R8G8B8A8_SRGB: return "R8G8B8A8_SRGB";
    default: return "format " + std::to_string(static_cast<int>(format));
    }
}

ImportSupport import_support(VkResult result, const VkImageFormatProperties& properties,
                             const VkExternalMemoryProperties& external, VkExtent2D extent)
{
    ImportSupport support;
    if (result != VK_SUCCESS) {
        support.reason = "the driver returned " + std::to_string(static_cast<int>(result));
        return support;
    }
    support.dedicated_only = (external.externalMemoryFeatures & VK_EXTERNAL_MEMORY_FEATURE_DEDICATED_ONLY_BIT) != 0;
    if ((external.externalMemoryFeatures & VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT) == 0) {
        support.reason = "the handle type is not importable (external memory features " +
                         std::to_string(external.externalMemoryFeatures) + ")";
        return support;
    }
    if (properties.maxExtent.width < extent.width || properties.maxExtent.height < extent.height) {
        support.reason = "the largest image is " + std::to_string(properties.maxExtent.width) + "x" +
                         std::to_string(properties.maxExtent.height) + ", the back-buffer " +
                         std::to_string(extent.width) + "x" + std::to_string(extent.height);
        return support;
    }
    support.ok = true;
    return support;
}

MemoryPick pick_memory_type(std::optional<uint32_t> handle_bits, uint32_t image_bits,
                            const VkPhysicalDeviceMemoryProperties& memory)
{
    MemoryPick pick;
    pick.fallback = !handle_bits || *handle_bits == 0;
    const uint32_t allowed = pick.fallback ? image_bits : (*handle_bits & image_bits);
    const uint32_t count = std::min<uint32_t>(memory.memoryTypeCount, VK_MAX_MEMORY_TYPES);
    for (uint32_t i = 0; i < count; i++) {
        if ((allowed & (1u << i)) == 0) {
            continue;
        }
        if ((memory.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT) != 0) {
            pick.index = i;
            return pick;
        }
        if (!pick.index) {
            pick.index = i;
        }
    }
    return pick;
}

} // namespace dvl
