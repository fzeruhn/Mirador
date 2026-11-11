// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The display, plane and mode commands (design 6.3). Our display is listed first on the physical device whose adapter
// drives the headset, our plane is index 0 there and the driver's planes follow; every command on our display or mode
// is answered here, and anything else goes down unchanged (plane indices shifted back where our plane is listed).
// Also the display session the intercepts use, and its test seam.

#include <vulkan/vulkan.h>

#include <atomic>
#include <cstring>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>

#include "core/display_session.hpp"
#include "layer/counted.hpp"
#include "layer/device_setup.hpp"
#include "layer/dispatch.hpp"
#include "layer/display.hpp"
#include "layer/import.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"

namespace dvl {
namespace {

std::atomic<nvdm::DisplaySession*> g_test_session{nullptr};

// The session's own lines (design 6.3: it names the failing step and its return value itself), into the layer's log:
// a step's failure at Error, so MIRADOR_DIRECT_MODE_LOG=warn still shows it (design 8), warnings at Warn.
void log_session_line(const std::string& line)
{
    const SessionLine s = session_line(line);
    log(s.level, "%.*s", static_cast<int>(s.text.size()), s.text.data());
}

// The product of the open display session. Only our own display and mode get here, and those are listed only once
// the session is open; anything else is a stale handle.
const nvdm::Product& open_product()
{
    nvdm::DisplaySession& session = display_session();
    if (!session.is_open()) {
        throw std::runtime_error("the layer's display was used while its display session is not open");
    }
    return session.product();
}

// The display-listing decision on a physical device, and the usage our surface offers there when listed.
struct Decision {
    bool listed = false;
    VkImageUsageFlags usage = 0;
};

// The first display query on a physical device (design 6.3). Not NVIDIA: no NVAPI at all. Otherwise its adapter LUID
// goes to the display session, which runs the holder check, opens (or picks up where it stopped), and says whether
// that adapter drives the display; it logs every failure and success itself. Then the import: the format queries and
// the trial import on the physical device's devices (import_verdict()).
Decision decide(InstanceData& instance, VkPhysicalDevice physical_device)
{
    VkPhysicalDeviceProperties properties{};
    if (instance.GetPhysicalDeviceProperties != nullptr) {
        instance.GetPhysicalDeviceProperties(physical_device, &properties);
    }
    if (properties.vendorID != kNvidiaVendorId) {
        log(Level::Debug, "display query on %s (vendor 0x%04x): not NVIDIA, the layer's display is not there",
            properties.deviceName, properties.vendorID);
        return Decision{};
    }
    if (instance.GetPhysicalDeviceProperties2KHR == nullptr) {
        log(Level::Error, "display query on %s: vkGetPhysicalDeviceProperties2KHR is missing, so the adapter LUID is "
                          "unknown and no display is listed (enable VK_KHR_get_physical_device_properties2)",
            properties.deviceName);
        return Decision{};
    }
    VkPhysicalDeviceIDPropertiesKHR id{};
    id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES_KHR;
    VkPhysicalDeviceProperties2KHR properties2{};
    properties2.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2_KHR;
    properties2.pNext = &id;
    instance.GetPhysicalDeviceProperties2KHR(physical_device, &properties2);
    if (id.deviceLUIDValid != VK_TRUE) {
        log(Level::Warn, "display query on %s: the driver gives no adapter LUID, so no display is listed",
            properties.deviceName);
        return Decision{};
    }
    static_assert(sizeof(LUID) == VK_LUID_SIZE, "a Vulkan device LUID is a Windows LUID");
    LUID luid{};
    std::memcpy(&luid, id.deviceLUID, sizeof(luid));

    const nvdm::OpenResult result = display_session().open(luid);
    Decision decision;
    if (result.ok) {
        // Design 6.3: the import fails here, where Monado can still stop cleanly, if it is going to fail at all.
        const ImportVerdict verdict = import_verdict(instance, physical_device);
        decision.listed = verdict.listed;
        decision.usage = verdict.usage;
    }
    log(Level::Debug, "display query on %s: the layer's display is %s", properties.deviceName,
        decision.listed ? "listed first" : "not listed");
    return decision;
}

// Plane-indexed calls on a physical device. nullopt: the index is our plane's (0) or past the last plane, and has no
// valid call down. Otherwise the driver's own index.
std::optional<uint32_t> driver_plane(InstanceData& instance, VkPhysicalDevice physical_device, uint32_t plane)
{
    if (plane == 0) {
        return std::nullopt;
    }
    return PlaneMap{driver_plane_count(instance, physical_device)}.to_driver(plane);
}

void set_mode_chain(void* next)
{
    for (auto* s = static_cast<VkBaseOutStructure*>(next); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_DISPLAY_MODE_STEREO_PROPERTIES_NV) {
            reinterpret_cast<VkDisplayModeStereoPropertiesNV*>(s)->hdmi3DSupported = VK_FALSE;
        }
    }
}

// What the plane can do with a mode it cannot show: nothing (no alpha mode, no extents).
VkDisplayPlaneCapabilitiesKHR no_capabilities()
{
    return VkDisplayPlaneCapabilitiesKHR{};
}

// The answer to vkGetDisplayPlaneCapabilitiesKHR and ...2KHR, or nullopt if the call goes down with the plane index
// in `down_plane` (design 6.3: pairs with no valid call down are answered here).
std::optional<VkDisplayPlaneCapabilitiesKHR> our_plane_answer(InstanceData& instance, VkPhysicalDevice physical_device,
                                                              VkDisplayModeKHR mode, uint32_t plane,
                                                              uint32_t& down_plane)
{
    down_plane = plane;
    if (is_our_mode(mode)) {
        return plane == 0 ? plane_capabilities(open_product()) : no_capabilities();
    }
    if (!display_listed(instance, physical_device)) {
        return std::nullopt; // no plane of ours on this physical device: nothing is shifted
    }
    const std::optional<uint32_t> driver = driver_plane(instance, physical_device, plane);
    if (!driver) {
        return no_capabilities(); // the driver's mode on our plane, or a plane past the last
    }
    down_plane = *driver;
    return std::nullopt;
}

} // namespace

nvdm::DisplaySession& display_session()
{
    if (nvdm::DisplaySession* test = g_test_session.load(std::memory_order_acquire)) {
        return *test;
    }
    static nvdm::DisplaySession* const process = [] {
        nvdm::set_process_session_log(&log_session_line);
        return &nvdm::process_session();
    }();
    return *process;
}

void set_display_session_for_tests(nvdm::DisplaySession* session)
{
    g_test_session.store(session, std::memory_order_release);
}

bool display_listed(InstanceData& instance, VkPhysicalDevice physical_device)
{
    std::lock_guard<std::mutex> lock(instance.display_mutex);
    const auto it = instance.display_listed_on.find(physical_device);
    if (it != instance.display_listed_on.end()) {
        return it->second;
    }
    const Decision decision = decide(instance, physical_device);
    instance.display_listed_on.emplace(physical_device, decision.listed);
    if (decision.listed) {
        instance.surface_usage_on.emplace(physical_device, decision.usage);
    }
    return decision.listed;
}

VkImageUsageFlags surface_usage(InstanceData& instance, VkPhysicalDevice physical_device)
{
    std::lock_guard<std::mutex> lock(instance.display_mutex);
    const auto it = instance.surface_usage_on.find(physical_device);
    return it != instance.surface_usage_on.end() ? it->second : 0;
}

bool display_listed_now(InstanceData& instance, VkPhysicalDevice physical_device)
{
    std::lock_guard<std::mutex> lock(instance.display_mutex);
    const auto it = instance.display_listed_on.find(physical_device);
    return it != instance.display_listed_on.end() && it->second;
}

std::optional<VkPhysicalDevice> listing_physical_device(InstanceData& instance)
{
    std::lock_guard<std::mutex> lock(instance.display_mutex);
    for (const auto& entry : instance.display_listed_on) {
        if (entry.second) {
            return entry.first;
        }
    }
    return std::nullopt;
}

uint32_t driver_plane_count(InstanceData& instance, VkPhysicalDevice physical_device)
{
    uint32_t n = 0;
    if (instance.GetPhysicalDeviceDisplayPlanePropertiesKHR == nullptr ||
        instance.GetPhysicalDeviceDisplayPlanePropertiesKHR(physical_device, &n, nullptr) < 0) {
        return 0;
    }
    return n;
}

VkResult missing_below(const char* command) noexcept
{
    log(Level::Error, "%s: the layer below does not have it", command);
    return VK_ERROR_EXTENSION_NOT_PRESENT;
}

/*
 * Displays and planes, listed by physical device.
 */

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceDisplayPropertiesKHR(VkPhysicalDevice physical_device,
                                                                           uint32_t* count,
                                                                           VkDisplayPropertiesKHR* out)
{
    return guarded("vkGetPhysicalDeviceDisplayPropertiesKHR", [&] {
        InstanceData& instance = instance_data(physical_device);
        const auto down = [&](uint32_t* n, VkDisplayPropertiesKHR* list) {
            return call_down(instance.GetPhysicalDeviceDisplayPropertiesKHR, "vkGetPhysicalDeviceDisplayPropertiesKHR",
                             physical_device, n, list);
        };
        if (!display_listed(instance, physical_device)) {
            return down(count, out);
        }
        const nvdm::Product& product = open_product();
        return list_ours_first(
            count, out, 1, [&](uint32_t, VkDisplayPropertiesKHR& p) { fill_display_properties(product, p); }, down);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceDisplayProperties2KHR(VkPhysicalDevice physical_device,
                                                                            uint32_t* count,
                                                                            VkDisplayProperties2KHR* out)
{
    return guarded("vkGetPhysicalDeviceDisplayProperties2KHR", [&] {
        InstanceData& instance = instance_data(physical_device);
        const auto down = [&](uint32_t* n, VkDisplayProperties2KHR* list) {
            return call_down(instance.GetPhysicalDeviceDisplayProperties2KHR,
                             "vkGetPhysicalDeviceDisplayProperties2KHR", physical_device, n, list);
        };
        if (!display_listed(instance, physical_device)) {
            return down(count, out);
        }
        const nvdm::Product& product = open_product();
        return list_ours_first(
            count, out, 1,
            [&](uint32_t, VkDisplayProperties2KHR& p) { fill_display_properties(product, p.displayProperties); },
            down);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceDisplayPlanePropertiesKHR(VkPhysicalDevice physical_device,
                                                                                uint32_t* count,
                                                                                VkDisplayPlanePropertiesKHR* out)
{
    return guarded("vkGetPhysicalDeviceDisplayPlanePropertiesKHR", [&] {
        InstanceData& instance = instance_data(physical_device);
        const auto down = [&](uint32_t* n, VkDisplayPlanePropertiesKHR* list) {
            return call_down(instance.GetPhysicalDeviceDisplayPlanePropertiesKHR,
                             "vkGetPhysicalDeviceDisplayPlanePropertiesKHR", physical_device, n, list);
        };
        if (!display_listed(instance, physical_device)) {
            return down(count, out);
        }
        return list_ours_first(
            count, out, 1, [](uint32_t, VkDisplayPlanePropertiesKHR& p) { p = our_plane_properties(); }, down);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPhysicalDeviceDisplayPlaneProperties2KHR(VkPhysicalDevice physical_device,
                                                                                 uint32_t* count,
                                                                                 VkDisplayPlaneProperties2KHR* out)
{
    return guarded("vkGetPhysicalDeviceDisplayPlaneProperties2KHR", [&] {
        InstanceData& instance = instance_data(physical_device);
        const auto down = [&](uint32_t* n, VkDisplayPlaneProperties2KHR* list) {
            return call_down(instance.GetPhysicalDeviceDisplayPlaneProperties2KHR,
                             "vkGetPhysicalDeviceDisplayPlaneProperties2KHR", physical_device, n, list);
        };
        if (!display_listed(instance, physical_device)) {
            return down(count, out);
        }
        return list_ours_first(
            count, out, 1,
            [](uint32_t, VkDisplayPlaneProperties2KHR& p) { p.displayPlaneProperties = our_plane_properties(); },
            down);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetDisplayPlaneSupportedDisplaysKHR(VkPhysicalDevice physical_device,
                                                                         uint32_t plane, uint32_t* count,
                                                                         VkDisplayKHR* out)
{
    return guarded("vkGetDisplayPlaneSupportedDisplaysKHR", [&] {
        InstanceData& instance = instance_data(physical_device);
        if (!display_listed(instance, physical_device)) {
            return call_down(instance.GetDisplayPlaneSupportedDisplaysKHR, "vkGetDisplayPlaneSupportedDisplaysKHR",
                             physical_device, plane, count, out);
        }
        if (plane == 0) {
            return fill_list(count, out, 1, [](uint32_t, VkDisplayKHR& d) { d = our_display(); });
        }
        const std::optional<uint32_t> driver = driver_plane(instance, physical_device, plane);
        if (!driver) {
            return fill_list(count, out, 0, [](uint32_t, VkDisplayKHR&) {}); // no such plane: no displays
        }
        return call_down(instance.GetDisplayPlaneSupportedDisplaysKHR, "vkGetDisplayPlaneSupportedDisplaysKHR",
                         physical_device, *driver, count, out);
    });
}

/*
 * Modes and plane capabilities.
 */

VKAPI_ATTR VkResult VKAPI_CALL layer_GetDisplayModePropertiesKHR(VkPhysicalDevice physical_device,
                                                                 VkDisplayKHR display, uint32_t* count,
                                                                 VkDisplayModePropertiesKHR* out)
{
    return guarded("vkGetDisplayModePropertiesKHR", [&] {
        if (is_our_display(display)) {
            const nvdm::Product& product = open_product();
            return fill_list(count, out, 1, [&](uint32_t, VkDisplayModePropertiesKHR& m) {
                m.displayMode = our_mode();
                m.parameters = mode_parameters(product);
            });
        }
        InstanceData& instance = instance_data(physical_device);
        return call_down(instance.GetDisplayModePropertiesKHR, "vkGetDisplayModePropertiesKHR", physical_device,
                         display, count, out);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetDisplayModeProperties2KHR(VkPhysicalDevice physical_device,
                                                                  VkDisplayKHR display, uint32_t* count,
                                                                  VkDisplayModeProperties2KHR* out)
{
    return guarded("vkGetDisplayModeProperties2KHR", [&] {
        if (is_our_display(display)) {
            const nvdm::Product& product = open_product();
            return fill_list(count, out, 1, [&](uint32_t, VkDisplayModeProperties2KHR& m) {
                m.displayModeProperties.displayMode = our_mode();
                m.displayModeProperties.parameters = mode_parameters(product);
                set_mode_chain(m.pNext);
            });
        }
        InstanceData& instance = instance_data(physical_device);
        return call_down(instance.GetDisplayModeProperties2KHR, "vkGetDisplayModeProperties2KHR", physical_device,
                         display, count, out);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_CreateDisplayModeKHR(VkPhysicalDevice physical_device, VkDisplayKHR display,
                                                          const VkDisplayModeCreateInfoKHR* ci,
                                                          const VkAllocationCallbacks* alloc, VkDisplayModeKHR* out)
{
    return guarded("vkCreateDisplayModeKHR", [&] {
        if (is_our_display(display)) {
            // One mode (design 6.3). Asking for it again gives it; anything else fails.
            const nvdm::Product& product = open_product();
            if (is_product_mode(product, ci->parameters)) {
                *out = our_mode();
                return VK_SUCCESS;
            }
            log(Level::Warn, "vkCreateDisplayModeKHR: the %s has one mode, %ux%u at %u mHz; %ux%u at %u mHz refused",
                product.name, product.width, product.height, product.refresh_mhz,
                ci->parameters.visibleRegion.width, ci->parameters.visibleRegion.height, ci->parameters.refreshRate);
            return VK_ERROR_INITIALIZATION_FAILED;
        }
        InstanceData& instance = instance_data(physical_device);
        return call_down(instance.CreateDisplayModeKHR, "vkCreateDisplayModeKHR", physical_device, display, ci, alloc,
                         out);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetDisplayPlaneCapabilitiesKHR(VkPhysicalDevice physical_device,
                                                                    VkDisplayModeKHR mode, uint32_t plane,
                                                                    VkDisplayPlaneCapabilitiesKHR* out)
{
    return guarded("vkGetDisplayPlaneCapabilitiesKHR", [&] {
        InstanceData& instance = instance_data(physical_device);
        uint32_t down_plane = plane;
        if (const std::optional<VkDisplayPlaneCapabilitiesKHR> answer =
                our_plane_answer(instance, physical_device, mode, plane, down_plane)) {
            *out = *answer;
            return VK_SUCCESS;
        }
        return call_down(instance.GetDisplayPlaneCapabilitiesKHR, "vkGetDisplayPlaneCapabilitiesKHR", physical_device,
                         mode, down_plane, out);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetDisplayPlaneCapabilities2KHR(VkPhysicalDevice physical_device,
                                                                     const VkDisplayPlaneInfo2KHR* info,
                                                                     VkDisplayPlaneCapabilities2KHR* out)
{
    return guarded("vkGetDisplayPlaneCapabilities2KHR", [&] {
        InstanceData& instance = instance_data(physical_device);
        uint32_t down_plane = info->planeIndex;
        if (const std::optional<VkDisplayPlaneCapabilitiesKHR> answer =
                our_plane_answer(instance, physical_device, info->mode, info->planeIndex, down_plane)) {
            out->capabilities = *answer;
            return VK_SUCCESS;
        }
        VkDisplayPlaneInfo2KHR down = *info;
        down.planeIndex = down_plane;
        return call_down(instance.GetDisplayPlaneCapabilities2KHR, "vkGetDisplayPlaneCapabilities2KHR",
                         physical_device, &down, out);
    });
}

/*
 * Acquiring and releasing displays. Nothing is ever acquired from the layer's display.
 */

VKAPI_ATTR VkResult VKAPI_CALL layer_ReleaseDisplayEXT(VkPhysicalDevice physical_device, VkDisplayKHR display)
{
    return guarded("vkReleaseDisplayEXT", [&] {
        if (is_our_display(display)) {
            return VK_SUCCESS; // nothing to release
        }
        InstanceData& instance = instance_data(physical_device);
        return call_down(instance.ReleaseDisplayEXT, "vkReleaseDisplayEXT", physical_device, display);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_AcquireWinrtDisplayNV(VkPhysicalDevice physical_device, VkDisplayKHR display)
{
    return guarded("vkAcquireWinrtDisplayNV", [&] {
        if (is_our_display(display)) {
            log(Level::Warn, "vkAcquireWinrtDisplayNV: the layer's display is held through NVAPI, not through "
                             "Windows; refused");
            return VK_ERROR_INITIALIZATION_FAILED;
        }
        InstanceData& instance = instance_data(physical_device);
        return call_down(instance.AcquireWinrtDisplayNV, "vkAcquireWinrtDisplayNV", physical_device, display);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetWinrtDisplayNV(VkPhysicalDevice physical_device, uint32_t device_relative_id,
                                                       VkDisplayKHR* out)
{
    // A Windows display target id names a display the driver knows; the answer is always the driver's handle, never
    // ours. Intercepted only so that the classification of this command holds (handled_commands.cpp).
    return guarded("vkGetWinrtDisplayNV", [&] {
        InstanceData& instance = instance_data(physical_device);
        return call_down(instance.GetWinrtDisplayNV, "vkGetWinrtDisplayNV", physical_device, device_relative_id, out);
    });
}

} // namespace dvl
