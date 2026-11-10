// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <vulkan/vulkan.h>

#include "layer/dispatch.hpp"
#include "layer/import_rules.hpp"

namespace dvl {

// The back-buffer import (design 6.3, 6.4): the calls on the layer below. What they decide without a call is in
// import_rules.hpp (dvl_pure); this half calls the driver and the display session (dvl_layer).

// A back-buffer imported on a device: the image, and the imported memory bound to it.
struct ImportedImage {
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
};

// Imports one back-buffer (`shared`, a KMT handle of the display session) as an image of `extent` in `config`, with
// design 6.4's checks in order:
//  1. vkGetPhysicalDeviceImageFormatProperties2KHR with the image's format, MUTABLE_FORMAT, usage and view formats, and
//     VkPhysicalDeviceExternalImageFormatInfoKHR (D3D11_TEXTURE_KMT): IMPORTABLE is required (import_support());
//     DEDICATED_ONLY is honoured by step 4, which is always dedicated;
//  2. vkCreateImage: the config's format and usage, MUTABLE_FORMAT, the view-format list (view_formats()), and
//     VkExternalMemoryImageCreateInfoKHR (D3D11_TEXTURE_KMT);
//  3. vkGetImageMemoryRequirements and vkGetMemoryWin32HandlePropertiesKHR on the handle, for the memory type
//     (pick_memory_type()); if the handle gives no types, the image's memoryTypeBits, logged once per device;
//  4. vkAllocateMemory, dedicated to the image (VkMemoryDedicatedAllocateInfoKHR) and importing the handle
//     (VkImportMemoryWin32HandleInfoKHR, name NULL; allocationSize is ignored for D3D11 handles), then
//     vkBindImageMemory at offset 0.
// On success `out` holds the image and its memory; release_imported() frees them. On failure nothing is left behind,
// `out` is empty, and one line names the step and the code: VK_ERROR_FORMAT_NOT_SUPPORTED for a config view_formats()
// refuses (SRGB with storage) or a format query that does not allow the import, VK_ERROR_EXTENSION_NOT_PRESENT for a
// command the layer below lacks, VK_ERROR_OUT_OF_DEVICE_MEMORY when no memory type fits, else the failing call's code.
VkResult import_back_buffer(DeviceData& device, HANDLE shared, VkExtent2D extent, const ImportConfig& config,
                            ImportedImage& out);

// Destroys the image, then frees its memory (either may be null), and empties `image`.
void release_imported(DeviceData& device, ImportedImage& image);

// The trial import of design 6.3 on one device, run once per device: each configuration of kTrialConfigs in turn,
// with all three back-buffers of the open display session imported, then released before the next configuration and
// before it returns. Later calls give the first call's answer without a call down. False, with a log line, if the
// display session is not open, the device lacks an import extension, or any import fails (it stops there).
bool trial_import(DeviceData& device);

// For vkCreateSwapchainKHR on our surface (Task 11). A device that did not exist when its physical device was asked
// for displays had no trial import there: it has it now, with one log line (design 6.3; when it fails, the swapchain
// is refused, one of design 6.10's exceptions). Any other device gets the answer it had. Meant for devices of the
// physical device that lists the display.
bool trial_import_at_swapchain(DeviceData& device);

// The usage our surface offers on a physical device (design 6.4): the usage of each trial configuration whose format
// query (step 1 above, for back-buffers of `extent`) allows the import, together; 0 if none does. Each refusal is
// logged.
VkImageUsageFlags supported_usage(InstanceData& instance, VkPhysicalDevice physical_device, VkExtent2D extent);
// The same on a device's physical device, for the open display session's back-buffers (0 while it is not open).
VkImageUsageFlags supported_usage(DeviceData& device);

// The import's part of the display-listing decision (design 6.3), once the display session is open on the adapter of
// `physical_device`: the format queries (supported_usage()), then the trial import on every device of
// `physical_device` in `instance` that exists now (Monado's does). The display is listed only if the format queries
// allow both configurations and every one of those devices passes (or there is none: a device made later has its
// trial at its first vkCreateSwapchainKHR). Every existing device has its trial, even after one fails, so that none
// is tried again later. `usage` is what the surface offers when listed.
struct ImportVerdict {
    bool listed = false;
    VkImageUsageFlags usage = 0;
};
ImportVerdict import_verdict(InstanceData& instance, VkPhysicalDevice physical_device);

} // namespace dvl
