// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// vkCreateDevice, vkDestroyDevice and vkDeviceWaitIdle (design 6.2). An NVIDIA device gets the private queue that
// signals acquire and the extensions the back-buffer import needs, before NVAPI is ever loaded: Monado creates its
// device before it asks for displays. vkDestroyDevice tears the device's part of the presentation down first, in
// design 6.10's order (swapchain.hpp: before_device_destroyed, device_destroyed).

#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <vector>

#include "layer/device_setup.hpp"
#include "layer/dispatch.hpp"
#include "layer/handled_commands.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/swapchain.hpp"

namespace dvl {
namespace {

// The loader's structs in a device create info's pNext chain: the link to the next layer (VK_LAYER_LINK_INFO) and the
// callback that makes objects the layer creates dispatchable (VK_LOADER_DATA_CALLBACK).
VkLayerDeviceCreateInfo* find_loader_struct(const VkDeviceCreateInfo* ci, VkLayerFunction function)
{
    for (auto* s = static_cast<const VkBaseInStructure*>(ci->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO) {
            auto* info = const_cast<VkLayerDeviceCreateInfo*>(reinterpret_cast<const VkLayerDeviceCreateInfo*>(s));
            if (info->function == function) {
                return info;
            }
        }
    }
    return nullptr;
}

std::vector<VkQueueFamilyProperties> queue_families(InstanceData& instance, VkPhysicalDevice physical_device)
{
    std::vector<VkQueueFamilyProperties> families;
    if (instance.GetPhysicalDeviceQueueFamilyProperties != nullptr) {
        uint32_t n = 0;
        instance.GetPhysicalDeviceQueueFamilyProperties(physical_device, &n, nullptr);
        families.resize(n);
        instance.GetPhysicalDeviceQueueFamilyProperties(physical_device, &n, families.data());
        families.resize(n);
    }
    return families;
}

std::vector<std::string> device_extensions(InstanceData& instance, VkPhysicalDevice physical_device)
{
    std::vector<std::string> names;
    if (instance.EnumerateDeviceExtensionProperties != nullptr) {
        uint32_t n = 0;
        if (instance.EnumerateDeviceExtensionProperties(physical_device, nullptr, &n, nullptr) == VK_SUCCESS) {
            std::vector<VkExtensionProperties> list(n);
            const VkResult r = instance.EnumerateDeviceExtensionProperties(physical_device, nullptr, &n, list.data());
            if (r == VK_SUCCESS || r == VK_INCOMPLETE) {
                for (uint32_t i = 0; i < n; i++) {
                    names.emplace_back(list[i].extensionName);
                }
            }
        }
    }
    return names;
}

std::string joined(const std::vector<const char*>& names)
{
    std::string text;
    for (const char* name : names) {
        text += text.empty() ? "" : ", ";
        text += name;
    }
    return text;
}

// The driver's device, until the layer has registered it: destroyed again, and the application's handle cleared, if
// anything after the call down fails or throws, so a failed vkCreateDevice leaves nothing behind (design 6.2).
class CreatedDevice {
public:
    CreatedDevice(VkDevice* out, PFN_vkDestroyDevice destroy, const VkAllocationCallbacks* alloc)
        : out_(out), device_(*out), destroy_(destroy), alloc_(alloc)
    {
    }
    ~CreatedDevice()
    {
        if (!kept_) {
            if (destroy_ != nullptr) {
                destroy_(device_, alloc_);
            }
            *out_ = VK_NULL_HANDLE;
        }
    }
    CreatedDevice(const CreatedDevice&) = delete;
    CreatedDevice& operator=(const CreatedDevice&) = delete;

    void keep() { kept_ = true; }

private:
    VkDevice* out_;
    VkDevice device_;
    PFN_vkDestroyDevice destroy_;
    const VkAllocationCallbacks* alloc_;
    bool kept_ = false;
};

VkResult create_device(VkPhysicalDevice physical_device, const VkDeviceCreateInfo* ci,
                       const VkAllocationCallbacks* alloc, VkDevice* out)
{
    InstanceData* instance = find_instance_data(physical_device);
    VkLayerDeviceCreateInfo* link = find_loader_struct(ci, VK_LAYER_LINK_INFO);
    if (instance == nullptr || link == nullptr || link->u.pLayerInfo == nullptr) {
        log(Level::Error, "vkCreateDevice: no layer link info, or an instance the layer does not know");
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    const PFN_vkGetInstanceProcAddr next_gipa = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
    const PFN_vkGetDeviceProcAddr next_gdpa = link->u.pLayerInfo->pfnNextGetDeviceProcAddr;
    const auto next_create = reinterpret_cast<PFN_vkCreateDevice>(next_gipa(instance->handle, "vkCreateDevice"));
    if (next_create == nullptr) {
        log(Level::Error, "vkCreateDevice: the next layer has no vkCreateDevice");
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    link->u.pLayerInfo = link->u.pLayerInfo->pNext; // the layer below finds its own link

    // Design 6.2: every NVIDIA device is prepared, before the display is known.
    VkPhysicalDeviceProperties properties{};
    if (instance->GetPhysicalDeviceProperties != nullptr) {
        instance->GetPhysicalDeviceProperties(physical_device, &properties);
    }
    std::optional<DevicePlan> plan;
    if (properties.vendorID == kNvidiaVendorId) {
        plan = plan_device(*ci, queue_families(*instance, physical_device), device_extensions(*instance, physical_device),
                           properties.vendorID);
    }
    VkDeviceCreateInfo down = *ci;
    if (plan) {
        down.queueCreateInfoCount = static_cast<uint32_t>(plan->queues.size());
        down.pQueueCreateInfos = plan->queues.data();
        down.enabledExtensionCount = static_cast<uint32_t>(plan->extensions.size());
        down.ppEnabledExtensionNames = plan->extensions.data();
    }

    const VkResult result = next_create(physical_device, &down, alloc, out);
    if (result != VK_SUCCESS) {
        // The plan was all the layer had made for it, and it is gone with this call: nothing else to undo. Monado
        // retries with a lower priority after VK_ERROR_NOT_PERMITTED.
        log(Level::Info, "vkCreateDevice: the driver returned %d", static_cast<int>(result));
        return result;
    }
    const VkDevice device = *out;
    CreatedDevice created(out, reinterpret_cast<PFN_vkDestroyDevice>(next_gdpa(device, "vkDestroyDevice")), alloc);

    std::unique_ptr<DeviceData> data = make_device_data(device, physical_device, instance, next_gdpa);
    if (plan && plan->private_family != VK_QUEUE_FAMILY_IGNORED) {
        // The private queue (design 6.2): vkGetDeviceQueue (Vulkan 1.0), then the loader's callback, without which a
        // layer below would crash on the queue.
        const VkLayerDeviceCreateInfo* callback = find_loader_struct(ci, VK_LOADER_DATA_CALLBACK);
        if (callback == nullptr || callback->u.pfnSetDeviceLoaderData == nullptr || data->GetDeviceQueue == nullptr) {
            log(Level::Error, "vkCreateDevice: the loader gave no vkSetDeviceLoaderData callback (or the driver no "
                              "vkGetDeviceQueue), so the private queue cannot be set up");
            return VK_ERROR_INITIALIZATION_FAILED;
        }
        VkQueue queue = VK_NULL_HANDLE;
        data->GetDeviceQueue(device, plan->private_family, plan->private_index, &queue);
        if (queue == VK_NULL_HANDLE) {
            log(Level::Error, "vkCreateDevice: the driver gave no queue %u of family %u", plan->private_index,
                plan->private_family);
            return VK_ERROR_INITIALIZATION_FAILED;
        }
        const VkResult set = callback->u.pfnSetDeviceLoaderData(device, queue);
        if (set != VK_SUCCESS) {
            log(Level::Error, "vkCreateDevice: vkSetDeviceLoaderData on the private queue returned %d",
                static_cast<int>(set));
            return set;
        }
        data->private_queue = queue;
        data->private_family = plan->private_family;
        data->private_index = plan->private_index;
    }
    data->display_control = plan ? plan->display_control : false;
    if (plan) {
        data->missing_import_extensions = plan->missing_extensions;
    }
    add_device(std::move(data));
    created.keep();

    check_command_coverage(ci->ppEnabledExtensionNames, ci->enabledExtensionCount, "vkCreateDevice");
    if (!plan) {
        log(Level::Debug, "vkCreateDevice: device %p on %s (vendor 0x%04x), not NVIDIA: passed down untouched",
            static_cast<void*>(device), properties.deviceName, properties.vendorID);
        return VK_SUCCESS;
    }
    if (plan->private_family == VK_QUEUE_FAMILY_IGNORED) {
        log(Level::Warn, "vkCreateDevice: device %p on %s: no queue family has room for the layer's private queue; "
                         "this device cannot present to the headset", static_cast<void*>(device), properties.deviceName);
    }
    if (!plan->missing_extensions.empty()) {
        log(Level::Warn, "vkCreateDevice: device %p on %s does not offer %s, which the back-buffer import needs",
            static_cast<void*>(device), properties.deviceName, joined(plan->missing_extensions).c_str());
    }
    log(Level::Info,
        "vkCreateDevice: device %p on %s: private queue family %u index %u (%s), %u extensions down, "
        "VK_EXT_display_control %s",
        static_cast<void*>(device), properties.deviceName, plan->private_family, plan->private_index,
        plan->new_info ? "a create info of its own" : "one more in the first create info",
        static_cast<unsigned>(plan->extensions.size()),
        plan->display_control ? "asked for (the layer's, not passed down)" : "not asked for");
    return VK_SUCCESS;
}

} // namespace

VKAPI_ATTR VkResult VKAPI_CALL layer_CreateDevice(VkPhysicalDevice physical_device, const VkDeviceCreateInfo* ci,
                                                  const VkAllocationCallbacks* alloc, VkDevice* out)
{
    return guarded("vkCreateDevice", [&] { return create_device(physical_device, ci, alloc, out); });
}

VKAPI_ATTR void VKAPI_CALL layer_DestroyDevice(VkDevice device, const VkAllocationCallbacks* alloc)
{
    guarded_void("vkDestroyDevice", [&] {
        if (device == VK_NULL_HANDLE) {
            return;
        }
        // Out of the table first: the handle is gone once the call below returns.
        const std::unique_ptr<DeviceData> data = remove_device(device);
        if (!data || data->DestroyDevice == nullptr) {
            // Not ours to forget, but the driver's object must not leak: down through the one function the layer
            // below has, if there is one.
            const PFN_vkDestroyDevice fallback = fallback_destroy_device();
            log(Level::Error, "vkDestroyDevice: device %p is not one the layer knows; %s", static_cast<void*>(device),
                fallback != nullptr ? "passed down" : "it cannot be passed down");
            if (fallback != nullptr) {
                fallback(device, alloc);
            }
            return;
        }
        log(Level::Debug, "vkDestroyDevice: device %p", static_cast<void*>(device));
        // Design 6.10, in order: the device's swapchains left alive are destroyed, the display thread leaves the
        // device (at most a refresh), frame fences still outstanding are waited for (at most 100 ms) ...
        before_device_destroyed(*data);
        // ... the private queue goes idle under its mutex, which stays held: every queue of the device must be
        // externally synchronised for the call down, the private one too (design 6.2) ...
        std::lock_guard<std::mutex> lock(data->queue_mutex);
        if (data->private_queue != VK_NULL_HANDLE && data->QueueWaitIdle != nullptr) {
            const VkResult idle = data->QueueWaitIdle(data->private_queue);
            if (idle != VK_SUCCESS) {
                log(Level::Warn, "vkDestroyDevice: vkQueueWaitIdle on the private queue returned %d",
                    static_cast<int>(idle));
            }
        }
        // ... the device's frames end (no ids completed) and its frame fences are destroyed; then the call down.
        device_destroyed(*data);
        data->DestroyDevice(device, alloc);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_DeviceWaitIdle(VkDevice device)
{
    return guarded("vkDeviceWaitIdle", [&] {
        DeviceData& data = device_data(device);
        // Monado calls this with only its own queue locked (comp_swapchain.c); the private queue is the layer's.
        std::lock_guard<std::mutex> lock(data.queue_mutex);
        return call_down(data.DeviceWaitIdle, "vkDeviceWaitIdle", device);
    });
}

} // namespace dvl
