// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The surface commands (design 6.4). vkCreateDisplayPlaneSurfaceKHR on our mode makes a surface of the layer's, and
// every query on it is answered here and never fails. Any other surface (Monado's peek window, a driver display's
// surface) goes down unchanged, ...Capabilities2EXT included: the loader's terminator emulates it for the driver.

#include <vulkan/vulkan.h>

#include <memory>
#include <optional>
#include <vector>

#include "core/display_session.hpp"
#include "layer/counted.hpp"
#include "layer/dispatch.hpp"
#include "layer/display.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/surface.hpp"

namespace dvl {
namespace {

const char* alpha_name(VkDisplayPlaneAlphaFlagBitsKHR alpha)
{
    switch (alpha) {
    case VK_DISPLAY_PLANE_ALPHA_OPAQUE_BIT_KHR: return "opaque";
    case VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR: return "global";
    case VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR: return "per pixel";
    case VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_PREMULTIPLIED_BIT_KHR: return "per pixel premultiplied";
    default: return "unknown";
    }
}

VkResult create_our_surface(VkInstance instance, const VkDisplaySurfaceCreateInfoKHR* ci, VkSurfaceKHR* out)
{
    if (ci->planeIndex != 0) {
        // Design 6.3: our mode is shown on our plane alone; this pair has no valid call down.
        log(Level::Error, "vkCreateDisplayPlaneSurfaceKHR: the layer's mode is shown on plane 0 only; plane %u refused",
            ci->planeIndex);
        return VK_ERROR_UNKNOWN;
    }
    nvdm::DisplaySession& session = display_session();
    if (!session.is_open()) {
        log(Level::Error, "vkCreateDisplayPlaneSurfaceKHR: the layer's mode was used while its display session is "
                          "not open");
        return VK_ERROR_UNKNOWN;
    }
    // The surface offers what the import allows on the physical device that lists the display in this instance
    // (design 6.4). An instance that lists it nowhere (the mode handle came from another instance) has no such answer.
    InstanceData& data = instance_data(instance);
    const std::optional<VkPhysicalDevice> listing = listing_physical_device(data);
    if (!listing) {
        log(Level::Error, "vkCreateDisplayPlaneSurfaceKHR: the layer's mode was used in an instance that has not "
                          "listed the layer's display");
        return VK_ERROR_UNKNOWN;
    }
    const nvdm::Product& product = session.product();
    auto record = std::make_unique<SurfaceRecord>();
    record->icd = icd_surface_display(*ci);
    record->instance = instance;
    record->info = *ci;
    record->info.pNext = nullptr;
    record->spec = SurfaceSpec{VkExtent2D{product.width, product.height}, surface_usage(data, *listing)};
    // Every alpha mode is accepted and shown opaque (design 6.3). An extent other than the mode's is answered with the
    // mode's in the surface caps.
    log(Level::Info,
        "vkCreateDisplayPlaneSurfaceKHR: a surface on the %s, plane 0 stack %u, alpha %s (shown opaque), extent "
        "%ux%u%s",
        product.name, ci->planeStackIndex, alpha_name(ci->alphaMode), ci->imageExtent.width, ci->imageExtent.height,
        ci->imageExtent.width == product.width && ci->imageExtent.height == product.height ? ""
                                                                                            : " (not the mode's)");
    *out = add_surface(std::move(record));
    return VK_SUCCESS;
}

// The surface queries all have this form: our surface is answered by `ours`, any other goes down.
template <class Ours, class Down>
VkResult surface_query(VkSurfaceKHR surface, Ours&& ours, Down&& down)
{
    if (const SurfaceRecord* record = find_surface(surface)) {
        return ours(*record);
    }
    return down();
}

const VkSurfaceKHR& surface_of(const VkPhysicalDeviceSurfaceInfo2KHR* info)
{
    static const VkSurfaceKHR none = VK_NULL_HANDLE;
    return info != nullptr ? info->surface : none;
}

} // namespace

VKAPI_ATTR VkResult VKAPI_CALL layer_CreateDisplayPlaneSurfaceKHR(VkInstance instance,
                                                                  const VkDisplaySurfaceCreateInfoKHR* ci,
                                                                  const VkAllocationCallbacks* alloc,
                                                                  VkSurfaceKHR* out)
{
    // With display queries, the only place the layer may fail (design 6.10).
    return guarded("vkCreateDisplayPlaneSurfaceKHR", [&] {
        if (is_our_mode(ci->displayMode)) {
            return create_our_surface(instance, ci, out);
        }
        InstanceData& data = instance_data(instance);
        const std::optional<VkPhysicalDevice> listing = listing_physical_device(data);
        if (!listing) {
            // Our plane is not listed in this instance: plane indices are the driver's own.
            return call_down(data.CreateDisplayPlaneSurfaceKHR, "vkCreateDisplayPlaneSurfaceKHR", instance, ci, alloc,
                             out);
        }
        // A driver mode, with our plane listed first (on the one physical device that lists it; the driver lists no
        // displays today, so the mode is that physical device's).
        if (ci->planeIndex == 0) {
            log(Level::Error, "vkCreateDisplayPlaneSurfaceKHR: plane 0 is the layer's, and shows only the layer's "
                              "mode; a driver mode on it is refused");
            return VK_ERROR_UNKNOWN;
        }
        const std::optional<uint32_t> driver =
            PlaneMap{driver_plane_count(data, *listing)}.to_driver(ci->planeIndex);
        if (!driver) {
            log(Level::Error, "vkCreateDisplayPlaneSurfaceKHR: plane %u is past the last plane; refused",
                ci->planeIndex);
            return VK_ERROR_UNKNOWN;
        }
        VkDisplaySurfaceCreateInfoKHR down = *ci;
        down.planeIndex = *driver;
        return call_down(data.CreateDisplayPlaneSurfaceKHR, "vkCreateDisplayPlaneSurfaceKHR", instance, &down, alloc,
                         out);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_DestroySurfaceKHR(VkInstance instance, VkSurfaceKHR surface,
                                                   const VkAllocationCallbacks* alloc)
{
    guarded_void("vkDestroySurfaceKHR", [&] {
        if (std::unique_ptr<SurfaceRecord> record = remove_surface(surface)) {
            log(Level::Info, "vkDestroySurfaceKHR: the layer's surface %p", static_cast<void*>(surface));
            return;
        }
        InstanceData& data = instance_data(instance);
        if (data.DestroySurfaceKHR == nullptr) {
            missing_below("vkDestroySurfaceKHR");
            return;
        }
        data.DestroySurfaceKHR(instance, surface, alloc);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfaceSupportKHR(VkPhysicalDevice physical_device,
                                                                        uint32_t family, VkSurfaceKHR surface,
                                                                        VkBool32* supported)
{
    return guarded("vkGetPhysicalDeviceSurfaceSupportKHR", [&] {
        return surface_query(
            surface,
            [&](const SurfaceRecord&) {
                // Every family of the physical device that lists our display: presenting goes through NVAPI. Not
                // from another physical device.
                InstanceData* instance = find_instance_data(physical_device);
                *supported = instance != nullptr && display_listed_now(*instance, physical_device) ? VK_TRUE : VK_FALSE;
                return VK_SUCCESS;
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfaceSupportKHR, "vkGetPhysicalDeviceSurfaceSupportKHR",
                                 physical_device, family, surface, supported);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfaceCapabilitiesKHR(VkPhysicalDevice physical_device,
                                                                             VkSurfaceKHR surface,
                                                                             VkSurfaceCapabilitiesKHR* caps)
{
    return guarded("vkGetPhysicalDeviceSurfaceCapabilitiesKHR", [&] {
        return surface_query(
            surface,
            [&](const SurfaceRecord& record) {
                *caps = surface_capabilities(record.spec);
                return VK_SUCCESS;
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfaceCapabilitiesKHR,
                                 "vkGetPhysicalDeviceSurfaceCapabilitiesKHR", physical_device, surface, caps);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfaceFormatsKHR(VkPhysicalDevice physical_device,
                                                                        VkSurfaceKHR surface, uint32_t* count,
                                                                        VkSurfaceFormatKHR* out)
{
    return guarded("vkGetPhysicalDeviceSurfaceFormatsKHR", [&] {
        return surface_query(
            surface,
            [&](const SurfaceRecord&) {
                const std::vector<VkSurfaceFormatKHR> formats = surface_formats();
                return fill_list(count, out, static_cast<uint32_t>(formats.size()),
                                 [&](uint32_t i, VkSurfaceFormatKHR& f) { f = formats[i]; });
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfaceFormatsKHR, "vkGetPhysicalDeviceSurfaceFormatsKHR",
                                 physical_device, surface, count, out);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfacePresentModesKHR(VkPhysicalDevice physical_device,
                                                                             VkSurfaceKHR surface, uint32_t* count,
                                                                             VkPresentModeKHR* out)
{
    return guarded("vkGetPhysicalDeviceSurfacePresentModesKHR", [&] {
        return surface_query(
            surface,
            [&](const SurfaceRecord&) {
                const std::vector<VkPresentModeKHR> modes = present_modes();
                return fill_list(count, out, static_cast<uint32_t>(modes.size()),
                                 [&](uint32_t i, VkPresentModeKHR& m) { m = modes[i]; });
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfacePresentModesKHR,
                                 "vkGetPhysicalDeviceSurfacePresentModesKHR", physical_device, surface, count, out);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfaceCapabilities2KHR(
    VkPhysicalDevice physical_device, const VkPhysicalDeviceSurfaceInfo2KHR* info, VkSurfaceCapabilities2KHR* caps)
{
    return guarded("vkGetPhysicalDeviceSurfaceCapabilities2KHR", [&] {
        return surface_query(
            surface_of(info),
            [&](const SurfaceRecord& record) {
                caps->surfaceCapabilities = surface_capabilities(record.spec);
                fill_surface_capabilities_chain(caps->pNext, record.spec, queried_present_mode(info->pNext));
                return VK_SUCCESS;
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfaceCapabilities2KHR,
                                 "vkGetPhysicalDeviceSurfaceCapabilities2KHR", physical_device, info, caps);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfaceFormats2KHR(VkPhysicalDevice physical_device,
                                                                         const VkPhysicalDeviceSurfaceInfo2KHR* info,
                                                                         uint32_t* count, VkSurfaceFormat2KHR* out)
{
    return guarded("vkGetPhysicalDeviceSurfaceFormats2KHR", [&] {
        return surface_query(
            surface_of(info),
            [&](const SurfaceRecord&) {
                const std::vector<VkSurfaceFormatKHR> formats = surface_formats();
                return fill_list(count, out, static_cast<uint32_t>(formats.size()),
                                 [&](uint32_t i, VkSurfaceFormat2KHR& f) {
                                     f.surfaceFormat = formats[i];
                                     fill_surface_format_chain(f.pNext);
                                 });
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfaceFormats2KHR,
                                 "vkGetPhysicalDeviceSurfaceFormats2KHR", physical_device, info, count, out);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfaceCapabilities2EXT(VkPhysicalDevice physical_device,
                                                                              VkSurfaceKHR surface,
                                                                              VkSurfaceCapabilities2EXT* caps)
{
    return guarded("vkGetPhysicalDeviceSurfaceCapabilities2EXT", [&] {
        return surface_query(
            surface,
            [&](const SurfaceRecord& record) {
                fill_surface_capabilities_2ext(record.spec, *caps);
                return VK_SUCCESS;
            },
            [&] {
                // The driver lacks VK_EXT_display_surface_counter; the loader's terminator emulates the query from
                // the plain one, with no counters (design 6.1).
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfaceCapabilities2EXT,
                                 "vkGetPhysicalDeviceSurfaceCapabilities2EXT", physical_device, surface, caps);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDevicePresentRectanglesKHR(VkPhysicalDevice physical_device,
                                                                           VkSurfaceKHR surface, uint32_t* count,
                                                                           VkRect2D* out)
{
    return guarded("vkGetPhysicalDevicePresentRectanglesKHR", [&] {
        return surface_query(
            surface,
            [&](const SurfaceRecord& record) {
                return fill_list(count, out, 1, [&](uint32_t, VkRect2D& r) { r = VkRect2D{{0, 0}, record.spec.extent}; });
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDevicePresentRectanglesKHR,
                                 "vkGetPhysicalDevicePresentRectanglesKHR", physical_device, surface, count, out);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceSurfacePresentModes2EXT(
    VkPhysicalDevice physical_device, const VkPhysicalDeviceSurfaceInfo2KHR* info, uint32_t* count,
    VkPresentModeKHR* out)
{
    return guarded("vkGetPhysicalDeviceSurfacePresentModes2EXT", [&] {
        return surface_query(
            surface_of(info),
            [&](const SurfaceRecord&) {
                const std::vector<VkPresentModeKHR> modes = present_modes();
                return fill_list(count, out, static_cast<uint32_t>(modes.size()),
                                 [&](uint32_t i, VkPresentModeKHR& m) { m = modes[i]; });
            },
            [&] {
                InstanceData& instance = instance_data(physical_device);
                return call_down(instance.GetPhysicalDeviceSurfacePresentModes2EXT,
                                 "vkGetPhysicalDeviceSurfacePresentModes2EXT", physical_device, info, count, out);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetDeviceGroupSurfacePresentModesKHR(VkDevice device, VkSurfaceKHR surface,
                                                                          VkDeviceGroupPresentModeFlagsKHR* modes)
{
    return guarded("vkGetDeviceGroupSurfacePresentModesKHR", [&] {
        return surface_query(
            surface,
            [&](const SurfaceRecord&) {
                *modes = VK_DEVICE_GROUP_PRESENT_MODE_LOCAL_BIT_KHR; // one GPU, presenting its own images
                return VK_SUCCESS;
            },
            [&] {
                DeviceData& data = device_data(device);
                return call_down(data.GetDeviceGroupSurfacePresentModesKHR, "vkGetDeviceGroupSurfacePresentModesKHR",
                                 device, surface, modes);
            });
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetDeviceGroupSurfacePresentModes2EXT(VkDevice device,
                                                                           const VkPhysicalDeviceSurfaceInfo2KHR* info,
                                                                           VkDeviceGroupPresentModeFlagsKHR* modes)
{
    return guarded("vkGetDeviceGroupSurfacePresentModes2EXT", [&] {
        return surface_query(
            surface_of(info),
            [&](const SurfaceRecord&) {
                *modes = VK_DEVICE_GROUP_PRESENT_MODE_LOCAL_BIT_KHR;
                return VK_SUCCESS;
            },
            [&] {
                DeviceData& data = device_data(device);
                return call_down(data.GetDeviceGroupSurfacePresentModes2EXT, "vkGetDeviceGroupSurfacePresentModes2EXT",
                                 device, info, modes);
            });
    });
}

} // namespace dvl
