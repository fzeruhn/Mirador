// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace dvl {

// The decisions of the back-buffer import that need no driver (design 6.3, 6.4). Pure: import.hpp makes the calls.

// One way of importing a back-buffer: the image's format and usage. The view-format list and the create flags follow
// from them (view_formats(), MUTABLE_FORMAT always).
struct ImportConfig {
    VkFormat format;
    VkImageUsageFlags usage;
};

// The two configurations the surface offers and the trial import proves (design 6.3): Monado's compute compositor
// (the default on Windows), which writes the image as storage, and its graphics compositor (XRT_COMPOSITOR_COMPUTE=0),
// which renders into SRGB. Both are sampled and both transfer source and destination.
inline constexpr ImportConfig kComputeConfig{VK_FORMAT_R8G8B8A8_UNORM,
                                             VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                                 VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT};
inline constexpr ImportConfig kGraphicsConfig{VK_FORMAT_R8G8B8A8_SRGB,
                                              VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_SAMPLED_BIT |
                                                  VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT};
// In the order the trial import runs them.
inline constexpr ImportConfig kTrialConfigs[] = {kComputeConfig, kGraphicsConfig};

// The display's back-buffers are legacy D3D11 KMT shared handles (NVAPI's CreateBackBuffer): not reference counted,
// never closed, and Vulkan does not take them over.
inline constexpr VkExternalMemoryHandleTypeFlagBits kBackBufferHandleType =
    VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT;

// The view formats an imported image is created with (VkImageFormatListCreateInfo, with MUTABLE_FORMAT), which are
// also the formats it may have itself. With storage in the usage, {R8G8B8A8_UNORM}: SRGB has no storage support, and
// every format of the list must support the usage. Without, {R8G8B8A8_UNORM, R8G8B8A8_SRGB}. nullopt for what cannot
// be imported: R8G8B8A8_SRGB with storage, and any format but R8G8B8A8_UNORM and _SRGB (the back-buffer is
// R8G8B8A8_TYPELESS). This is also the rule for which configuration a usage can have: storage only as UNORM, anything
// else as UNORM or SRGB.
std::optional<std::vector<VkFormat>> view_formats(VkFormat format, VkImageUsageFlags usage);

// "R8G8B8A8_UNORM", "R8G8B8A8_SRGB", or "format <number>", for log lines.
std::string format_name(VkFormat format);

// What vkGetPhysicalDeviceImageFormatProperties2KHR said about a configuration with the back-buffer's handle type
// (design 6.4's first check). `ok` needs VK_SUCCESS, IMPORTABLE among the external memory features, and a maxExtent
// that holds the back-buffer. `dedicated_only` is DEDICATED_ONLY: the import always allocates dedicated memory, so it
// is honoured either way, and only logged. `reason` says why not, for the log line, when not ok.
struct ImportSupport {
    bool ok = false;
    bool dedicated_only = false;
    std::string reason;
};
ImportSupport import_support(VkResult result, const VkImageFormatProperties& properties,
                             const VkExternalMemoryProperties& external, VkExtent2D extent);

// The memory type an imported back-buffer is allocated with (design 6.4). `handle_bits` is
// VkMemoryWin32HandlePropertiesKHR::memoryTypeBits, nullopt if vkGetMemoryWin32HandlePropertiesKHR failed;
// `image_bits` the image's VkMemoryRequirements::memoryTypeBits. A type must be in both (the import, and the bind to
// the image): the first device-local one, else the first. If the handle gave no bits (failed, or 0), the image's bits
// alone, as Monado's target did, with `fallback` set so that the caller logs it once. nullopt if no type is left.
struct MemoryPick {
    std::optional<uint32_t> index;
    bool fallback = false;
};
MemoryPick pick_memory_type(std::optional<uint32_t> handle_bits, uint32_t image_bits,
                            const VkPhysicalDeviceMemoryProperties& memory);

} // namespace dvl
