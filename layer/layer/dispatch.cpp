// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/dispatch.hpp"

#include <shared_mutex>
#include <stdexcept>
#include <unordered_map>
#include <utility>

namespace dvl {
namespace {

// The one destroy function every instance (device) added so far had below it, for fallback_destroy_*(). Once two
// differ there is none, for good.
template <class Fn>
struct Fallback {
    Fn fn = nullptr;
    bool ambiguous = false;

    void note(Fn seen)
    {
        if (seen == nullptr || ambiguous || seen == fn) {
            return;
        }
        if (fn == nullptr) {
            fn = seen;
        } else {
            fn = nullptr;
            ambiguous = true;
        }
    }
};

struct Registry {
    std::shared_mutex mutex;
    std::unordered_map<void*, std::unique_ptr<InstanceData>> instances;
    std::unordered_map<void*, std::unique_ptr<DeviceData>> devices;
    Fallback<PFN_vkDestroyInstance> destroy_instance;
    Fallback<PFN_vkDestroyDevice> destroy_device;
};

// Made at first use and never destroyed: a static would run its destructor at process exit, when a killed thread may
// hold the lock (design 6.10).
Registry& registry()
{
    static Registry* const r = new Registry;
    return *r;
}

} // namespace

std::vector<VkPhysicalDevice> InstanceData::physical_devices()
{
    std::lock_guard<std::mutex> lock(physical_mutex_);
    if (!physical_listed_ && EnumeratePhysicalDevices != nullptr) {
        uint32_t count = 0;
        if (EnumeratePhysicalDevices(handle, &count, nullptr) == VK_SUCCESS && count > 0) {
            std::vector<VkPhysicalDevice> list(count);
            const VkResult r = EnumeratePhysicalDevices(handle, &count, list.data());
            if (r == VK_SUCCESS || r == VK_INCOMPLETE) {
                list.resize(count);
                physical_ = std::move(list);
                physical_listed_ = true;
            }
        }
    }
    return physical_;
}

std::unique_ptr<InstanceData> make_instance_data(VkInstance instance, PFN_vkGetInstanceProcAddr next_gipa)
{
    auto data = std::make_unique<InstanceData>();
    data->handle = instance;
    data->gipa = next_gipa;
#define X(name) data->name = reinterpret_cast<PFN_vk##name>(next_gipa(instance, "vk" #name));
    DVL_INSTANCE_FUNCTIONS(X)
#undef X
    return data;
}

std::unique_ptr<DeviceData> make_device_data(VkDevice device, VkPhysicalDevice physical_device, InstanceData* instance,
                                             PFN_vkGetDeviceProcAddr next_gdpa)
{
    auto data = std::make_unique<DeviceData>();
    data->handle = device;
    data->physical_device = physical_device;
    data->instance = instance;
    data->gdpa = next_gdpa;
#define X(name) data->name = reinterpret_cast<PFN_vk##name>(next_gdpa(device, "vk" #name));
    DVL_DEVICE_FUNCTIONS(X)
#undef X
    return data;
}

InstanceData& add_instance(std::unique_ptr<InstanceData> data)
{
    InstanceData& ref = *data;
    Registry& r = registry();
    std::unique_lock<std::shared_mutex> lock(r.mutex);
    r.destroy_instance.note(ref.DestroyInstance);
    // A key still in the table belongs to an instance that was destroyed without the layer seeing it: replaced.
    r.instances[dispatch_key(ref.handle)] = std::move(data);
    return ref;
}

std::unique_ptr<InstanceData> remove_instance(void* dispatchable)
{
    if (dispatchable == nullptr) {
        return nullptr;
    }
    Registry& r = registry();
    std::unique_lock<std::shared_mutex> lock(r.mutex);
    auto it = r.instances.find(dispatch_key(dispatchable));
    if (it == r.instances.end()) {
        return nullptr;
    }
    std::unique_ptr<InstanceData> data = std::move(it->second);
    r.instances.erase(it);
    return data;
}

InstanceData* find_instance_data(void* dispatchable)
{
    if (dispatchable == nullptr) {
        return nullptr;
    }
    Registry& r = registry();
    std::shared_lock<std::shared_mutex> lock(r.mutex);
    auto it = r.instances.find(dispatch_key(dispatchable));
    return it == r.instances.end() ? nullptr : it->second.get();
}

InstanceData& instance_data(void* dispatchable)
{
    InstanceData* data = find_instance_data(dispatchable);
    if (data == nullptr) {
        throw std::out_of_range("no layer data for this instance or physical device");
    }
    return *data;
}

DeviceData& add_device(std::unique_ptr<DeviceData> data)
{
    DeviceData& ref = *data;
    Registry& r = registry();
    std::unique_lock<std::shared_mutex> lock(r.mutex);
    r.destroy_device.note(ref.DestroyDevice);
    r.devices[dispatch_key(ref.handle)] = std::move(data);
    return ref;
}

std::unique_ptr<DeviceData> remove_device(void* dispatchable)
{
    if (dispatchable == nullptr) {
        return nullptr;
    }
    Registry& r = registry();
    std::unique_lock<std::shared_mutex> lock(r.mutex);
    auto it = r.devices.find(dispatch_key(dispatchable));
    if (it == r.devices.end()) {
        return nullptr;
    }
    std::unique_ptr<DeviceData> data = std::move(it->second);
    r.devices.erase(it);
    return data;
}

DeviceData* find_device_data(void* dispatchable)
{
    if (dispatchable == nullptr) {
        return nullptr;
    }
    Registry& r = registry();
    std::shared_lock<std::shared_mutex> lock(r.mutex);
    auto it = r.devices.find(dispatch_key(dispatchable));
    return it == r.devices.end() ? nullptr : it->second.get();
}

DeviceData& device_data(void* dispatchable)
{
    DeviceData* data = find_device_data(dispatchable);
    if (data == nullptr) {
        throw std::out_of_range("no layer data for this device or queue");
    }
    return *data;
}

size_t for_each_device(const InstanceData* instance, VkPhysicalDevice physical_device,
                       const std::function<void(DeviceData&)>& fn)
{
    Registry& r = registry();
    std::shared_lock<std::shared_mutex> lock(r.mutex);
    size_t n = 0;
    for (const auto& entry : r.devices) {
        DeviceData& device = *entry.second;
        if (device.instance == instance && device.physical_device == physical_device) {
            fn(device);
            n++;
        }
    }
    return n;
}

PFN_vkDestroyInstance fallback_destroy_instance()
{
    Registry& r = registry();
    std::shared_lock<std::shared_mutex> lock(r.mutex);
    return r.destroy_instance.fn;
}

PFN_vkDestroyDevice fallback_destroy_device()
{
    Registry& r = registry();
    std::shared_lock<std::shared_mutex> lock(r.mutex);
    return r.destroy_device.fn;
}

} // namespace dvl
