// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

// The layer's intercepts outside layer_main.cpp, for its proc-address tables: device.cpp, display_intercepts.cpp,
// surface_intercepts.cpp, Task 11's swapchain.cpp, present.cpp, events.cpp and misc_intercepts.cpp, and
// present_timing_intercepts.cpp (design 6.11). Each is declared from its Vulkan PFN type, so a definition whose
// signature differs from the command's does not link.

#include <vulkan/vulkan.h>

#include <exception>
#include <new>
#include <optional>
#include <type_traits>

#include "core/display_session.hpp"
#include "layer/dispatch.hpp"
#include "layer/log.hpp"

namespace dvl {

// Instance-level commands (vkGetInstanceProcAddr only). X(name) is vkName; the function is layer_name.
#define DVL_INSTANCE_INTERCEPTS(X)                 \
    X(CreateDevice)                                \
    X(GetPhysicalDeviceDisplayPropertiesKHR)       \
    X(GetPhysicalDeviceDisplayProperties2KHR)      \
    X(GetPhysicalDeviceDisplayPlanePropertiesKHR)  \
    X(GetPhysicalDeviceDisplayPlaneProperties2KHR) \
    X(GetDisplayPlaneSupportedDisplaysKHR)         \
    X(GetDisplayModePropertiesKHR)                 \
    X(GetDisplayModeProperties2KHR)                \
    X(CreateDisplayModeKHR)                        \
    X(GetDisplayPlaneCapabilitiesKHR)              \
    X(GetDisplayPlaneCapabilities2KHR)             \
    X(ReleaseDisplayEXT)                           \
    X(AcquireWinrtDisplayNV)                       \
    X(GetWinrtDisplayNV)                           \
    X(CreateDisplayPlaneSurfaceKHR)                \
    X(DestroySurfaceKHR)                           \
    X(GetPhysicalDeviceSurfaceSupportKHR)          \
    X(GetPhysicalDeviceSurfaceCapabilitiesKHR)     \
    X(GetPhysicalDeviceSurfaceFormatsKHR)          \
    X(GetPhysicalDeviceSurfacePresentModesKHR)     \
    X(GetPhysicalDeviceSurfaceCapabilities2KHR)    \
    X(GetPhysicalDeviceSurfaceFormats2KHR)         \
    X(GetPhysicalDeviceSurfaceCapabilities2EXT)    \
    X(GetPhysicalDevicePresentRectanglesKHR)       \
    X(GetPhysicalDeviceSurfacePresentModes2EXT)

// Device-level commands (vkGetDeviceProcAddr, and vkGetInstanceProcAddr too): device.cpp, surface_intercepts.cpp,
// Task 11's swapchain.cpp, present.cpp, events.cpp and misc_intercepts.cpp, and present_timing_intercepts.cpp.
#define DVL_DEVICE_INTERCEPTS(X)                    \
    X(DestroyDevice)                                \
    X(DeviceWaitIdle)                               \
    X(GetDeviceGroupSurfacePresentModesKHR)         \
    X(GetDeviceGroupSurfacePresentModes2EXT)        \
    X(CreateSwapchainKHR)                           \
    X(DestroySwapchainKHR)                          \
    X(GetSwapchainImagesKHR)                        \
    X(GetSwapchainStatusKHR)                        \
    X(AcquireNextImageKHR)                          \
    X(AcquireNextImage2KHR)                         \
    X(QueuePresentKHR)                              \
    X(WaitForPresentKHR)                            \
    X(WaitForPresent2KHR)                           \
    X(RegisterDisplayEventEXT)                      \
    X(RegisterDeviceEventEXT)                       \
    X(DisplayPowerControlEXT)                       \
    X(GetSwapchainCounterEXT)                       \
    X(WaitForFences)                                \
    X(GetFenceStatus)                               \
    X(ResetFences)                                  \
    X(DestroyFence)                                 \
    X(SetHdrMetadataEXT)                            \
    X(CreateSharedSwapchainsKHR)                    \
    X(AcquireFullScreenExclusiveModeEXT)            \
    X(ReleaseFullScreenExclusiveModeEXT)            \
    X(ReleaseSwapchainImagesEXT)                    \
    X(ReleaseSwapchainImagesKHR)                    \
    X(GetRefreshCycleDurationGOOGLE)                \
    X(GetPastPresentationTimingGOOGLE)              \
    X(SetSwapchainPresentTimingQueueSizeEXT)        \
    X(GetSwapchainTimingPropertiesEXT)              \
    X(GetSwapchainTimeDomainPropertiesEXT)          \
    X(GetPastPresentationTimingEXT)                 \
    X(GetCalibratedTimestampsKHR)                   \
    X(GetCalibratedTimestampsEXT)                   \
    X(SetDebugUtilsObjectNameEXT)                   \
    X(SetDebugUtilsObjectTagEXT)                    \
    X(DebugMarkerSetObjectNameEXT)                  \
    X(DebugMarkerSetObjectTagEXT)                   \
    X(SetPrivateData)                               \
    X(GetPrivateData)                               \
    X(SetPrivateDataEXT)                            \
    X(GetPrivateDataEXT)                            \
    X(SetLatencySleepModeNV)                        \
    X(LatencySleepNV)                               \
    X(SetLatencyMarkerNV)                           \
    X(GetLatencyTimingsNV)                          \
    X(SetLocalDimmingAMD)                           \
    X(CreateImage)                                  \
    X(BindImageMemory2)                             \
    X(BindImageMemory2KHR)                          \
    X(GetDeviceImageMemoryRequirements)             \
    X(GetDeviceImageMemoryRequirementsKHR)          \
    X(GetDeviceImageSparseMemoryRequirements)       \
    X(GetDeviceImageSparseMemoryRequirementsKHR)    \
    X(GetDeviceImageSubresourceLayout)              \
    X(GetDeviceImageSubresourceLayoutKHR)

#define DVL_DECLARE_INTERCEPT(name) extern std::remove_pointer_t<PFN_vk##name> layer_##name;
DVL_INSTANCE_INTERCEPTS(DVL_DECLARE_INTERCEPT)
DVL_DEVICE_INTERCEPTS(DVL_DECLARE_INTERCEPT)
#undef DVL_DECLARE_INTERCEPT

// The display session the intercepts use: nvdm::process_session(), its lines routed to the layer's log at the first
// use, unless a test has put another in its place.
nvdm::DisplaySession& display_session();

// The test seam (the controller's ruling for Task 9): every later display_session() is `session` (a session on a fake
// NVAPI) instead of the process's; null puts the process's back. Only the test build of the DLL exports it
// (test_hooks.cpp), so no test reaches the real NVAPI. The caller keeps `session` alive while it is in place.
void set_display_session_for_tests(nvdm::DisplaySession* session);

// Whether our display is listed on `physical_device` (design 6.3). The first call for a physical device of the
// instance decides it and keeps the answer: not NVIDIA, no LUID, the display session failing to open for its LUID (it
// logs the line itself), or the import failing (the format queries, or the trial import on a device of the physical
// device that exists now: import_verdict()) is no. The only place, with vkCreateDisplayPlaneSurfaceKHR, where the
// layer can fail (design 6.10). May wait for the session's open (MIRADOR_DIRECT_MODE_WAIT_MS and the NVAPI calls) and
// the trial import.
bool display_listed(InstanceData& instance, VkPhysicalDevice physical_device);
// The decided answer, without deciding: false if not decided yet.
bool display_listed_now(InstanceData& instance, VkPhysicalDevice physical_device);
// The usage our surface offers on a physical device that lists the display (from the format queries of the decision,
// design 6.4); 0 on one that does not, or is not decided yet.
VkImageUsageFlags surface_usage(InstanceData& instance, VkPhysicalDevice physical_device);
// The instance's physical device that lists our display, if one does (decided ones only).
std::optional<VkPhysicalDevice> listing_physical_device(InstanceData& instance);
// The layer below's plane count on `physical_device` (0 if it cannot tell).
uint32_t driver_plane_count(InstanceData& instance, VkPhysicalDevice physical_device);

// Runs an intercept's body. No exception leaves the layer: out of memory is VK_ERROR_OUT_OF_HOST_MEMORY, anything
// else (a handle the layer does not know) VK_ERROR_UNKNOWN, each with a log line naming the command.
template <class Body>
VkResult guarded(const char* command, Body&& body) noexcept
{
    try {
        return body();
    } catch (const std::bad_alloc&) {
        log(Level::Error, "%s: out of host memory", command);
        return VK_ERROR_OUT_OF_HOST_MEMORY;
    } catch (const std::exception& e) {
        log(Level::Error, "%s: %s", command, e.what());
        return VK_ERROR_UNKNOWN;
    } catch (...) {
        log(Level::Error, "%s: unexpected error", command);
        return VK_ERROR_UNKNOWN;
    }
}

template <class Body>
void guarded_void(const char* command, Body&& body) noexcept
{
    guarded(command, [&] {
        body();
        return VK_SUCCESS;
    });
}

// The call down for a command whose next function the layer below did not give. Only a layer below that lacks a
// command it should have gets here (the loader's terminators have them all): logged, VK_ERROR_EXTENSION_NOT_PRESENT.
VkResult missing_below(const char* command) noexcept;

// Calls the layer below's `fn`, or missing_below() if it has none.
template <class Fn, class... Args>
VkResult call_down(Fn fn, const char* command, Args... args)
{
    if (fn == nullptr) {
        return missing_below(command);
    }
    return fn(args...);
}

} // namespace dvl
