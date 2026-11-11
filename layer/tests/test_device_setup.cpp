// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// Device setup (design 6.2): the plan alone (plan_device), then vkCreateDevice, vkDeviceWaitIdle and vkDestroyDevice
// through the layer's chain to a fake driver.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "fakes/fake_chain.hpp"
#include "layer/device_setup.hpp"

namespace {

using dvl_test::ChainRig;
using dvl_test::g_driver;

// A VkDeviceCreateInfo and what it points at.
struct DeviceInfo {
    std::vector<VkDeviceQueueCreateInfo> queues;
    std::vector<const char*> extensions;

    VkDeviceCreateInfo ci() const
    {
        VkDeviceCreateInfo c{};
        c.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
        c.queueCreateInfoCount = static_cast<uint32_t>(queues.size());
        c.pQueueCreateInfos = queues.data();
        c.enabledExtensionCount = static_cast<uint32_t>(extensions.size());
        c.ppEnabledExtensionNames = extensions.data();
        return c;
    }
};

VkDeviceQueueCreateInfo queue_info(uint32_t family, uint32_t count, const float* priorities, const void* next = nullptr,
                                   VkDeviceQueueCreateFlags flags = 0)
{
    return VkDeviceQueueCreateInfo{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, next, flags, family, count, priorities};
}

size_t count_of(const std::vector<const char*>& names, const char* name)
{
    return static_cast<size_t>(
        std::count_if(names.begin(), names.end(), [&](const char* n) { return std::strcmp(n, name) == 0; }));
}

size_t count_of(const std::vector<std::string>& names, const char* name)
{
    return static_cast<size_t>(std::count(names.begin(), names.end(), std::string(name)));
}

const float kPriorities[8] = {0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f};

} // namespace

TEST_CASE("private_queue_added_to_first_info")
{
    // Monado's compute compositor: the compute family (8 queues), one queue, the global-priority pNext.
    dvl_test::MonadoQueue monado;
    const float other_priority = 1.0f;
    DeviceInfo info;
    info.queues = {monado.info, queue_info(0, 1, &other_priority)};

    std::optional<dvl::DevicePlan> plan =
        dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(), dvl::kNvidiaVendorId);
    REQUIRE(plan.has_value());
    REQUIRE(plan->queues.size() == 2);
    const VkDeviceQueueCreateInfo& first = plan->queues[0];
    CHECK(first.queueFamilyIndex == 2);
    CHECK(first.queueCount == 2);
    CHECK(first.pNext == &monado.priority); // the global priority applies to the private queue too
    REQUIRE(first.pQueuePriorities != nullptr);
    CHECK(first.pQueuePriorities[0] == 0.5f);
    CHECK(first.pQueuePriorities[1] == 0.5f);
    CHECK(plan->private_family == 2);
    CHECK(plan->private_index == 1);
    CHECK_FALSE(plan->new_info);
    // The other info goes down as it came.
    CHECK(plan->queues[1].queueFamilyIndex == 0);
    CHECK(plan->queues[1].queueCount == 1);
    CHECK(plan->queues[1].pQueuePriorities == &other_priority);
    // The application's own info is not touched.
    CHECK(monado.info.queueCount == 1);
    CHECK(monado.info.pQueuePriorities == monado.priorities);

    // The plan moves without its priorities moving under the info.
    dvl::DevicePlan moved = std::move(*plan);
    CHECK(moved.queues[0].pQueuePriorities[1] == 0.5f);
}

TEST_CASE("private_queue_skips_flagged_info")
{
    const float one = 1.0f;
    DeviceInfo info;
    info.queues = {queue_info(2, 1, &one, nullptr, VK_DEVICE_QUEUE_CREATE_PROTECTED_BIT)};
    std::optional<dvl::DevicePlan> plan =
        dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(), dvl::kNvidiaVendorId);
    REQUIRE(plan.has_value());
    REQUIRE(plan->queues.size() == 2);
    CHECK(plan->queues[0].queueCount == 1); // the protected info is left alone
    CHECK(plan->queues[0].flags == VK_DEVICE_QUEUE_CREATE_PROTECTED_BIT);
    const VkDeviceQueueCreateInfo& added = plan->queues[1];
    CHECK(added.sType == VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO);
    CHECK(added.pNext == nullptr);
    CHECK(added.flags == 0);
    CHECK(added.queueFamilyIndex == 0); // the first family not listed
    CHECK(added.queueCount == 1);
    REQUIRE(added.pQueuePriorities != nullptr);
    CHECK(added.pQueuePriorities[0] == 1.0f);
    CHECK(plan->private_family == 0);
    CHECK(plan->private_index == 0);
    CHECK(plan->new_info);
}

TEST_CASE("private_queue_full_family_uses_other")
{
    // Every queue of the compute family taken: a family that is not listed.
    DeviceInfo info;
    info.queues = {queue_info(2, 8, kPriorities), queue_info(0, 1, kPriorities)};
    std::optional<dvl::DevicePlan> plan =
        dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(), dvl::kNvidiaVendorId);
    REQUIRE(plan.has_value());
    REQUIRE(plan->queues.size() == 3);
    CHECK(plan->queues[0].queueCount == 8);
    CHECK(plan->queues[2].queueFamilyIndex == 1); // 0 and 2 are listed
    CHECK(plan->private_family == 1);
    CHECK(plan->private_index == 0);
    CHECK(plan->new_info);

    SECTION("no family has room: no private queue, the rest of the plan stands")
    {
        info.queues.push_back(queue_info(1, 2, kPriorities));
        plan = dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(),
                                dvl::kNvidiaVendorId);
        REQUIRE(plan.has_value());
        CHECK(plan->queues.size() == 3);
        CHECK(plan->private_family == VK_QUEUE_FAMILY_IGNORED);
        CHECK(plan->extensions.size() == 5);
    }
}

TEST_CASE("non_nvidia_device_untouched")
{
    DeviceInfo info;
    info.queues = {queue_info(0, 1, kPriorities)};
    info.extensions = {"VK_KHR_swapchain", VK_EXT_DISPLAY_CONTROL_EXTENSION_NAME};
    CHECK_FALSE(dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(), 0x8086)
                    .has_value());
    CHECK_FALSE(
        dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(), 0x1002).has_value());
}

TEST_CASE("import_extensions_added_once")
{
    DeviceInfo info;
    info.queues = {queue_info(2, 1, kPriorities)};
    info.extensions = {"VK_KHR_swapchain", "VK_KHR_external_memory", "VK_KHR_dedicated_allocation"};
    std::optional<dvl::DevicePlan> plan =
        dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(), dvl::kNvidiaVendorId);
    REQUIRE(plan.has_value());
    CHECK(count_of(plan->extensions, "VK_KHR_swapchain") == 1);
    for (const char* name : dvl::kImportExtensions) {
        INFO(name);
        CHECK(count_of(plan->extensions, name) == 1);
    }
    CHECK(plan->extensions.size() == 6);
    CHECK(plan->missing_extensions.empty());
    // The application's names are passed on as they are (the same strings).
    CHECK(plan->extensions[0] == info.extensions[0]);

    SECTION("an import extension the device lacks is not added (the call down would fail), and is named")
    {
        std::vector<std::string> available = dvl_test::nvidia_extensions();
        available.erase(std::find(available.begin(), available.end(), "VK_KHR_image_format_list"));
        plan = dvl::plan_device(info.ci(), dvl_test::nvidia_families(), available, dvl::kNvidiaVendorId);
        REQUIRE(plan.has_value());
        CHECK(count_of(plan->extensions, "VK_KHR_image_format_list") == 0);
        REQUIRE(plan->missing_extensions.size() == 1);
        CHECK(std::strcmp(plan->missing_extensions[0], "VK_KHR_image_format_list") == 0);
    }
}

TEST_CASE("display_control_removed_before_call_down")
{
    DeviceInfo info;
    info.queues = {queue_info(2, 1, kPriorities)};
    info.extensions = {"VK_KHR_swapchain", VK_EXT_DISPLAY_CONTROL_EXTENSION_NAME, "VK_KHR_present_wait"};
    std::optional<dvl::DevicePlan> plan =
        dvl::plan_device(info.ci(), dvl_test::nvidia_families(), dvl_test::nvidia_extensions(), dvl::kNvidiaVendorId);
    REQUIRE(plan.has_value());
    CHECK(count_of(plan->extensions, VK_EXT_DISPLAY_CONTROL_EXTENSION_NAME) == 0);
    CHECK(plan->display_control);
    CHECK(count_of(plan->extensions, "VK_KHR_present_wait") == 1);

    // Through the layer: the driver never sees it, and gets the import extensions.
    ChainRig rig;
    REQUIRE(rig.ready());
    dvl_test::MonadoQueue monado;
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(0, {monado.info}, info.extensions, &device) == VK_SUCCESS);
    CHECK(count_of(g_driver.device_extensions, VK_EXT_DISPLAY_CONTROL_EXTENSION_NAME) == 0);
    CHECK(count_of(g_driver.device_extensions, "VK_KHR_swapchain") == 1);
    for (const char* name : dvl::kImportExtensions) {
        INFO(name);
        CHECK(count_of(g_driver.device_extensions, name) == 1);
    }
    rig.destroy_device(device);
}

TEST_CASE("create_device_sets_up_the_private_queue")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    dvl_test::MonadoQueue monado;
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(0, {monado.info}, {"VK_KHR_swapchain"}, &device) == VK_SUCCESS);
    CHECK(device == reinterpret_cast<VkDevice>(&dvl_test::g_devices[0]));

    // What went down: Monado's info, one queue more, priorities extended, its pNext kept; the loader's structs too.
    REQUIRE(g_driver.queues.size() == 1);
    CHECK(g_driver.queues[0].queueFamilyIndex == 2);
    CHECK(g_driver.queues[0].queueCount == 2);
    CHECK(g_driver.queues[0].pNext == &monado.priority);
    CHECK(g_driver.priorities[0] == std::vector<float>{0.5f, 0.5f});
    CHECK(g_driver.device_pnext_loader_structs ==
          std::vector<VkLayerFunction>{VK_LAYER_LINK_INFO, VK_LOADER_DATA_CALLBACK});

    // The private queue: taken with vkGetDeviceQueue (Vulkan 1.0), given to the loader's callback, which made it one
    // of the device's dispatchable objects.
    const dvl_test::FakeDriver::Call get = g_driver.last("vkGetDeviceQueue");
    CHECK(get.handle == 2);
    CHECK(get.index == 1);
    REQUIRE(g_driver.queue_objects.size() == 1);
    REQUIRE(g_driver.loader_data_set.size() == 1);
    CHECK(g_driver.loader_data_set[0] == &g_driver.queue_objects[0]);
    CHECK(g_driver.queue_objects[0].table == dvl_test::g_devices[0].table);

    // The device is known by its dispatch key: names it does not intercept go to the driver.
    CHECK(rig.device_fn<PFN_vkVoidFunction>(device, "vkGetDeviceQueue") ==
          reinterpret_cast<PFN_vkVoidFunction>(&dvl_test::fake::GetDeviceQueue));
    rig.destroy_device(device);
    CHECK(g_driver.count("vkDestroyDevice") == 1);
    CHECK(rig.device_fn<PFN_vkVoidFunction>(device, "vkGetDeviceQueue") == nullptr); // forgotten
}

TEST_CASE("non_nvidia_device_passed_down_untouched")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const float one = 1.0f;
    const std::vector<const char*> extensions = {"VK_KHR_swapchain", VK_EXT_DISPLAY_CONTROL_EXTENSION_NAME};
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(2, {queue_info(0, 1, &one)}, extensions, &device) == VK_SUCCESS);
    REQUIRE(g_driver.queues.size() == 1);
    CHECK(g_driver.queues[0].queueCount == 1);
    CHECK(g_driver.queues[0].pQueuePriorities == &one);
    CHECK(g_driver.device_extensions == std::vector<std::string>{"VK_KHR_swapchain", "VK_EXT_display_control"});
    CHECK(g_driver.count("vkGetDeviceQueue") == 0);
    CHECK(g_driver.count("vkSetDeviceLoaderData") == 0);
    CHECK(g_driver.count("vkEnumerateDeviceExtensionProperties") == 0);
    rig.destroy_device(device);
}

TEST_CASE("failed_create_device_returns_the_driver_code_and_keeps_nothing")
{
    // Monado retries with a lower priority after VK_ERROR_NOT_PERMITTED (comp_vulkan.c).
    ChainRig rig;
    REQUIRE(rig.ready());
    dvl_test::MonadoQueue monado;
    g_driver.create_device_result = VK_ERROR_NOT_PERMITTED_KHR;
    VkDevice device = VK_NULL_HANDLE;
    CHECK(rig.create_device(0, {monado.info}, {}, &device) == VK_ERROR_NOT_PERMITTED_KHR);
    CHECK(device == VK_NULL_HANDLE);
    CHECK(g_driver.count("vkGetDeviceQueue") == 0);
    CHECK(g_driver.count("vkSetDeviceLoaderData") == 0);
    CHECK(rig.device_fn<PFN_vkVoidFunction>(reinterpret_cast<VkDevice>(&dvl_test::g_devices[0]), "vkGetDeviceQueue") ==
          nullptr);

    // The retry works as a first attempt would.
    g_driver.create_device_result = VK_SUCCESS;
    monado.priority.globalPriority = VK_QUEUE_GLOBAL_PRIORITY_HIGH_KHR;
    REQUIRE(rig.create_device(0, {monado.info}, {}, &device) == VK_SUCCESS);
    CHECK(g_driver.queues[0].queueCount == 2);
    CHECK(g_driver.count("vkSetDeviceLoaderData") == 1);
    rig.destroy_device(device);
}

TEST_CASE("loader_data_failure_undoes_the_device")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    dvl_test::MonadoQueue monado;
    g_driver.set_loader_data_result = VK_ERROR_OUT_OF_HOST_MEMORY;
    VkDevice device = VK_NULL_HANDLE;
    CHECK(rig.create_device(0, {monado.info}, {}, &device) == VK_ERROR_OUT_OF_HOST_MEMORY);
    CHECK(device == VK_NULL_HANDLE);
    CHECK(g_driver.count("vkDestroyDevice") == 1); // the driver's device is not leaked
    CHECK(g_driver.last("vkDestroyDevice").handle == dvl_test::handle_value(&dvl_test::g_devices[0]));
    CHECK(rig.device_fn<PFN_vkVoidFunction>(reinterpret_cast<VkDevice>(&dvl_test::g_devices[0]), "vkGetDeviceQueue") ==
          nullptr);

    SECTION("no loader-data callback in the chain: the same")
    {
        g_driver.set_loader_data_result = VK_SUCCESS;
        g_driver.clear_calls();
        CHECK(rig.create_device(0, {monado.info}, {}, &device, false) == VK_ERROR_INITIALIZATION_FAILED);
        CHECK(device == VK_NULL_HANDLE);
        CHECK(g_driver.count("vkDestroyDevice") == 1);
    }
}

TEST_CASE("wait_idle_and_destroy_hold_the_private_queue_mutex")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    dvl_test::MonadoQueue monado;
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(0, {monado.info}, {}, &device) == VK_SUCCESS);
    const auto wait_idle = rig.device_fn<PFN_vkDeviceWaitIdle>(device, "vkDeviceWaitIdle");
    REQUIRE(wait_idle != nullptr);
    CHECK(wait_idle != reinterpret_cast<PFN_vkDeviceWaitIdle>(&dvl_test::fake::DeviceWaitIdle));

    // The first vkDeviceWaitIdle stays inside the driver until released.
    std::mutex m;
    std::condition_variable cv;
    bool release = false;
    std::atomic<int> inside{0};
    g_driver.on_device_wait_idle = [&] {
        if (++inside == 1) {
            std::unique_lock<std::mutex> lock(m);
            cv.wait_for(lock, std::chrono::seconds(5), [&] { return release; });
        }
    };
    std::thread first([&] { CHECK(wait_idle(device) == VK_SUCCESS); });
    for (int i = 0; i < 500 && inside.load() == 0; i++) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    REQUIRE(inside.load() == 1);

    // A second wait and a destroy from other threads do not reach the driver while it runs.
    std::thread second([&] { CHECK(wait_idle(device) == VK_SUCCESS); });
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    CHECK(inside.load() == 1);
    CHECK(g_driver.count("vkDeviceWaitIdle") == 1);
    {
        std::lock_guard<std::mutex> lock(m);
        release = true;
    }
    cv.notify_all();
    first.join();
    second.join();
    CHECK(g_driver.count("vkDeviceWaitIdle") == 2);

    inside = 0;
    release = false;
    std::thread third([&] { CHECK(wait_idle(device) == VK_SUCCESS); });
    for (int i = 0; i < 500 && inside.load() == 0; i++) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    REQUIRE(inside.load() == 1);
    std::thread destroyer([&] { rig.destroy_device(device); });
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    CHECK(g_driver.count("vkDestroyDevice") == 0);
    {
        std::lock_guard<std::mutex> lock(m);
        release = true;
    }
    cv.notify_all();
    third.join();
    destroyer.join();
    CHECK(g_driver.count("vkDestroyDevice") == 1);
}

TEST_CASE("destroy_of_unknown_handles_still_calls_down")
{
    // A handle the registry does not have: passed down through the one destroy function the layer below has, so the
    // driver's object is not leaked.
    ChainRig rig;
    REQUIRE(rig.ready());
    dvl_test::MonadoQueue monado;
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(0, {monado.info}, {}, &device) == VK_SUCCESS);
    rig.destroy_device(device);

    const auto unknown_device = reinterpret_cast<VkDevice>(&dvl_test::g_devices[3]);
    rig.device_fn<PFN_vkDestroyDevice>(device, "vkDestroyDevice")(unknown_device, nullptr);
    CHECK(g_driver.last("vkDestroyDevice").handle == dvl_test::handle_value(unknown_device));

    int other_table = 0;
    dvl_test::Dispatchable other{&other_table};
    const auto unknown_instance = reinterpret_cast<VkInstance>(&other);
    rig.fn<PFN_vkDestroyInstance>("vkDestroyInstance")(unknown_instance, nullptr);
    CHECK(g_driver.last("vkDestroyInstance").handle == dvl_test::handle_value(unknown_instance));
}
