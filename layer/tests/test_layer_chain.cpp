// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The built DLL driven the way the loader drives a layer: negotiation, then vkCreateInstance and vkCreateDevice down a
// chain of one fake "next layer" (a driver stand-in). No GPU, no loader.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

#include "fakes/layer_dll.hpp"

namespace {

// A dispatchable handle as the loader makes them: the first word points at a dispatch table.
struct FakeHandle {
    void* dispatch;
};
int g_instance_table;
int g_device_table;
FakeHandle g_instance{&g_instance_table};
FakeHandle g_physical{&g_instance_table};
FakeHandle g_device{&g_device_table};

// What the fake next layer saw.
struct NextLayer {
    std::vector<std::string> instance_extensions;
    std::vector<std::string> device_extensions;
    bool instance_destroyed = false;
    bool device_destroyed = false;
    int create_instance_calls = 0;
    int create_device_calls = 0;
    std::vector<std::string> gipa_asked;
    std::vector<std::string> gdpa_asked;
    const void* device_create_pnext = nullptr;
};
NextLayer g_next;

void VKAPI_CALL next_Marker() {}
void VKAPI_CALL next_DeviceMarker() {}

VkResult VKAPI_CALL next_CreateInstance(const VkInstanceCreateInfo* ci, const VkAllocationCallbacks*, VkInstance* out)
{
    g_next.create_instance_calls++;
    g_next.instance_extensions.clear();
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) {
        g_next.instance_extensions.emplace_back(ci->ppEnabledExtensionNames[i]);
    }
    *out = reinterpret_cast<VkInstance>(&g_instance);
    return VK_SUCCESS;
}

void VKAPI_CALL next_DestroyInstance(VkInstance, const VkAllocationCallbacks*) { g_next.instance_destroyed = true; }

VkResult VKAPI_CALL next_CreateDevice(VkPhysicalDevice, const VkDeviceCreateInfo* ci, const VkAllocationCallbacks*,
                                      VkDevice* out)
{
    g_next.create_device_calls++;
    g_next.device_extensions.clear();
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) {
        g_next.device_extensions.emplace_back(ci->ppEnabledExtensionNames[i]);
    }
    g_next.device_create_pnext = ci->pNext;
    *out = reinterpret_cast<VkDevice>(&g_device);
    return VK_SUCCESS;
}

void VKAPI_CALL next_DestroyDevice(VkDevice, const VkAllocationCallbacks*) { g_next.device_destroyed = true; }

PFN_vkVoidFunction VKAPI_CALL next_GetDeviceProcAddr(VkDevice, const char* name)
{
    g_next.gdpa_asked.emplace_back(name);
    if (std::strcmp(name, "vkDestroyDevice") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_DestroyDevice);
    }
    if (std::strcmp(name, "vkQueueSubmit") == 0 || std::strcmp(name, "vkDeviceWaitIdle") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_DeviceMarker);
    }
    if (std::strcmp(name, "vkNotOurs") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_DeviceMarker);
    }
    return nullptr;
}

PFN_vkVoidFunction VKAPI_CALL next_GetInstanceProcAddr(VkInstance, const char* name)
{
    g_next.gipa_asked.emplace_back(name);
    if (std::strcmp(name, "vkCreateInstance") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_CreateInstance);
    }
    if (std::strcmp(name, "vkDestroyInstance") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_DestroyInstance);
    }
    if (std::strcmp(name, "vkCreateDevice") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_CreateDevice);
    }
    if (std::strcmp(name, "vkGetDeviceProcAddr") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_GetDeviceProcAddr);
    }
    if (std::strcmp(name, "vkNotOurs") == 0) {
        return reinterpret_cast<PFN_vkVoidFunction>(&next_Marker);
    }
    return nullptr;
}

bool contains(const std::vector<std::string>& v, const char* s)
{
    return std::find(v.begin(), v.end(), s) != v.end();
}

} // namespace

TEST_CASE("layer_chains_instance_and_device_creation")
{
    dvl_test::ScopedEnv processes(L"DREAMAIR_VK_LAYER_PROCESSES", L"dvl_tests.exe");
    dvl_test::ScopedEnv quiet(L"DREAMAIR_VK_LAYER_LOG", L"off");
    g_next = NextLayer{};
    dvl_test::LayerDll dll;
    REQUIRE(dll.loaded());
    auto n = dvl_test::negotiate(dll, 2);
    REQUIRE(n.result == VK_SUCCESS);
    PFN_vkGetInstanceProcAddr gipa = n.iface.pfnGetInstanceProcAddr;
    PFN_vkGetDeviceProcAddr gdpa = n.iface.pfnGetDeviceProcAddr;

    // The names the layer answers itself, and the ones it passes on.
    {
        auto self = reinterpret_cast<PFN_vkVoidFunction>(gipa);
        CHECK(gipa(VK_NULL_HANDLE, "vkGetInstanceProcAddr") == self);
        CHECK(gipa(VK_NULL_HANDLE, "vkCreateInstance") != nullptr);
        CHECK(gipa(VK_NULL_HANDLE, "vkCreateInstance") != reinterpret_cast<PFN_vkVoidFunction>(&next_CreateInstance));
        // Without an instance there is nothing to pass a name on to.
        CHECK(gipa(VK_NULL_HANDLE, "vkNotOurs") == nullptr);
        CHECK(gipa(VK_NULL_HANDLE, "vkEnumerateInstanceExtensionProperties") == nullptr);
        CHECK(gdpa(VK_NULL_HANDLE, "vkNotOurs") == nullptr);
        CHECK(gipa(VK_NULL_HANDLE, nullptr) == nullptr);
    }

    VkLayerInstanceLink link{nullptr, next_GetInstanceProcAddr, nullptr};
    VkLayerInstanceCreateInfo chain{};
    chain.sType = VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO;
    chain.function = VK_LAYER_LINK_INFO;
    chain.u.pLayerInfo = &link;

    const char* extensions[] = {"VK_KHR_surface", VK_EXT_DISPLAY_SURFACE_COUNTER_EXTENSION_NAME, "VK_KHR_display"};
    VkInstanceCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ci.pNext = &chain;
    ci.enabledExtensionCount = 3;
    ci.ppEnabledExtensionNames = extensions;

    auto create_instance = reinterpret_cast<PFN_vkCreateInstance>(gipa(VK_NULL_HANDLE, "vkCreateInstance"));
    REQUIRE(create_instance != nullptr);
    VkInstance instance = VK_NULL_HANDLE;
    REQUIRE(create_instance(&ci, nullptr, &instance) == VK_SUCCESS);
    CHECK(instance == reinterpret_cast<VkInstance>(&g_instance));
    CHECK(g_next.create_instance_calls == 1);
    // It loaded the next layer's functions for the instance.
    CHECK(contains(g_next.gipa_asked, "vkDestroyInstance"));
    CHECK(contains(g_next.gipa_asked, "vkGetPhysicalDeviceProperties"));

    // The layer took its link out of the chain (the next layer must find its own), and the extension that the driver
    // lacks did not go down.
    CHECK(chain.u.pLayerInfo == nullptr);
    CHECK(g_next.instance_extensions == std::vector<std::string>{"VK_KHR_surface", "VK_KHR_display"});
    // The caller's create info is unchanged.
    CHECK(ci.enabledExtensionCount == 3);
    CHECK(ci.ppEnabledExtensionNames == extensions);

    // Names go to the next layer once the instance exists, and ours are answered first.
    CHECK(gipa(instance, "vkNotOurs") == reinterpret_cast<PFN_vkVoidFunction>(&next_Marker));
    CHECK(gipa(instance, "vkDestroyInstance") != nullptr);
    CHECK(gipa(instance, "vkDestroyInstance") != reinterpret_cast<PFN_vkVoidFunction>(&next_DestroyInstance));
    CHECK(gipa(instance, "vkDoesNotExist") == nullptr);
    // A physical device has the instance's dispatch key.
    CHECK(gipa(reinterpret_cast<VkInstance>(&g_physical), "vkNotOurs") ==
          reinterpret_cast<PFN_vkVoidFunction>(&next_Marker));

    // The device: the same walk down.
    VkLayerDeviceLink dlink{nullptr, next_GetInstanceProcAddr, next_GetDeviceProcAddr};
    VkLayerDeviceCreateInfo dchain{};
    dchain.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
    dchain.function = VK_LAYER_LINK_INFO;
    dchain.u.pLayerInfo = &dlink;
    const char* device_extensions[] = {"VK_KHR_swapchain"};
    VkDeviceCreateInfo dci{};
    dci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    dci.pNext = &dchain;
    dci.enabledExtensionCount = 1;
    dci.ppEnabledExtensionNames = device_extensions;

    auto create_device = reinterpret_cast<PFN_vkCreateDevice>(gipa(instance, "vkCreateDevice"));
    REQUIRE(create_device != nullptr);
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(create_device(reinterpret_cast<VkPhysicalDevice>(&g_physical), &dci, nullptr, &device) == VK_SUCCESS);
    CHECK(device == reinterpret_cast<VkDevice>(&g_device));
    CHECK(g_next.create_device_calls == 1);
    CHECK(contains(g_next.gdpa_asked, "vkQueueSubmit"));
    CHECK(dchain.u.pLayerInfo == nullptr);
    CHECK(g_next.device_extensions == std::vector<std::string>{"VK_KHR_swapchain"});
    CHECK(g_next.device_create_pnext == &dchain); // the chain went down as it came

    CHECK(gdpa(device, "vkNotOurs") == reinterpret_cast<PFN_vkVoidFunction>(&next_DeviceMarker));
    CHECK(gdpa(device, "vkDoesNotExist") == nullptr);
    CHECK(gdpa(device, "vkDestroyDevice") != nullptr);
    CHECK(gdpa(device, "vkDestroyDevice") != reinterpret_cast<PFN_vkVoidFunction>(&next_DestroyDevice));
    // The instance level also answers device commands, with the same functions.
    CHECK(gipa(instance, "vkDestroyDevice") == gdpa(device, "vkDestroyDevice"));

    // Teardown, in the loader's order.
    auto destroy_device = reinterpret_cast<PFN_vkDestroyDevice>(gdpa(device, "vkDestroyDevice"));
    destroy_device(device, nullptr);
    CHECK(g_next.device_destroyed);
    CHECK(gdpa(device, "vkNotOurs") == nullptr); // forgotten

    auto destroy_instance = reinterpret_cast<PFN_vkDestroyInstance>(gipa(instance, "vkDestroyInstance"));
    destroy_instance(instance, nullptr);
    CHECK(g_next.instance_destroyed);
    CHECK(gipa(instance, "vkNotOurs") == nullptr); // forgotten
}

TEST_CASE("layer_create_instance_without_link_info_fails")
{
    dvl_test::ScopedEnv processes(L"DREAMAIR_VK_LAYER_PROCESSES", L"dvl_tests.exe");
    dvl_test::ScopedEnv quiet(L"DREAMAIR_VK_LAYER_LOG", L"off");
    g_next = NextLayer{};
    dvl_test::LayerDll dll;
    REQUIRE(dll.loaded());
    auto n = dvl_test::negotiate(dll, 2);
    REQUIRE(n.result == VK_SUCCESS);

    VkInstanceCreateInfo ci{};
    ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    auto create_instance =
        reinterpret_cast<PFN_vkCreateInstance>(n.iface.pfnGetInstanceProcAddr(VK_NULL_HANDLE, "vkCreateInstance"));
    REQUIRE(create_instance != nullptr);
    VkInstance instance = VK_NULL_HANDLE;
    CHECK(create_instance(&ci, nullptr, &instance) == VK_ERROR_INITIALIZATION_FAILED);
    CHECK(g_next.create_instance_calls == 0);
}

TEST_CASE("only_the_test_build_exports_the_display_session_seam")
{
    // The tests put a display session on a fake NVAPI into the layer through an export of the DLL's test build; the
    // shipped DLL has no such entry point.
    dvl_test::LayerDll shipped;
    REQUIRE(shipped.loaded());
    CHECK(shipped.proc<void (*)()>("dvl_test_set_display_session") == nullptr);
    CHECK(shipped.proc<void (*)()>("vkNegotiateLoaderLayerInterfaceVersion") != nullptr);
    dvl_test::LayerDll test_build(DVL_LAYER_TEST_DLL);
    REQUIRE(test_build.loaded());
    CHECK(test_build.proc<void (*)()>("dvl_test_set_display_session") != nullptr);
}
