// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The back-buffer import and the trial import (design 6.3, 6.4). See import.hpp.

#include "layer/import.hpp"

#include <cstdio>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "core/display_session.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"

namespace dvl {
namespace {

// "R8G8B8A8_UNORM usage 0x2f", for log lines.
std::string describe(const ImportConfig& config)
{
    char usage[32];
    std::snprintf(usage, sizeof(usage), " usage 0x%x", static_cast<unsigned>(config.usage));
    return format_name(config.format) + usage;
}

std::string joined(const std::vector<const char*>& names)
{
    std::string text;
    for (const char* name : names) {
        text += text.empty() ? "" : ", ";
        text += name;
    }
    return text;
}

// The back-buffers' extent, from the open display session; nullopt while it is not open.
std::optional<VkExtent2D> back_buffer_extent()
{
    nvdm::DisplaySession& session = display_session();
    if (!session.is_open()) {
        return std::nullopt;
    }
    return VkExtent2D{session.product().width, session.product().height};
}

// Design 6.4's first check: the format query with the back-buffers' handle type, for an image made as step 2 makes
// it. `result` is the driver's code (VK_ERROR_EXTENSION_NOT_PRESENT if the layer below lacks the command).
ImportSupport query_support(InstanceData& instance, VkPhysicalDevice physical_device, const ImportConfig& config,
                            const std::vector<VkFormat>& views, VkExtent2D extent, VkResult& result)
{
    if (instance.GetPhysicalDeviceImageFormatProperties2KHR == nullptr) {
        result = VK_ERROR_EXTENSION_NOT_PRESENT;
        ImportSupport none;
        none.reason = "vkGetPhysicalDeviceImageFormatProperties2KHR is missing (the instance needs "
                      "VK_KHR_get_physical_device_properties2 and VK_KHR_external_memory_capabilities)";
        return none;
    }
    VkImageFormatListCreateInfoKHR list{VK_STRUCTURE_TYPE_IMAGE_FORMAT_LIST_CREATE_INFO_KHR};
    list.viewFormatCount = static_cast<uint32_t>(views.size());
    list.pViewFormats = views.data();
    VkPhysicalDeviceExternalImageFormatInfoKHR external{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO_KHR};
    external.pNext = &list;
    external.handleType = kBackBufferHandleType;
    VkPhysicalDeviceImageFormatInfo2KHR info{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2_KHR};
    info.pNext = &external;
    info.format = config.format;
    info.type = VK_IMAGE_TYPE_2D;
    info.tiling = VK_IMAGE_TILING_OPTIMAL;
    info.usage = config.usage;
    info.flags = VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;

    VkExternalImageFormatPropertiesKHR external_properties{VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES_KHR};
    VkImageFormatProperties2KHR properties{VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2_KHR};
    properties.pNext = &external_properties;
    result = instance.GetPhysicalDeviceImageFormatProperties2KHR(physical_device, &info, &properties);
    return import_support(result, properties.imageFormatProperties, external_properties.externalMemoryProperties,
                          extent);
}

// The commands the import calls, all there.
bool has_import_commands(const DeviceData& device)
{
    return device.CreateImage != nullptr && device.DestroyImage != nullptr &&
           device.GetImageMemoryRequirements != nullptr && device.GetMemoryWin32HandlePropertiesKHR != nullptr &&
           device.AllocateMemory != nullptr && device.FreeMemory != nullptr && device.BindImageMemory != nullptr &&
           device.instance != nullptr && device.instance->GetPhysicalDeviceMemoryProperties != nullptr;
}

// The format queries of both trial configurations: the usage of those that allow the import, and whether all do.
struct FormatAnswers {
    VkImageUsageFlags usage = 0;
    bool all = true;
};

FormatAnswers format_answers(InstanceData& instance, VkPhysicalDevice physical_device, VkExtent2D extent)
{
    FormatAnswers answers;
    for (const ImportConfig& config : kTrialConfigs) {
        VkResult result = VK_SUCCESS;
        const ImportSupport support =
            query_support(instance, physical_device, config, *view_formats(config.format, config.usage), extent,
                          result);
        if (support.ok) {
            answers.usage |= config.usage;
        } else {
            answers.all = false;
            log(Level::Error, "format query for back-buffers as %s (D3D11 KMT, mutable format): %s",
                describe(config).c_str(), support.reason.c_str());
        }
    }
    return answers;
}

bool run_trial(DeviceData& device)
{
    const void* handle = static_cast<void*>(device.handle);
    if (!device.missing_import_extensions.empty()) {
        log(Level::Error, "trial import on device %p: the device was made without %s (the driver lacks them), which "
                          "the import needs",
            handle, joined(device.missing_import_extensions).c_str());
        return false;
    }
    nvdm::DisplaySession& session = display_session();
    if (!session.is_open()) {
        log(Level::Error, "trial import on device %p: the display session is not open", handle);
        return false;
    }
    const VkExtent2D extent{session.product().width, session.product().height};
    for (const ImportConfig& config : kTrialConfigs) {
        ImportedImage images[nvdm::kBackBufferCount];
        uint32_t imported = 0;
        VkResult result = VK_SUCCESS;
        while (imported < nvdm::kBackBufferCount && result == VK_SUCCESS) {
            result = import_back_buffer(device, session.back_buffer(imported).shared, extent, config, images[imported]);
            imported += result == VK_SUCCESS ? 1 : 0;
        }
        for (ImportedImage& image : images) {
            release_imported(device, image);
        }
        if (result != VK_SUCCESS) {
            log(Level::Warn, "trial import on device %p: back-buffer %u as %s failed (%d), so the layer's display is "
                             "not usable from this device",
                handle, imported, describe(config).c_str(), static_cast<int>(result));
            return false;
        }
    }
    log(Level::Info, "trial import on device %p: the %u back-buffers import as %s and as %s (imported and released)",
        handle, nvdm::kBackBufferCount, describe(kTrialConfigs[0]).c_str(), describe(kTrialConfigs[1]).c_str());
    return true;
}

} // namespace

VkResult import_back_buffer(DeviceData& device, HANDLE shared, VkExtent2D extent, const ImportConfig& config,
                            ImportedImage& out)
{
    out = ImportedImage{};
    const void* handle = static_cast<void*>(device.handle);
    const std::string what = describe(config);
    const std::optional<std::vector<VkFormat>> views = view_formats(config.format, config.usage);
    if (!views) {
        log(Level::Error, "import of back-buffer %p as %s on device %p: refused; only R8G8B8A8_UNORM and _SRGB, and "
                          "SRGB without storage, can be imported",
            shared, what.c_str(), handle);
        return VK_ERROR_FORMAT_NOT_SUPPORTED;
    }
    if (!has_import_commands(device)) {
        log(Level::Error, "import of back-buffer %p on device %p: the layer below lacks a command the import needs "
                          "(VK_KHR_external_memory_win32 enabled?)",
            shared, handle);
        return VK_ERROR_EXTENSION_NOT_PRESENT;
    }
    InstanceData& instance = *device.instance;

    // 1. The format query.
    VkResult result = VK_SUCCESS;
    const ImportSupport support = query_support(instance, device.physical_device, config, *views, extent, result);
    if (!support.ok) {
        log(Level::Error, "import of back-buffer %p as %s on device %p: the format query: %s", shared, what.c_str(),
            handle, support.reason.c_str());
        return result != VK_SUCCESS ? result : VK_ERROR_FORMAT_NOT_SUPPORTED;
    }

    // 2. The image.
    VkImageFormatListCreateInfoKHR list{VK_STRUCTURE_TYPE_IMAGE_FORMAT_LIST_CREATE_INFO_KHR};
    list.viewFormatCount = static_cast<uint32_t>(views->size());
    list.pViewFormats = views->data();
    VkExternalMemoryImageCreateInfoKHR external{VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO_KHR};
    external.pNext = &list;
    external.handleTypes = kBackBufferHandleType;
    VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ci.pNext = &external;
    ci.flags = VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT;
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = config.format;
    ci.extent = {extent.width, extent.height, 1};
    ci.mipLevels = 1;
    ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = config.usage;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    ImportedImage made;
    result = device.CreateImage(device.handle, &ci, nullptr, &made.image);
    if (result != VK_SUCCESS) {
        log(Level::Error, "import of back-buffer %p as %s on device %p: vkCreateImage returned %d", shared,
            what.c_str(), handle, static_cast<int>(result));
        return result;
    }

    // 3. The memory type: one both the handle and the image allow.
    VkMemoryRequirements requirements{};
    device.GetImageMemoryRequirements(device.handle, made.image, &requirements);
    VkMemoryWin32HandlePropertiesKHR handle_properties{VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR};
    const VkResult handle_result =
        device.GetMemoryWin32HandlePropertiesKHR(device.handle, kBackBufferHandleType, shared, &handle_properties);
    std::optional<uint32_t> handle_bits;
    if (handle_result == VK_SUCCESS) {
        handle_bits = handle_properties.memoryTypeBits;
    }
    VkPhysicalDeviceMemoryProperties memory_properties{};
    instance.GetPhysicalDeviceMemoryProperties(device.physical_device, &memory_properties);
    const MemoryPick pick = pick_memory_type(handle_bits, requirements.memoryTypeBits, memory_properties);
    if (pick.fallback && !device.memory_fallback_logged.exchange(true)) {
        // Design 8's memory-type fallback warning, once per device.
        log(Level::Warn,
            "import on device %p: vkGetMemoryWin32HandlePropertiesKHR %s for back-buffer %p, so the memory type comes "
            "from the image's memoryTypeBits (0x%x) alone, as Monado's target did",
            handle, handle_result != VK_SUCCESS ? "failed" : "gave no memory types", shared,
            static_cast<unsigned>(requirements.memoryTypeBits));
    }
    if (!pick.index) {
        log(Level::Error,
            "import of back-buffer %p as %s on device %p: no memory type fits (the handle allows 0x%x, the image "
            "0x%x)",
            shared, what.c_str(), handle, static_cast<unsigned>(handle_bits.value_or(0)),
            static_cast<unsigned>(requirements.memoryTypeBits));
        release_imported(device, made);
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }

    // 4. The dedicated import, and the bind.
    VkImportMemoryWin32HandleInfoKHR import{VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR};
    import.handleType = kBackBufferHandleType;
    import.handle = shared;
    import.name = nullptr;
    VkMemoryDedicatedAllocateInfoKHR dedicated{VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO_KHR};
    dedicated.pNext = &import;
    dedicated.image = made.image;
    dedicated.buffer = VK_NULL_HANDLE;
    VkMemoryAllocateInfo allocate{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    allocate.pNext = &dedicated;
    allocate.allocationSize = requirements.size; // ignored for D3D11 handles: the driver takes it from the handle
    allocate.memoryTypeIndex = *pick.index;
    result = device.AllocateMemory(device.handle, &allocate, nullptr, &made.memory);
    if (result != VK_SUCCESS) {
        made.memory = VK_NULL_HANDLE;
        log(Level::Error, "import of back-buffer %p as %s on device %p: vkAllocateMemory (memory type %u) returned %d",
            shared, what.c_str(), handle, *pick.index, static_cast<int>(result));
        release_imported(device, made);
        return result;
    }
    result = device.BindImageMemory(device.handle, made.image, made.memory, 0);
    if (result != VK_SUCCESS) {
        log(Level::Error, "import of back-buffer %p as %s on device %p: vkBindImageMemory returned %d", shared,
            what.c_str(), handle, static_cast<int>(result));
        release_imported(device, made);
        return result;
    }
    log(Level::Debug, "import of back-buffer %p as %s on device %p: image %p, memory type %u%s", shared, what.c_str(),
        handle, static_cast<void*>(made.image), *pick.index,
        support.dedicated_only ? " (the driver allows dedicated memory only)" : "");
    out = made;
    return VK_SUCCESS;
}

void release_imported(DeviceData& device, ImportedImage& image)
{
    if (image.image != VK_NULL_HANDLE && device.DestroyImage != nullptr) {
        device.DestroyImage(device.handle, image.image, nullptr);
    }
    if (image.memory != VK_NULL_HANDLE && device.FreeMemory != nullptr) {
        device.FreeMemory(device.handle, image.memory, nullptr);
    }
    image = ImportedImage{};
}

bool trial_import(DeviceData& device)
{
    std::lock_guard<std::mutex> lock(device.import_mutex);
    if (!device.trial_import_result) {
        device.trial_import_result = run_trial(device);
    }
    return *device.trial_import_result;
}

bool trial_import_at_swapchain(DeviceData& device)
{
    {
        std::lock_guard<std::mutex> lock(device.import_mutex);
        if (device.trial_import_result) {
            return *device.trial_import_result;
        }
    }
    log(Level::Info, "vkCreateSwapchainKHR: device %p did not exist when its GPU was asked for displays, so its trial "
                     "import runs now (design 6.3)",
        static_cast<void*>(device.handle));
    return trial_import(device);
}

VkImageUsageFlags supported_usage(InstanceData& instance, VkPhysicalDevice physical_device, VkExtent2D extent)
{
    return format_answers(instance, physical_device, extent).usage;
}

VkImageUsageFlags supported_usage(DeviceData& device)
{
    const std::optional<VkExtent2D> extent = back_buffer_extent();
    if (!extent || device.instance == nullptr) {
        return 0;
    }
    return supported_usage(*device.instance, device.physical_device, *extent);
}

ImportVerdict import_verdict(InstanceData& instance, VkPhysicalDevice physical_device)
{
    ImportVerdict verdict;
    const std::optional<VkExtent2D> extent = back_buffer_extent();
    if (!extent) {
        log(Level::Error, "the import cannot be checked: the display session is not open");
        return verdict;
    }
    const FormatAnswers answers = format_answers(instance, physical_device, *extent);
    size_t failed = 0;
    const size_t devices = for_each_device(&instance, physical_device, [&](DeviceData& device) {
        failed += trial_import(device) ? 0 : 1;
    });
    if (devices == 0 && answers.all) {
        log(Level::Info, "no device exists on this GPU yet: the import is tried at each device's first "
                         "vkCreateSwapchainKHR instead (design 6.3)");
    }
    if (failed > 0) {
        log(Level::Warn, "%zu of %zu devices on this GPU failed the trial import, so the layer's display is not listed",
            failed, devices);
    }
    if (!answers.all) {
        log(Level::Warn, "the format queries allow the import for usage 0x%x only, not both configurations, so the "
                         "layer's display is not listed",
            static_cast<unsigned>(answers.usage));
    }
    verdict.listed = answers.all && failed == 0;
    verdict.usage = verdict.listed ? answers.usage : 0;
    return verdict;
}

} // namespace dvl
