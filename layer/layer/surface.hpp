// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>
#include <vulkan/vk_icd.h>

#include <cstddef>
#include <memory>
#include <optional>
#include <string>
#include <type_traits>
#include <vector>

namespace dvl {

// The surface on the layer's display (design 6.4): the answers to every surface query, without any call, and the
// layer's surface objects. The intercepts that give them are in surface_intercepts.cpp. No surface query on our surface
// ever fails.

// What a surface's answers depend on: the mode's extent, and the image usage the import supports.
struct SurfaceSpec {
    VkExtent2D extent;
    VkImageUsageFlags usage;
};

// The usage of the two image configurations the import offers together (import_rules.hpp's kComputeConfig and
// kGraphicsConfig): Monado's compute compositor (storage) and its graphics compositor (colour attachment), both sampled
// and both transfer source and destination. A listed display's surface offers this: its usage comes from the format
// queries of the decision (design 6.4), and the display is listed only if both configurations pass them.
inline constexpr VkImageUsageFlags kSurfaceUsage = VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                                                   VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                                                   VK_IMAGE_USAGE_TRANSFER_DST_BIT;

// The image count: the display's three back-buffers.
inline constexpr uint32_t kSurfaceImageCount = 3;

// Extent min = max = current = spec.extent; images min = max = 3; one array layer; identity transform only; opaque
// composite alpha only; usage spec.usage.
VkSurfaceCapabilitiesKHR surface_capabilities(const SurfaceSpec& spec);

// The same, with the VBLANK surface counter (VK_EXT_display_surface_counter). Writes every member of `out` but sType
// and pNext.
void fill_surface_capabilities_2ext(const SurfaceSpec& spec, VkSurfaceCapabilities2EXT& out);

// The structs the layer knows in the pNext chain of a VkSurfaceCapabilities2KHR (the output of
// vkGetPhysicalDeviceSurfaceCapabilities2KHR); others are left alone. Present id 2, present wait 2 and present timing
// are on (design 6.4, 6.11: VK_EXT_present_timing reports VkPresentId2KHR ids, and the layer completes and waits for
// both kinds of id alike); everything else optional is off: no shared present, no protected, no full-screen exclusive,
// no present barrier, no local dimming, no present scaling or low-latency modes. The present modes compatible with
// `present_mode` (the query's VkSurfacePresentModeEXT) are that mode alone; none without one.
void fill_surface_capabilities_chain(void* next, const SurfaceSpec& spec, std::optional<VkPresentModeKHR> present_mode);

// The VkSurfacePresentModeEXT in the input chain of a VkPhysicalDeviceSurfaceInfo2KHR, if there is one.
std::optional<VkPresentModeKHR> queried_present_mode(const void* next);

// The structs the layer knows in the pNext chain of one VkSurfaceFormat2KHR: no image compression control.
void fill_surface_format_chain(void* next);

// R8G8B8A8_UNORM and R8G8B8A8_SRGB, both SRGB_NONLINEAR: the back-buffer is R8G8B8A8_TYPELESS.
std::vector<VkSurfaceFormatKHR> surface_formats();

// FIFO, MAILBOX, IMMEDIATE, FIFO_RELAXED: all are paced as FIFO.
std::vector<VkPresentModeKHR> present_modes();

// The swapchain-parameter check of design 6.4, for vkCreateSwapchainKHR on our surface. A swapchain is refused for one
// combination only: an SRGB format with storage usage (SRGB has no storage support on this driver). Anything else the
// caps would not allow is accepted and named in `notes` (the swapchain is made from the caps; the caller logs them).
struct SwapchainCheck {
    bool refused = false;
    std::string reason;             // why, when refused
    std::vector<std::string> notes; // what differs from the caps, when accepted
};
SwapchainCheck check_swapchain_parameters(const VkSwapchainCreateInfoKHR& ci, const SurfaceSpec& spec);

// A surface of the layer's, made by vkCreateDisplayPlaneSurfaceKHR on our mode. Its handle is its address, and it
// starts as the loader and drivers lay out a display surface (VkIcdSurfaceDisplay, vk_icd.h), filled from the create
// info: design 5's first fallback, so that a layer above ours that looks inside a surface handle finds a display
// surface, not what it would take for another platform's.
struct SurfaceRecord {
    VkIcdSurfaceDisplay icd{}; // first: the handle points at it
    VkInstance instance = VK_NULL_HANDLE;
    VkDisplaySurfaceCreateInfoKHR info{}; // as the application gave it, pNext dropped
    SurfaceSpec spec{};
};
static_assert(std::is_standard_layout_v<SurfaceRecord>, "the header's place is defined for a standard-layout type");
static_assert(offsetof(SurfaceRecord, icd) == 0, "the handle points at the VkIcdSurfaceDisplay header");
static_assert(offsetof(VkIcdSurfaceDisplay, base) == 0 && offsetof(VkIcdSurfaceDisplay, displayMode) == 8 &&
                  offsetof(VkIcdSurfaceDisplay, planeIndex) == 16 &&
                  offsetof(VkIcdSurfaceDisplay, planeStackIndex) == 20 &&
                  offsetof(VkIcdSurfaceDisplay, transform) == 24 && offsetof(VkIcdSurfaceDisplay, globalAlpha) == 28 &&
                  offsetof(VkIcdSurfaceDisplay, alphaMode) == 32 && offsetof(VkIcdSurfaceDisplay, imageExtent) == 36 &&
                  sizeof(VkIcdSurfaceDisplay) == 48,
              "VkIcdSurfaceDisplay as the loader and drivers lay it out on x64");

// The header of a display surface made from `ci`.
VkIcdSurfaceDisplay icd_surface_display(const VkDisplaySurfaceCreateInfoKHR& ci);

// The registry of the layer's surfaces. The handle is the record's address; find_surface() looks it up without
// dereferencing it, so any handle (the peek window's Win32 surface, VK_NULL_HANDLE) can be asked about. Any thread.
VkSurfaceKHR add_surface(std::unique_ptr<SurfaceRecord> record);
const SurfaceRecord* find_surface(VkSurfaceKHR surface); // null if not ours
std::unique_ptr<SurfaceRecord> remove_surface(VkSurfaceKHR surface); // null if not ours

} // namespace dvl
