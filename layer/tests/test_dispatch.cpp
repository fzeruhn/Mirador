// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The layer's dispatch tables: per-instance and per-device data found by the loader's dispatch key.

#include <catch2/catch_test_macros.hpp>

#include <cstring>
#include <stdexcept>
#include <string>
#include <vector>

#include "layer/dispatch.hpp"

namespace {

// A dispatchable handle as the loader makes them: its first word points at a dispatch table. Handles of one
// instance (the instance and its physical devices) share a table, so do those of one device (device, queues).
struct FakeHandle {
    void* dispatch;
};
int g_instance_table;
int g_device_table;
int g_other_table;

VkInstance as_instance(FakeHandle* h) { return reinterpret_cast<VkInstance>(h); }
VkDevice as_device(FakeHandle* h) { return reinterpret_cast<VkDevice>(h); }
VkPhysicalDevice as_physical(FakeHandle* h) { return reinterpret_cast<VkPhysicalDevice>(h); }

FakeHandle g_physical_devices[2] = {{&g_instance_table}, {&g_instance_table}};
int g_enumerate_calls = 0;
std::vector<std::string> g_asked;

void VKAPI_CALL some_function() {}

VkResult VKAPI_CALL fake_EnumeratePhysicalDevices(VkInstance, uint32_t* count, VkPhysicalDevice* out)
{
    g_enumerate_calls++;
    if (!out) {
        *count = 2;
        return VK_SUCCESS;
    }
    uint32_t n = *count < 2 ? *count : 2;
    for (uint32_t i = 0; i < n; i++) {
        out[i] = as_physical(&g_physical_devices[i]);
    }
    *count = n;
    return n < 2 ? VK_INCOMPLETE : VK_SUCCESS;
}

PFN_vkVoidFunction VKAPI_CALL fake_gipa(VkInstance, const char* name)
{
    g_asked.emplace_back(name);
    if (std::strcmp(name, "vkEnumeratePhysicalDevices") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&fake_EnumeratePhysicalDevices);
    }
    return reinterpret_cast<PFN_vkVoidFunction>(&some_function);
}

PFN_vkVoidFunction VKAPI_CALL fake_gdpa(VkDevice, const char* name)
{
    g_asked.emplace_back(name);
    return reinterpret_cast<PFN_vkVoidFunction>(&some_function);
}

PFN_vkVoidFunction VKAPI_CALL gdpa_with_holes(VkDevice, const char* name)
{
    g_asked.emplace_back(name);
    // An extension that is not enabled on the device has no entry point.
    if (std::strstr(name, "KHR") != nullptr) {
        return nullptr;
    }
    return reinterpret_cast<PFN_vkVoidFunction>(&some_function);
}

} // namespace

TEST_CASE("dispatch_key_is_the_first_word_of_the_handle")
{
    FakeHandle a{&g_instance_table};
    FakeHandle b{&g_instance_table};
    FakeHandle c{&g_other_table};
    CHECK(dvl::dispatch_key(&a) == &g_instance_table);
    CHECK(dvl::dispatch_key(&a) == dvl::dispatch_key(&b));
    CHECK(dvl::dispatch_key(&a) != dvl::dispatch_key(&c));
}

TEST_CASE("instance_data_found_by_dispatch_key")
{
    FakeHandle instance{&g_instance_table};
    FakeHandle physical{&g_instance_table};
    FakeHandle other{&g_other_table};

    g_asked.clear();
    auto made = dvl::make_instance_data(as_instance(&instance), fake_gipa);
    REQUIRE(made);
    CHECK(made->handle == as_instance(&instance));
    CHECK(made->gipa == fake_gipa);

    // Every function in the list was asked of the next layer, by its Vulkan name, and every one is set.
    std::vector<std::string> expected;
#define X(name) expected.push_back("vk" #name);
    DVL_INSTANCE_FUNCTIONS(X)
#undef X
    CHECK(g_asked == expected);
#define X(name) CHECK(made->name != nullptr);
    DVL_INSTANCE_FUNCTIONS(X)
#undef X

    CHECK(dvl::find_instance_data(&instance) == nullptr);
    dvl::InstanceData& added = dvl::add_instance(std::move(made));
    CHECK(dvl::find_instance_data(&instance) == &added);
    CHECK(&dvl::instance_data(&instance) == &added);
    // A physical device has the instance's dispatch key.
    CHECK(&dvl::instance_data(&physical) == &added);
    CHECK(dvl::find_instance_data(&other) == nullptr);
    CHECK_THROWS_AS(dvl::instance_data(&other), std::out_of_range);

    auto removed = dvl::remove_instance(&instance);
    CHECK(removed.get() == &added);
    CHECK(dvl::find_instance_data(&instance) == nullptr);
    CHECK_THROWS_AS(dvl::instance_data(&instance), std::out_of_range);
    CHECK(dvl::remove_instance(&instance) == nullptr);
}

TEST_CASE("instance_data_lists_physical_devices_once")
{
    FakeHandle instance{&g_instance_table};
    g_enumerate_calls = 0;
    auto data = dvl::make_instance_data(as_instance(&instance), fake_gipa);
    REQUIRE(data);
    CHECK(g_enumerate_calls == 0); // not at instance creation: that stays cheap

    const std::vector<VkPhysicalDevice>& list = data->physical_devices();
    REQUIRE(list.size() == 2);
    CHECK(list[0] == as_physical(&g_physical_devices[0]));
    CHECK(list[1] == as_physical(&g_physical_devices[1]));
    CHECK(g_enumerate_calls == 2); // the count, then the handles
    data->physical_devices();
    CHECK(g_enumerate_calls == 2);
}

TEST_CASE("device_data_found_by_dispatch_key")
{
    FakeHandle instance{&g_instance_table};
    FakeHandle physical{&g_instance_table};
    FakeHandle device{&g_device_table};
    FakeHandle queue{&g_device_table};
    FakeHandle other{&g_other_table};

    auto inst = dvl::make_instance_data(as_instance(&instance), fake_gipa);
    g_asked.clear();
    auto made = dvl::make_device_data(as_device(&device), as_physical(&physical), inst.get(), fake_gdpa);
    REQUIRE(made);
    CHECK(made->handle == as_device(&device));
    CHECK(made->physical_device == as_physical(&physical));
    CHECK(made->instance == inst.get());
    CHECK(made->gdpa == fake_gdpa);
    CHECK(made->private_queue == VK_NULL_HANDLE);
    CHECK(made->fence_pool.created() == 0);
    for (const VkFence fence : made->fences) {
        CHECK(fence == VK_NULL_HANDLE); // no frame fence is made before a present takes its slot
    }

    std::vector<std::string> expected;
#define X(name) expected.push_back("vk" #name);
    DVL_DEVICE_FUNCTIONS(X)
#undef X
    CHECK(g_asked == expected);
#define X(name) CHECK(made->name != nullptr);
    DVL_DEVICE_FUNCTIONS(X)
#undef X

    dvl::DeviceData& added = dvl::add_device(std::move(made));
    CHECK(&dvl::device_data(&device) == &added);
    // The device's queues have its dispatch key.
    CHECK(&dvl::device_data(&queue) == &added);
    CHECK(dvl::find_device_data(&other) == nullptr);
    CHECK(dvl::find_device_data(&instance) == nullptr); // an instance is not a device
    CHECK_THROWS_AS(dvl::device_data(&other), std::out_of_range);

    auto removed = dvl::remove_device(&device);
    CHECK(removed.get() == &added);
    CHECK(dvl::find_device_data(&queue) == nullptr);
}

TEST_CASE("device_data_keeps_missing_entry_points_null")
{
    FakeHandle instance{&g_instance_table};
    FakeHandle device{&g_device_table};
    auto inst = dvl::make_instance_data(as_instance(&instance), fake_gipa);
    auto data = dvl::make_device_data(as_device(&device), VK_NULL_HANDLE, inst.get(), gdpa_with_holes);
    REQUIRE(data);
    CHECK(data->DestroyDevice != nullptr);
    CHECK(data->QueueSubmit != nullptr);
    CHECK(data->CreateSwapchainKHR == nullptr);
    CHECK(data->WaitForPresentKHR == nullptr);
}

namespace {

void VKAPI_CALL destroy_instance_a(VkInstance, const VkAllocationCallbacks*) {}
void VKAPI_CALL destroy_instance_b(VkInstance, const VkAllocationCallbacks*) {}
void VKAPI_CALL destroy_device_a(VkDevice, const VkAllocationCallbacks*) {}
void VKAPI_CALL destroy_device_b(VkDevice, const VkAllocationCallbacks*) {}

} // namespace

TEST_CASE("fallback_destroy_is_none_once_two_differ")
{
    // For a handle the registry does not have, the layer passes vkDestroy* down only through the one function every
    // registered object had below it. The registry is process-wide, so this checks what holds whatever came before:
    // two different ones leave none. (That one function is used is checked through the DLL, test_device_setup.cpp.)
    FakeHandle i1{&g_instance_table};
    FakeHandle i2{&g_other_table};
    auto a = std::make_unique<dvl::InstanceData>();
    a->handle = as_instance(&i1);
    a->DestroyInstance = destroy_instance_a;
    dvl::add_instance(std::move(a));
    auto b = std::make_unique<dvl::InstanceData>();
    b->handle = as_instance(&i2);
    b->DestroyInstance = destroy_instance_b;
    dvl::add_instance(std::move(b));
    CHECK(dvl::fallback_destroy_instance() == nullptr);
    dvl::remove_instance(&i1);
    dvl::remove_instance(&i2);
    CHECK(dvl::fallback_destroy_instance() == nullptr); // for good

    FakeHandle d1{&g_device_table};
    FakeHandle d2{&g_other_table};
    auto c = std::make_unique<dvl::DeviceData>();
    c->handle = as_device(&d1);
    c->DestroyDevice = destroy_device_a;
    dvl::add_device(std::move(c));
    auto d = std::make_unique<dvl::DeviceData>();
    d->handle = as_device(&d2);
    d->DestroyDevice = destroy_device_b;
    dvl::add_device(std::move(d));
    CHECK(dvl::fallback_destroy_device() == nullptr);
    dvl::remove_device(&d1);
    dvl::remove_device(&d2);
}
