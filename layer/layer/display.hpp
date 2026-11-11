// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <optional>

#include "core/product_table.hpp"

namespace dvl {

// The layer's display, its mode and its plane (design 6.3): the answers, without any call. The intercepts that give
// them are in display_intercepts.cpp.

// The layer's VkDisplayKHR and its one VkDisplayModeKHR. Each is a process-wide object of the layer's, and its
// address is the handle, so telling ours from the driver's is a comparison: a handle that is not ours is never
// dereferenced. They stay valid for the process (the DLL is pinned), whatever instance asks.
VkDisplayKHR our_display();
VkDisplayModeKHR our_mode();
bool is_our_display(VkDisplayKHR display);
bool is_our_mode(VkDisplayModeKHR mode);

// Plane indices as the application sees them: ours is 0, the driver's planes follow at 1..driver_planes.
struct PlaneMap {
    uint32_t driver_planes = 0;

    // The driver's own index of a driver plane (layer_index - 1). nullopt for 0 (our plane) and for an index past the
    // last plane: neither has a valid call down, so the layer answers or refuses those itself.
    std::optional<uint32_t> to_driver(uint32_t layer_index) const;
    // All planes: ours and the driver's.
    uint32_t count() const;
};

// VkDisplayPropertiesKHR of the display: the product's name (a copy kept for the life of the process, whatever
// `product` was: Monado keeps the pointer after freeing the array), its resolution, identity transform,
// planeReorderPossible and persistentContent false. physicalDimensions is 0x0 (unknown): Product carries no size.
void fill_display_properties(const nvdm::Product& product, VkDisplayPropertiesKHR& out);

// The one mode: the product's resolution and refresh rate (millihertz).
VkDisplayModeParametersKHR mode_parameters(const nvdm::Product& product);
bool is_product_mode(const nvdm::Product& product, const VkDisplayModeParametersKHR& parameters);

// Our plane (index 0): it shows our display, at stack index 0.
VkDisplayPlanePropertiesKHR our_plane_properties();

// Our mode on our plane: source and destination are the whole mode, at 0,0; alpha OPAQUE | GLOBAL (Monado asks for
// GLOBAL when no per-pixel mode is offered; every alpha mode is accepted and shown opaque).
VkDisplayPlaneCapabilitiesKHR plane_capabilities(const nvdm::Product& product);

} // namespace dvl
