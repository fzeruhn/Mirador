// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The rest of design 6.3's list: every other command that can be handed one of the layer's display, mode, surface or
// swapchain (handled_commands.cpp). For the layer's own handles each is accepted and answered here (debug names and
// tags, HDR metadata, full-screen exclusive, NVIDIA low latency, AMD local dimming, GOOGLE display timing, private
// data, released images), or refused with a log line (vkCreateSharedSwapchainsKHR, an image made or bound for the
// layer's swapchain: design 6.10's exceptions). Anything else goes down unchanged.

#include <vulkan/vulkan.h>

#include <map>
#include <memory>
#include <mutex>
#include <tuple>
#include <vector>

#include "layer/dispatch.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/surface.hpp"
#include "layer/swapchain.hpp"

namespace dvl {
namespace {

// --- Private data on the layer's objects ---------------------------------------------------------------------------

// The layer keeps it itself: the driver cannot be handed our handles. Keyed by device, slot, type and handle. Made at
// first use and never destroyed (design 6.10); a slot's entries outlive it, which costs a few bytes.
struct PrivateStore {
    std::mutex mutex;
    std::map<std::tuple<VkDevice, VkPrivateDataSlot, VkObjectType, uint64_t>, uint64_t> data;
};

PrivateStore& private_store()
{
    static PrivateStore* const s = new PrivateStore;
    return *s;
}

void set_private(VkDevice device, VkObjectType type, uint64_t handle, VkPrivateDataSlot slot, uint64_t value)
{
    PrivateStore& s = private_store();
    std::lock_guard<std::mutex> hold(s.mutex);
    s.data[std::make_tuple(device, slot, type, handle)] = value;
}

uint64_t get_private(VkDevice device, VkObjectType type, uint64_t handle, VkPrivateDataSlot slot)
{
    PrivateStore& s = private_store();
    std::lock_guard<std::mutex> hold(s.mutex);
    const auto it = s.data.find(std::make_tuple(device, slot, type, handle));
    return it == s.data.end() ? 0 : it->second; // never set: 0, as Vulkan answers
}

// --- Images made or bound for a swapchain --------------------------------------------------------------------------

// The layer's swapchain named in a pNext chain (VkImageSwapchainCreateInfoKHR, VkBindImageMemorySwapchainInfoKHR):
// the driver would take our handle for its own.
bool names_our_swapchain(const void* next)
{
    for (auto* s = static_cast<const VkBaseInStructure*>(next); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_IMAGE_SWAPCHAIN_CREATE_INFO_KHR &&
            find_swapchain(reinterpret_cast<const VkImageSwapchainCreateInfoKHR*>(s)->swapchain)) {
            return true;
        }
        if (s->sType == VK_STRUCTURE_TYPE_BIND_IMAGE_MEMORY_SWAPCHAIN_INFO_KHR &&
            find_swapchain(reinterpret_cast<const VkBindImageMemorySwapchainInfoKHR*>(s)->swapchain)) {
            return true;
        }
    }
    return false;
}

bool creates_for_our_swapchain(const VkDeviceImageMemoryRequirements* info)
{
    return info != nullptr && info->pCreateInfo != nullptr && names_our_swapchain(info->pCreateInfo->pNext);
}

void refuse_image(const char* command)
{
    // One of design 6.10's exceptions (Task 8): the images of the layer's swapchain are the imported back-buffers.
    log(Level::Error, "%s: the pNext chain names the layer's swapchain; images of it come from "
                      "vkGetSwapchainImagesKHR only. Refused",
        command);
}

VkResult bind_image_memory_2(const char* command, PFN_vkBindImageMemory2 down, VkDevice device, uint32_t count,
                             const VkBindImageMemoryInfo* infos)
{
    for (uint32_t i = 0; i < count; ++i) {
        if (names_our_swapchain(infos[i].pNext)) {
            refuse_image(command);
            return VK_ERROR_UNKNOWN;
        }
    }
    return call_down(down, command, device, count, infos);
}

void memory_requirements(const char* command, PFN_vkGetDeviceImageMemoryRequirements down, VkDevice device,
                         const VkDeviceImageMemoryRequirements* info, VkMemoryRequirements2* out)
{
    if (creates_for_our_swapchain(info)) {
        refuse_image(command);
        out->memoryRequirements = VkMemoryRequirements{};
        return;
    }
    if (down == nullptr) {
        missing_below(command);
        return;
    }
    down(device, info, out);
}

void sparse_requirements(const char* command, PFN_vkGetDeviceImageSparseMemoryRequirements down, VkDevice device,
                         const VkDeviceImageMemoryRequirements* info, uint32_t* count,
                         VkSparseImageMemoryRequirements2* out)
{
    if (creates_for_our_swapchain(info)) {
        refuse_image(command);
        *count = 0;
        return;
    }
    if (down == nullptr) {
        missing_below(command);
        *count = 0;
        return;
    }
    down(device, info, count, out);
}

void subresource_layout(const char* command, PFN_vkGetDeviceImageSubresourceLayout down, VkDevice device,
                        const VkDeviceImageSubresourceInfo* info, VkSubresourceLayout2* out)
{
    if (info != nullptr && info->pCreateInfo != nullptr && names_our_swapchain(info->pCreateInfo->pNext)) {
        refuse_image(command);
        out->subresourceLayout = VkSubresourceLayout{};
        return;
    }
    if (down == nullptr) {
        missing_below(command);
        return;
    }
    down(device, info, out);
}

} // namespace

// --- Debug names and tags: accepted for the layer's handles, not passed down ----------------------------------------

VKAPI_ATTR VkResult VKAPI_CALL layer_SetDebugUtilsObjectNameEXT(VkDevice device,
                                                                const VkDebugUtilsObjectNameInfoEXT* info)
{
    return guarded("vkSetDebugUtilsObjectNameEXT", [&] {
        if (is_our_object(info->objectType, info->objectHandle)) {
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.SetDebugUtilsObjectNameEXT, "vkSetDebugUtilsObjectNameEXT", device, info);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_SetDebugUtilsObjectTagEXT(VkDevice device, const VkDebugUtilsObjectTagInfoEXT* info)
{
    return guarded("vkSetDebugUtilsObjectTagEXT", [&] {
        if (is_our_object(info->objectType, info->objectHandle)) {
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.SetDebugUtilsObjectTagEXT, "vkSetDebugUtilsObjectTagEXT", device, info);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_DebugMarkerSetObjectNameEXT(VkDevice device,
                                                                 const VkDebugMarkerObjectNameInfoEXT* info)
{
    return guarded("vkDebugMarkerSetObjectNameEXT", [&] {
        if (is_our_debug_report_object(info->objectType, info->object)) {
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.DebugMarkerSetObjectNameEXT, "vkDebugMarkerSetObjectNameEXT", device, info);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_DebugMarkerSetObjectTagEXT(VkDevice device,
                                                                const VkDebugMarkerObjectTagInfoEXT* info)
{
    return guarded("vkDebugMarkerSetObjectTagEXT", [&] {
        if (is_our_debug_report_object(info->objectType, info->object)) {
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.DebugMarkerSetObjectTagEXT, "vkDebugMarkerSetObjectTagEXT", device, info);
    });
}

// --- Private data ----------------------------------------------------------------------------------------------------

VKAPI_ATTR VkResult VKAPI_CALL layer_SetPrivateData(VkDevice device, VkObjectType type, uint64_t handle,
                                                    VkPrivateDataSlot slot, uint64_t value)
{
    return guarded("vkSetPrivateData", [&] {
        if (is_our_object(type, handle)) {
            set_private(device, type, handle, slot, value);
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.SetPrivateData, "vkSetPrivateData", device, type, handle, slot, value);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_SetPrivateDataEXT(VkDevice device, VkObjectType type, uint64_t handle,
                                                       VkPrivateDataSlot slot, uint64_t value)
{
    return guarded("vkSetPrivateDataEXT", [&] {
        if (is_our_object(type, handle)) {
            set_private(device, type, handle, slot, value);
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.SetPrivateDataEXT, "vkSetPrivateDataEXT", device, type, handle, slot, value);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetPrivateData(VkDevice device, VkObjectType type, uint64_t handle,
                                                VkPrivateDataSlot slot, uint64_t* value)
{
    guarded_void("vkGetPrivateData", [&] {
        if (is_our_object(type, handle)) {
            *value = get_private(device, type, handle, slot);
            return;
        }
        DeviceData& d = device_data(device);
        if (d.GetPrivateData == nullptr) {
            missing_below("vkGetPrivateData");
            *value = 0;
            return;
        }
        d.GetPrivateData(device, type, handle, slot, value);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetPrivateDataEXT(VkDevice device, VkObjectType type, uint64_t handle,
                                                   VkPrivateDataSlot slot, uint64_t* value)
{
    guarded_void("vkGetPrivateDataEXT", [&] {
        if (is_our_object(type, handle)) {
            *value = get_private(device, type, handle, slot);
            return;
        }
        DeviceData& d = device_data(device);
        if (d.GetPrivateDataEXT == nullptr) {
            missing_below("vkGetPrivateDataEXT");
            *value = 0;
            return;
        }
        d.GetPrivateDataEXT(device, type, handle, slot, value);
    });
}

// --- Swapchain extras --------------------------------------------------------------------------------------------

VKAPI_ATTR void VKAPI_CALL layer_SetHdrMetadataEXT(VkDevice device, uint32_t count, const VkSwapchainKHR* swapchains,
                                                   const VkHdrMetadataEXT* metadata)
{
    guarded_void("vkSetHdrMetadataEXT", [&] {
        // The headset takes no HDR metadata: a no-op for the layer's swapchain; the others' go down, with theirs.
        std::vector<VkSwapchainKHR> theirs;
        std::vector<VkHdrMetadataEXT> their_metadata;
        for (uint32_t i = 0; i < count; ++i) {
            if (!find_swapchain(swapchains[i])) {
                theirs.push_back(swapchains[i]);
                their_metadata.push_back(metadata[i]);
            }
        }
        if (theirs.empty()) {
            return;
        }
        DeviceData& d = device_data(device);
        if (d.SetHdrMetadataEXT == nullptr) {
            missing_below("vkSetHdrMetadataEXT");
            return;
        }
        d.SetHdrMetadataEXT(device, static_cast<uint32_t>(theirs.size()), theirs.data(), their_metadata.data());
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_CreateSharedSwapchainsKHR(VkDevice device, uint32_t count,
                                                               const VkSwapchainCreateInfoKHR* infos,
                                                               const VkAllocationCallbacks* alloc,
                                                               VkSwapchainKHR* swapchains)
{
    return guarded("vkCreateSharedSwapchainsKHR", [&] {
        for (uint32_t i = 0; i < count; ++i) {
            if (find_surface(infos[i].surface) != nullptr || find_swapchain(infos[i].oldSwapchain)) {
                // Design 6.3 and 6.10: refused. Monado never calls it.
                log(Level::Error, "vkCreateSharedSwapchainsKHR: create info %u names the layer's surface or "
                                  "swapchain; shared swapchains are not supported there. Refused",
                    i);
                return VK_ERROR_INITIALIZATION_FAILED;
            }
        }
        DeviceData& d = device_data(device);
        return call_down(d.CreateSharedSwapchainsKHR, "vkCreateSharedSwapchainsKHR", device, count, infos, alloc,
                         swapchains);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_AcquireFullScreenExclusiveModeEXT(VkDevice device, VkSwapchainKHR swapchain)
{
    return guarded("vkAcquireFullScreenExclusiveModeEXT", [&] {
        if (find_swapchain(swapchain)) {
            return VK_SUCCESS; // the headset is the layer's alone already
        }
        DeviceData& d = device_data(device);
        return call_down(d.AcquireFullScreenExclusiveModeEXT, "vkAcquireFullScreenExclusiveModeEXT", device,
                         swapchain);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_ReleaseFullScreenExclusiveModeEXT(VkDevice device, VkSwapchainKHR swapchain)
{
    return guarded("vkReleaseFullScreenExclusiveModeEXT", [&] {
        if (find_swapchain(swapchain)) {
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.ReleaseFullScreenExclusiveModeEXT, "vkReleaseFullScreenExclusiveModeEXT", device,
                         swapchain);
    });
}

namespace {

// vkReleaseSwapchainImagesEXT and its KHR name (VK_KHR_swapchain_maintenance1): acquired images go back unpresented,
// their frames end and their back-buffers are free again.
void release_images(SwapchainRecord& record, const VkReleaseSwapchainImagesInfoKHR& info)
{
    Presenter& p = presenter();
    Locked lk(p);
    for (uint32_t i = 0; i < info.imageIndexCount; ++i) {
        const uint32_t index = info.pImageIndices[i];
        if (index < kImageCount && record.acquired[index] != 0) {
            p.model.release(record.acquired[index]);
            record.acquired[index] = 0;
        }
    }
    p.changed.notify_all();
}

} // namespace

VKAPI_ATTR VkResult VKAPI_CALL layer_ReleaseSwapchainImagesEXT(VkDevice device,
                                                               const VkReleaseSwapchainImagesInfoEXT* info)
{
    return guarded("vkReleaseSwapchainImagesEXT", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(info->swapchain)) {
            release_images(*record, *info);
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.ReleaseSwapchainImagesEXT, "vkReleaseSwapchainImagesEXT", device, info);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_ReleaseSwapchainImagesKHR(VkDevice device,
                                                               const VkReleaseSwapchainImagesInfoKHR* info)
{
    return guarded("vkReleaseSwapchainImagesKHR", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(info->swapchain)) {
            release_images(*record, *info);
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.ReleaseSwapchainImagesKHR, "vkReleaseSwapchainImagesKHR", device, info);
    });
}

// --- GOOGLE display timing -------------------------------------------------------------------------------------------

VKAPI_ATTR VkResult VKAPI_CALL layer_GetRefreshCycleDurationGOOGLE(VkDevice device, VkSwapchainKHR swapchain,
                                                                   VkRefreshCycleDurationGOOGLE* out)
{
    return guarded("vkGetRefreshCycleDurationGOOGLE", [&] {
        if (find_swapchain(swapchain)) {
            out->refreshDuration = static_cast<uint64_t>(presenter().refresh_ns); // 1/90 s
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.GetRefreshCycleDurationGOOGLE, "vkGetRefreshCycleDurationGOOGLE", device, swapchain, out);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetPastPresentationTimingGOOGLE(VkDevice device, VkSwapchainKHR swapchain,
                                                                     uint32_t* count,
                                                                     VkPastPresentationTimingGOOGLE* timings)
{
    return guarded("vkGetPastPresentationTimingGOOGLE", [&] {
        if (find_swapchain(swapchain)) {
            *count = 0; // design 6.3: no timings
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.GetPastPresentationTimingGOOGLE, "vkGetPastPresentationTimingGOOGLE", device, swapchain,
                         count, timings);
    });
}

// --- NVIDIA low latency and AMD local dimming: accepted and ignored on the layer's swapchain -----------------------

VKAPI_ATTR VkResult VKAPI_CALL layer_SetLatencySleepModeNV(VkDevice device, VkSwapchainKHR swapchain,
                                                           const VkLatencySleepModeInfoNV* info)
{
    return guarded("vkSetLatencySleepModeNV", [&] {
        if (find_swapchain(swapchain)) {
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.SetLatencySleepModeNV, "vkSetLatencySleepModeNV", device, swapchain, info);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_LatencySleepNV(VkDevice device, VkSwapchainKHR swapchain,
                                                    const VkLatencySleepInfoNV* info)
{
    return guarded("vkLatencySleepNV", [&] {
        DeviceData& d = device_data(device);
        if (find_swapchain(swapchain)) {
            // No sleep (pacing is acquire's and the display thread's), but the application waits on its semaphore
            // reaching the value: signalled from the host at once with vkSignalSemaphoreKHR (VK_KHR_timeline_semaphore;
            // VK_NV_low_latency2 needs timeline semaphores). Never the core 1.2 vkSignalSemaphore: the layer calls
            // only 1.0 core and extension commands (Monado's instance is API 1.0).
            if (d.SignalSemaphoreKHR != nullptr) {
                VkSemaphoreSignalInfo signal{VK_STRUCTURE_TYPE_SEMAPHORE_SIGNAL_INFO};
                signal.semaphore = info->signalSemaphore;
                signal.value = info->value;
                return d.SignalSemaphoreKHR(device, &signal);
            }
            if (!d.latency_signal_missing_logged.exchange(true)) {
                log(Level::Warn, "vkLatencySleepNV: device %p has no vkSignalSemaphoreKHR (VK_KHR_timeline_semaphore "
                                 "not enabled), so the sleep semaphore is not signalled",
                    static_cast<void*>(device));
            }
            return VK_SUCCESS;
        }
        return call_down(d.LatencySleepNV, "vkLatencySleepNV", device, swapchain, info);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_SetLatencyMarkerNV(VkDevice device, VkSwapchainKHR swapchain,
                                                    const VkSetLatencyMarkerInfoNV* info)
{
    guarded_void("vkSetLatencyMarkerNV", [&] {
        if (find_swapchain(swapchain)) {
            return;
        }
        DeviceData& d = device_data(device);
        if (d.SetLatencyMarkerNV == nullptr) {
            missing_below("vkSetLatencyMarkerNV");
            return;
        }
        d.SetLatencyMarkerNV(device, swapchain, info);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetLatencyTimingsNV(VkDevice device, VkSwapchainKHR swapchain,
                                                     VkGetLatencyMarkerInfoNV* info)
{
    guarded_void("vkGetLatencyTimingsNV", [&] {
        if (find_swapchain(swapchain)) {
            info->timingCount = 0; // no timings
            return;
        }
        DeviceData& d = device_data(device);
        if (d.GetLatencyTimingsNV == nullptr) {
            missing_below("vkGetLatencyTimingsNV");
            return;
        }
        d.GetLatencyTimingsNV(device, swapchain, info);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_SetLocalDimmingAMD(VkDevice device, VkSwapchainKHR swapchain, VkBool32 enable)
{
    guarded_void("vkSetLocalDimmingAMD", [&] {
        if (find_swapchain(swapchain)) {
            return;
        }
        DeviceData& d = device_data(device);
        if (d.SetLocalDimmingAMD == nullptr) {
            missing_below("vkSetLocalDimmingAMD");
            return;
        }
        d.SetLocalDimmingAMD(device, swapchain, enable);
    });
}

// --- Images whose pNext names a swapchain ----------------------------------------------------------------------------

VKAPI_ATTR VkResult VKAPI_CALL layer_CreateImage(VkDevice device, const VkImageCreateInfo* ci,
                                                 const VkAllocationCallbacks* alloc, VkImage* image)
{
    return guarded("vkCreateImage", [&] {
        if (names_our_swapchain(ci->pNext)) {
            refuse_image("vkCreateImage");
            return VK_ERROR_UNKNOWN;
        }
        DeviceData& d = device_data(device);
        return call_down(d.CreateImage, "vkCreateImage", device, ci, alloc, image);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_BindImageMemory2(VkDevice device, uint32_t count,
                                                      const VkBindImageMemoryInfo* infos)
{
    return guarded("vkBindImageMemory2", [&] {
        return bind_image_memory_2("vkBindImageMemory2", device_data(device).BindImageMemory2, device, count, infos);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_BindImageMemory2KHR(VkDevice device, uint32_t count,
                                                         const VkBindImageMemoryInfo* infos)
{
    return guarded("vkBindImageMemory2KHR", [&] {
        return bind_image_memory_2("vkBindImageMemory2KHR", device_data(device).BindImageMemory2KHR, device, count,
                                   infos);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetDeviceImageMemoryRequirements(VkDevice device,
                                                                  const VkDeviceImageMemoryRequirements* info,
                                                                  VkMemoryRequirements2* out)
{
    guarded_void("vkGetDeviceImageMemoryRequirements", [&] {
        memory_requirements("vkGetDeviceImageMemoryRequirements", device_data(device).GetDeviceImageMemoryRequirements,
                            device, info, out);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetDeviceImageMemoryRequirementsKHR(VkDevice device,
                                                                     const VkDeviceImageMemoryRequirements* info,
                                                                     VkMemoryRequirements2* out)
{
    guarded_void("vkGetDeviceImageMemoryRequirementsKHR", [&] {
        memory_requirements("vkGetDeviceImageMemoryRequirementsKHR",
                            device_data(device).GetDeviceImageMemoryRequirementsKHR, device, info, out);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetDeviceImageSparseMemoryRequirements(VkDevice device,
                                                                        const VkDeviceImageMemoryRequirements* info,
                                                                        uint32_t* count,
                                                                        VkSparseImageMemoryRequirements2* out)
{
    guarded_void("vkGetDeviceImageSparseMemoryRequirements", [&] {
        sparse_requirements("vkGetDeviceImageSparseMemoryRequirements",
                            device_data(device).GetDeviceImageSparseMemoryRequirements, device, info, count, out);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetDeviceImageSparseMemoryRequirementsKHR(
    VkDevice device, const VkDeviceImageMemoryRequirements* info, uint32_t* count,
    VkSparseImageMemoryRequirements2* out)
{
    guarded_void("vkGetDeviceImageSparseMemoryRequirementsKHR", [&] {
        sparse_requirements("vkGetDeviceImageSparseMemoryRequirementsKHR",
                            device_data(device).GetDeviceImageSparseMemoryRequirementsKHR, device, info, count, out);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetDeviceImageSubresourceLayout(VkDevice device,
                                                                 const VkDeviceImageSubresourceInfo* info,
                                                                 VkSubresourceLayout2* out)
{
    guarded_void("vkGetDeviceImageSubresourceLayout", [&] {
        subresource_layout("vkGetDeviceImageSubresourceLayout", device_data(device).GetDeviceImageSubresourceLayout,
                           device, info, out);
    });
}

VKAPI_ATTR void VKAPI_CALL layer_GetDeviceImageSubresourceLayoutKHR(VkDevice device,
                                                                    const VkDeviceImageSubresourceInfo* info,
                                                                    VkSubresourceLayout2* out)
{
    guarded_void("vkGetDeviceImageSubresourceLayoutKHR", [&] {
        subresource_layout("vkGetDeviceImageSubresourceLayoutKHR",
                           device_data(device).GetDeviceImageSubresourceLayoutKHR, device, info, out);
    });
}

} // namespace dvl
