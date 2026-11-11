// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#ifndef VK_NO_PROTOTYPES
#error "The layer calls Vulkan only through function pointers: define VK_NO_PROTOTYPES (CMake does for dvl_pure)."
#endif

#include <vulkan/vulkan.h>

#include <array>
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <unordered_map>
#include <vector>

#include "core/fence_pool.hpp"

namespace dvl {

// The functions the layer calls on the layer below it (or the driver), by instance. X(name) is vkName; the member is
// called `name`. Only Vulkan 1.0 core plus extension commands (design 6.2: Monado's vk_display instance is API 1.0).
// A command the layer below does not know (its extension is not enabled) leaves the member null: callers that use an
// extension command check it first. Tasks 9-11 add what they call.
#define DVL_INSTANCE_FUNCTIONS(X)                    \
    X(DestroyInstance)                               \
    X(EnumeratePhysicalDevices)                      \
    X(EnumerateDeviceExtensionProperties)            \
    X(GetPhysicalDeviceProperties)                   \
    X(GetPhysicalDeviceMemoryProperties)             \
    X(GetPhysicalDeviceQueueFamilyProperties)        \
    X(GetPhysicalDeviceProperties2KHR)               \
    X(GetPhysicalDeviceImageFormatProperties2KHR)    \
    X(GetPhysicalDeviceDisplayPropertiesKHR)         \
    X(GetPhysicalDeviceDisplayPlanePropertiesKHR)    \
    X(GetDisplayPlaneSupportedDisplaysKHR)           \
    X(GetDisplayModePropertiesKHR)                   \
    X(CreateDisplayModeKHR)                          \
    X(GetDisplayPlaneCapabilitiesKHR)                \
    X(CreateDisplayPlaneSurfaceKHR)                  \
    X(GetPhysicalDeviceDisplayProperties2KHR)        \
    X(GetPhysicalDeviceDisplayPlaneProperties2KHR)   \
    X(GetDisplayModeProperties2KHR)                  \
    X(GetDisplayPlaneCapabilities2KHR)                \
    X(DestroySurfaceKHR)                             \
    X(GetPhysicalDeviceSurfaceSupportKHR)            \
    X(GetPhysicalDeviceSurfaceCapabilitiesKHR)       \
    X(GetPhysicalDeviceSurfaceFormatsKHR)            \
    X(GetPhysicalDeviceSurfacePresentModesKHR)       \
    X(GetPhysicalDeviceSurfaceCapabilities2KHR)      \
    X(GetPhysicalDeviceSurfaceFormats2KHR)           \
    X(GetPhysicalDeviceSurfaceCapabilities2EXT)      \
    X(GetPhysicalDevicePresentRectanglesKHR)         \
    X(GetPhysicalDeviceSurfacePresentModes2EXT)      \
    X(ReleaseDisplayEXT)                             \
    X(AcquireWinrtDisplayNV)                         \
    X(GetWinrtDisplayNV)

// The same for devices.
#define DVL_DEVICE_FUNCTIONS(X)                     \
    X(DestroyDevice)                                \
    X(GetDeviceQueue)                               \
    X(GetDeviceGroupSurfacePresentModesKHR)         \
    X(GetDeviceGroupSurfacePresentModes2EXT)        \
    X(QueueSubmit)                                  \
    X(DeviceWaitIdle)                               \
    X(CreateImage)                                  \
    X(DestroyImage)                                 \
    X(GetImageMemoryRequirements)                   \
    X(AllocateMemory)                               \
    X(FreeMemory)                                   \
    X(BindImageMemory)                              \
    X(GetMemoryWin32HandlePropertiesKHR)            \
    X(CreateFence)                                  \
    X(DestroyFence)                                 \
    X(WaitForFences)                                \
    X(ResetFences)                                  \
    X(GetFenceStatus)                               \
    X(CreateSwapchainKHR)                           \
    X(DestroySwapchainKHR)                          \
    X(GetSwapchainImagesKHR)                        \
    X(AcquireNextImageKHR)                          \
    X(AcquireNextImage2KHR)                         \
    X(QueuePresentKHR)                              \
    X(WaitForPresentKHR)                            \
    X(WaitForPresent2KHR)                           \
    X(GetSwapchainStatusKHR)                        \
    X(QueueWaitIdle)                                \
    X(GetSwapchainCounterEXT)                       \
    X(SetDebugUtilsObjectNameEXT)                   \
    X(SetDebugUtilsObjectTagEXT)                    \
    X(DebugMarkerSetObjectNameEXT)                  \
    X(DebugMarkerSetObjectTagEXT)                   \
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
    X(SetPrivateData)                               \
    X(GetPrivateData)                               \
    X(SetPrivateDataEXT)                            \
    X(GetPrivateDataEXT)                            \
    X(SetLatencySleepModeNV)                        \
    X(LatencySleepNV)                               \
    X(SetLatencyMarkerNV)                           \
    X(GetLatencyTimingsNV)                          \
    X(SignalSemaphoreKHR)                           \
    X(SetLocalDimmingAMD)                           \
    X(BindImageMemory2)                             \
    X(BindImageMemory2KHR)                          \
    X(GetDeviceImageMemoryRequirements)             \
    X(GetDeviceImageMemoryRequirementsKHR)          \
    X(GetDeviceImageSparseMemoryRequirements)       \
    X(GetDeviceImageSparseMemoryRequirementsKHR)    \
    X(GetDeviceImageSubresourceLayout)              \
    X(GetDeviceImageSubresourceLayoutKHR)

// The loader's dispatch key of a dispatchable handle: the first word of the object, which points at a dispatch table.
// An instance and its physical devices share one; a device, its queues and its command buffers share another.
inline void* dispatch_key(void* dispatchable)
{
    return *static_cast<void**>(dispatchable);
}

// What the layer keeps per VkInstance.
class InstanceData {
public:
    InstanceData() = default;
    InstanceData(const InstanceData&) = delete;
    InstanceData& operator=(const InstanceData&) = delete;

    VkInstance handle = VK_NULL_HANDLE;
    PFN_vkGetInstanceProcAddr gipa = nullptr; // the next layer's

#define X(name) PFN_vk##name name = nullptr;
    DVL_INSTANCE_FUNCTIONS(X)
#undef X

    // The instance's physical devices as the layer below lists them (the handles our later calls get). Asked of the
    // layer below at the first call, not at vkCreateInstance, and kept; empty if it cannot be done.
    std::vector<VkPhysicalDevice> physical_devices();

    // Whether the layer's display is listed on a physical device (design 6.3), decided at the first display query on
    // it and kept for the instance's life, so that every later query in this instance gives the same answer
    // (display_intercepts.cpp). A physical device that is not in the map has not been decided yet. A later instance
    // (Monado's retry) decides again. display_mutex is held while deciding, which may open the display session and
    // run the trial import. surface_usage_on: the usage our surface offers on a physical device that lists the display
    // (design 6.4: from the trial import's format queries), set with the decision.
    std::mutex display_mutex;
    std::unordered_map<VkPhysicalDevice, bool> display_listed_on;
    std::unordered_map<VkPhysicalDevice, VkImageUsageFlags> surface_usage_on;

private:
    std::mutex physical_mutex_;
    std::vector<VkPhysicalDevice> physical_;
    bool physical_listed_ = false;
};

// What the layer keeps per VkDevice.
class DeviceData {
public:
    DeviceData() = default;
    DeviceData(const DeviceData&) = delete;
    DeviceData& operator=(const DeviceData&) = delete;

    VkDevice handle = VK_NULL_HANDLE;
    VkPhysicalDevice physical_device = VK_NULL_HANDLE;
    InstanceData* instance = nullptr; // outlives its devices (Vulkan requires devices destroyed first)
    PFN_vkGetDeviceProcAddr gdpa = nullptr; // the next layer's

#define X(name) PFN_vk##name name = nullptr;
    DVL_DEVICE_FUNCTIONS(X)
#undef X

    // The private queue that signals acquire (design 6.2), set up at vkCreateDevice (device.cpp) on an NVIDIA device.
    // VK_NULL_HANDLE on any other device, or when no queue family had room. queue_mutex guards it and is held around
    // vkDeviceWaitIdle and vkDestroyDevice.
    VkQueue private_queue = VK_NULL_HANDLE;
    uint32_t private_family = 0;
    uint32_t private_index = 0;
    std::mutex queue_mutex;

    // The application asked for VK_EXT_display_control (the layer's, so it is not passed down). Without it Monado runs
    // no vblank thread (design 8's first degradation warning).
    bool display_control = false;

    // vkLatencySleepNV on the layer's swapchain found no vkSignalSemaphoreKHR on this device, and said so (once).
    std::atomic<bool> latency_signal_missing_logged{false};

    // The back-buffer import (import.cpp). missing_import_extensions: the import extensions (kImportExtensions) the
    // device was made without because the driver lacks them, set at vkCreateDevice; the import cannot run on such a
    // device. trial_import_result: the device's trial import (design 6.3), run once: at the display query if the
    // device existed then, else at its first vkCreateSwapchainKHR; nullopt until it has run. import_mutex guards it
    // and is held for the whole trial. memory_fallback_logged: the memory-type fallback (design 6.4) was logged for
    // this device.
    std::vector<const char*> missing_import_extensions;
    std::mutex import_mutex;
    std::optional<bool> trial_import_result;
    std::atomic<bool> memory_fallback_logged{false};

    // The frame fences (design 6.5): the pool's bookkeeping, guarded by the display-session lock as FencePool requires,
    // and the VkFence of each slot (VK_NULL_HANDLE until the present that first takes the slot makes it). A slot's
    // VkFence is written only by the present that holds the slot taken, and at vkDestroyDevice; the display thread
    // reads the slots of queued frames without the lock, so the array never moves. The pool asks vkGetFenceStatus
    // (below the layer) whether a left-over fence has signalled.
    std::array<VkFence, nvdm::kFencePoolCap> fences{};
    nvdm::FencePool fence_pool{nvdm::kFencePoolCap, [this](nvdm::FenceSlot slot) { return fence_signalled(slot); }};
    bool fence_signalled(nvdm::FenceSlot slot) const
    {
        return slot < fences.size() && fences[slot] != VK_NULL_HANDLE && GetFenceStatus != nullptr &&
               GetFenceStatus(handle, fences[slot]) == VK_SUCCESS;
    }
};

// Loads every function of the list through the next layer's GIPA / GDPA.
std::unique_ptr<InstanceData> make_instance_data(VkInstance instance, PFN_vkGetInstanceProcAddr next_gipa);
std::unique_ptr<DeviceData> make_device_data(VkDevice device, VkPhysicalDevice physical_device, InstanceData* instance,
                                             PFN_vkGetDeviceProcAddr next_gdpa);

// The registry, keyed by dispatch_key(). The data is found from any handle with the key: an instance or a physical
// device for instance data, a device or a queue for device data. add_*() keeps the data under the key of its own
// handle and returns it; remove_*() gives it back (null if there was none). find_*() give null for an unknown handle;
// instance_data() and device_data() throw std::out_of_range, and are for handles the loader passed down our chain,
// which are always known.
InstanceData& add_instance(std::unique_ptr<InstanceData> data);
std::unique_ptr<InstanceData> remove_instance(void* dispatchable);
InstanceData* find_instance_data(void* dispatchable);
InstanceData& instance_data(void* dispatchable);

DeviceData& add_device(std::unique_ptr<DeviceData> data);
std::unique_ptr<DeviceData> remove_device(void* dispatchable);
DeviceData* find_device_data(void* dispatchable);
DeviceData& device_data(void* dispatchable);

// Calls `fn` on every device of `instance` made on `physical_device`, in no set order, and returns how many there
// were. The registry stays locked (shared) meanwhile, so no device can be removed (or added) under it: `fn` must not
// use the registry itself (add_*, remove_*, find_*, *_data).
size_t for_each_device(const InstanceData* instance, VkPhysicalDevice physical_device,
                       const std::function<void(DeviceData&)>& fn);

// For vkDestroyInstance / vkDestroyDevice on a handle the registry does not have: the layer below's destroy function,
// if every instance (device) added so far had the same one, else null. That is the case with one layer or driver
// below, so the call can still go down instead of leaking the object; with two different ones there is no telling
// which owns the handle, and the layer must not guess.
PFN_vkDestroyInstance fallback_destroy_instance();
PFN_vkDestroyDevice fallback_destroy_device();

} // namespace dvl
