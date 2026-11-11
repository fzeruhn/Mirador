// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/display.hpp"

#include <mutex>
#include <set>
#include <string>

namespace dvl {
namespace {

// The objects behind our handles. Their contents are never read: a handle is ours if it is one of these addresses.
struct LayerObject {
    uint64_t tag;
};
LayerObject g_display{0x444953504c415944ull}; // "DISPLAYD"
LayerObject g_mode{0x45444f4d59414c44ull};    // "DLAYMODE"

// Product names as strings that live for the process (never destroyed, design 6.10).
const char* lasting_name(const char* name)
{
    static std::mutex* const mutex = new std::mutex;
    static std::set<std::string>* const names = new std::set<std::string>;
    std::lock_guard<std::mutex> lock(*mutex);
    return names->insert(name != nullptr ? name : "").first->c_str();
}

} // namespace

VkDisplayKHR our_display()
{
    return reinterpret_cast<VkDisplayKHR>(&g_display);
}

VkDisplayModeKHR our_mode()
{
    return reinterpret_cast<VkDisplayModeKHR>(&g_mode);
}

bool is_our_display(VkDisplayKHR display)
{
    return display == our_display();
}

bool is_our_mode(VkDisplayModeKHR mode)
{
    return mode == our_mode();
}

std::optional<uint32_t> PlaneMap::to_driver(uint32_t layer_index) const
{
    if (layer_index == 0 || layer_index > driver_planes) {
        return std::nullopt;
    }
    return layer_index - 1;
}

uint32_t PlaneMap::count() const
{
    return driver_planes + 1;
}

void fill_display_properties(const nvdm::Product& product, VkDisplayPropertiesKHR& out)
{
    out = VkDisplayPropertiesKHR{};
    out.display = our_display();
    out.displayName = lasting_name(product.name);
    out.physicalDimensions = {0, 0};
    out.physicalResolution = {product.width, product.height};
    out.supportedTransforms = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    out.planeReorderPossible = VK_FALSE;
    out.persistentContent = VK_FALSE;
}

VkDisplayModeParametersKHR mode_parameters(const nvdm::Product& product)
{
    return VkDisplayModeParametersKHR{{product.width, product.height}, product.refresh_mhz};
}

bool is_product_mode(const nvdm::Product& product, const VkDisplayModeParametersKHR& parameters)
{
    const VkDisplayModeParametersKHR ours = mode_parameters(product);
    return parameters.visibleRegion.width == ours.visibleRegion.width &&
           parameters.visibleRegion.height == ours.visibleRegion.height && parameters.refreshRate == ours.refreshRate;
}

VkDisplayPlanePropertiesKHR our_plane_properties()
{
    return VkDisplayPlanePropertiesKHR{our_display(), 0};
}

VkDisplayPlaneCapabilitiesKHR plane_capabilities(const nvdm::Product& product)
{
    const VkExtent2D whole{product.width, product.height};
    VkDisplayPlaneCapabilitiesKHR caps{};
    caps.supportedAlpha = VK_DISPLAY_PLANE_ALPHA_OPAQUE_BIT_KHR | VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR;
    caps.minSrcPosition = {0, 0};
    caps.maxSrcPosition = {0, 0};
    caps.minSrcExtent = whole;
    caps.maxSrcExtent = whole;
    caps.minDstPosition = {0, 0};
    caps.maxDstPosition = {0, 0};
    caps.minDstExtent = whole;
    caps.maxDstExtent = whole;
    return caps;
}

} // namespace dvl
