// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/surface.hpp"

#include <algorithm>
#include <mutex>
#include <unordered_map>
#include <utility>

#include "layer/present_timing.hpp"

namespace dvl {
namespace {

const VkSurfaceFormatKHR kFormats[] = {
    {VK_FORMAT_R8G8B8A8_UNORM, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR},
    {VK_FORMAT_R8G8B8A8_SRGB, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR},
};

const VkPresentModeKHR kPresentModes[] = {
    VK_PRESENT_MODE_FIFO_KHR,
    VK_PRESENT_MODE_MAILBOX_KHR,
    VK_PRESENT_MODE_IMMEDIATE_KHR,
    VK_PRESENT_MODE_FIFO_RELAXED_KHR,
};

template <class T>
T& as(VkBaseOutStructure* s)
{
    return *reinterpret_cast<T*>(s);
}

// A two-call list inside an output struct (count and pointer members).
void list_modes(uint32_t& count, VkPresentModeKHR* out, std::optional<VkPresentModeKHR> mode)
{
    const uint32_t total = mode ? 1 : 0;
    if (out == nullptr) {
        count = total;
        return;
    }
    count = std::min(count, total);
    if (count > 0) {
        out[0] = *mode;
    }
}

struct SurfaceRegistry {
    std::mutex mutex;
    std::unordered_map<const void*, std::unique_ptr<SurfaceRecord>> records;
};

// Made at first use and never destroyed (design 6.10).
SurfaceRegistry& registry()
{
    static SurfaceRegistry* const r = new SurfaceRegistry;
    return *r;
}

const void* key(VkSurfaceKHR surface)
{
    return reinterpret_cast<const void*>(surface);
}

} // namespace

VkSurfaceCapabilitiesKHR surface_capabilities(const SurfaceSpec& spec)
{
    VkSurfaceCapabilitiesKHR caps{};
    caps.minImageCount = kSurfaceImageCount;
    caps.maxImageCount = kSurfaceImageCount;
    caps.currentExtent = spec.extent;
    caps.minImageExtent = spec.extent;
    caps.maxImageExtent = spec.extent;
    caps.maxImageArrayLayers = 1;
    caps.supportedTransforms = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    caps.currentTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    caps.supportedCompositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    caps.supportedUsageFlags = spec.usage;
    return caps;
}

void fill_surface_capabilities_2ext(const SurfaceSpec& spec, VkSurfaceCapabilities2EXT& out)
{
    const VkSurfaceCapabilitiesKHR caps = surface_capabilities(spec);
    out.minImageCount = caps.minImageCount;
    out.maxImageCount = caps.maxImageCount;
    out.currentExtent = caps.currentExtent;
    out.minImageExtent = caps.minImageExtent;
    out.maxImageExtent = caps.maxImageExtent;
    out.maxImageArrayLayers = caps.maxImageArrayLayers;
    out.supportedTransforms = caps.supportedTransforms;
    out.currentTransform = caps.currentTransform;
    out.supportedCompositeAlpha = caps.supportedCompositeAlpha;
    out.supportedUsageFlags = caps.supportedUsageFlags;
    out.supportedSurfaceCounters = VK_SURFACE_COUNTER_VBLANK_BIT_EXT;
}

void fill_surface_capabilities_chain(void* next, const SurfaceSpec& spec, std::optional<VkPresentModeKHR> present_mode)
{
    for (auto* s = static_cast<VkBaseOutStructure*>(next); s != nullptr; s = s->pNext) {
        switch (s->sType) {
        case VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_PRESENT_ID_2_KHR:
            as<VkSurfaceCapabilitiesPresentId2KHR>(s).presentId2Supported = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_PRESENT_WAIT_2_KHR:
            as<VkSurfaceCapabilitiesPresentWait2KHR>(s).presentWait2Supported = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PRESENT_TIMING_SURFACE_CAPABILITIES_EXT:
            fill_present_timing_caps(as<VkPresentTimingSurfaceCapabilitiesEXT>(s), present_at_enabled());
            break;
        case VK_STRUCTURE_TYPE_SHARED_PRESENT_SURFACE_CAPABILITIES_KHR:
            as<VkSharedPresentSurfaceCapabilitiesKHR>(s).sharedPresentSupportedUsageFlags = 0;
            break;
        case VK_STRUCTURE_TYPE_SURFACE_PROTECTED_CAPABILITIES_KHR:
            as<VkSurfaceProtectedCapabilitiesKHR>(s).supportsProtected = VK_FALSE;
            break;
        case VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_FULL_SCREEN_EXCLUSIVE_EXT:
            as<VkSurfaceCapabilitiesFullScreenExclusiveEXT>(s).fullScreenExclusiveSupported = VK_FALSE;
            break;
        case VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_PRESENT_BARRIER_NV:
            as<VkSurfaceCapabilitiesPresentBarrierNV>(s).presentBarrierSupported = VK_FALSE;
            break;
        case VK_STRUCTURE_TYPE_DISPLAY_NATIVE_HDR_SURFACE_CAPABILITIES_AMD:
            as<VkDisplayNativeHdrSurfaceCapabilitiesAMD>(s).localDimmingSupport = VK_FALSE;
            break;
        case VK_STRUCTURE_TYPE_SURFACE_PRESENT_SCALING_CAPABILITIES_EXT: {
            auto& scaling = as<VkSurfacePresentScalingCapabilitiesEXT>(s);
            scaling.supportedPresentScaling = 0;
            scaling.supportedPresentGravityX = 0;
            scaling.supportedPresentGravityY = 0;
            scaling.minScaledImageExtent = spec.extent;
            scaling.maxScaledImageExtent = spec.extent;
            break;
        }
        case VK_STRUCTURE_TYPE_SURFACE_PRESENT_MODE_COMPATIBILITY_EXT: {
            auto& compatible = as<VkSurfacePresentModeCompatibilityEXT>(s);
            list_modes(compatible.presentModeCount, compatible.pPresentModes, present_mode);
            break;
        }
        case VK_STRUCTURE_TYPE_LATENCY_SURFACE_CAPABILITIES_NV: {
            auto& latency = as<VkLatencySurfaceCapabilitiesNV>(s);
            list_modes(latency.presentModeCount, latency.pPresentModes, std::nullopt);
            break;
        }
        default:
            break; // not known to the layer: left as the application set it
        }
    }
}

std::optional<VkPresentModeKHR> queried_present_mode(const void* next)
{
    for (auto* s = static_cast<const VkBaseInStructure*>(next); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_SURFACE_PRESENT_MODE_EXT) {
            return reinterpret_cast<const VkSurfacePresentModeEXT*>(s)->presentMode;
        }
    }
    return std::nullopt;
}

void fill_surface_format_chain(void* next)
{
    for (auto* s = static_cast<VkBaseOutStructure*>(next); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_IMAGE_COMPRESSION_PROPERTIES_EXT) {
            auto& compression = as<VkImageCompressionPropertiesEXT>(s);
            compression.imageCompressionFlags = VK_IMAGE_COMPRESSION_DEFAULT_EXT;
            compression.imageCompressionFixedRateFlags = VK_IMAGE_COMPRESSION_FIXED_RATE_NONE_EXT;
        }
    }
}

std::vector<VkSurfaceFormatKHR> surface_formats()
{
    return std::vector<VkSurfaceFormatKHR>(std::begin(kFormats), std::end(kFormats));
}

std::vector<VkPresentModeKHR> present_modes()
{
    return std::vector<VkPresentModeKHR>(std::begin(kPresentModes), std::end(kPresentModes));
}

SwapchainCheck check_swapchain_parameters(const VkSwapchainCreateInfoKHR& ci, const SurfaceSpec& spec)
{
    SwapchainCheck check;
    if (ci.imageFormat == VK_FORMAT_R8G8B8A8_SRGB && (ci.imageUsage & VK_IMAGE_USAGE_STORAGE_BIT) != 0) {
        check.refused = true;
        check.reason = "an SRGB swapchain with storage usage cannot be made: R8G8B8A8_SRGB has no storage support";
        return check;
    }

    const VkSurfaceCapabilitiesKHR caps = surface_capabilities(spec);
    const bool format_offered = std::any_of(std::begin(kFormats), std::end(kFormats), [&](const VkSurfaceFormatKHR& f) {
        return f.format == ci.imageFormat && f.colorSpace == ci.imageColorSpace;
    });
    if (!format_offered) {
        check.notes.push_back("format " + std::to_string(ci.imageFormat) + " in colour space " +
                              std::to_string(ci.imageColorSpace) + " is not offered");
    }
    if (ci.imageExtent.width != caps.currentExtent.width || ci.imageExtent.height != caps.currentExtent.height) {
        check.notes.push_back("extent " + std::to_string(ci.imageExtent.width) + "x" +
                              std::to_string(ci.imageExtent.height) + " is not the mode's " +
                              std::to_string(caps.currentExtent.width) + "x" + std::to_string(caps.currentExtent.height));
    }
    if (ci.minImageCount > caps.maxImageCount) {
        check.notes.push_back(std::to_string(ci.minImageCount) + " images asked for, the display has " +
                              std::to_string(caps.maxImageCount));
    }
    if (ci.imageArrayLayers != caps.maxImageArrayLayers) {
        check.notes.push_back(std::to_string(ci.imageArrayLayers) + " array layers asked for, the display has 1");
    }
    if ((ci.imageUsage & ~caps.supportedUsageFlags) != 0) {
        check.notes.push_back("usage bits " + std::to_string(ci.imageUsage & ~caps.supportedUsageFlags) +
                              " are not supported");
    }
    if (ci.preTransform != caps.currentTransform) {
        check.notes.push_back("transform " + std::to_string(ci.preTransform) + " is not the identity");
    }
    if (ci.compositeAlpha != VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR) {
        check.notes.push_back("composite alpha " + std::to_string(ci.compositeAlpha) + " is shown opaque");
    }
    if (std::find(std::begin(kPresentModes), std::end(kPresentModes), ci.presentMode) == std::end(kPresentModes)) {
        check.notes.push_back("present mode " + std::to_string(ci.presentMode) + " is not offered");
    }
    return check;
}

VkIcdSurfaceDisplay icd_surface_display(const VkDisplaySurfaceCreateInfoKHR& ci)
{
    VkIcdSurfaceDisplay header{};
    header.base.platform = VK_ICD_WSI_PLATFORM_DISPLAY;
    header.displayMode = ci.displayMode;
    header.planeIndex = ci.planeIndex;
    header.planeStackIndex = ci.planeStackIndex;
    header.transform = ci.transform;
    header.globalAlpha = ci.globalAlpha;
    header.alphaMode = ci.alphaMode;
    header.imageExtent = ci.imageExtent;
    return header;
}

VkSurfaceKHR add_surface(std::unique_ptr<SurfaceRecord> record)
{
    const VkSurfaceKHR handle = reinterpret_cast<VkSurfaceKHR>(record.get());
    SurfaceRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    r.records[key(handle)] = std::move(record);
    return handle;
}

const SurfaceRecord* find_surface(VkSurfaceKHR surface)
{
    SurfaceRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    const auto it = r.records.find(key(surface));
    return it == r.records.end() ? nullptr : it->second.get();
}

std::unique_ptr<SurfaceRecord> remove_surface(VkSurfaceKHR surface)
{
    SurfaceRegistry& r = registry();
    std::lock_guard<std::mutex> lock(r.mutex);
    const auto it = r.records.find(key(surface));
    if (it == r.records.end()) {
        return nullptr;
    }
    std::unique_ptr<SurfaceRecord> record = std::move(it->second);
    r.records.erase(it);
    return record;
}

} // namespace dvl
