// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// mirador-display-check --vulkan (design 7), run for real against the test build of the layer DLL: a small stand-in for
// the Vulkan loader (below) hands the tool a vkGetInstanceProcAddr, puts the loader's link info in front of the layer
// the way the loader does, and lets through everything else to the layer, whose "next layer" is the fake driver and
// whose display session sits on a fake NVAPI with a running clock. So the tool's whole call sequence (instance, device
// before the first display query, display, plane, mode, surface, swapchain, clears with present-wait, a display event
// per frame, the flip counter, teardown) runs against the layer's real swapchain code and display thread. Nothing here
// reaches the real NVAPI, the real board, the real Vulkan loader or the real display session.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <fstream>
#include <string>
#include <vector>

#include "check/modes.hpp"
#include "check/options.hpp"
#include "check/report.hpp"
#include "fakes/fake_chain.hpp"

namespace {

namespace fake = dvl_test::fake;
using dvl_test::ChainRig;
using dvl_test::g_driver;

// --- A loader stand-in ------------------------------------------------------------------------------------------------

struct Barrier {
    VkImage image;
    VkImageLayout old_layout;
    VkImageLayout new_layout;
    VkPipelineStageFlags src_stage;
    VkPipelineStageFlags dst_stage;
};
struct Clear {
    VkImage image;
    VkImageLayout layout;
    float colour[4];
};

struct Shim {
    ChainRig* rig = nullptr;

    // What the loader offers and enumerates.
    std::vector<std::string> instance_extensions;
    bool layer_listed = true;
    std::vector<VkPhysicalDevice> physical_devices;
    bool present_features = true; // vkGetPhysicalDeviceFeatures2KHR says presentId and presentWait are there
    VkResult create_device_result = VK_SUCCESS;

    // What the tool did.
    size_t instances_created = 0;
    size_t instances_destroyed = 0;
    uint32_t api_version = 0;
    std::vector<std::string> enabled_instance_extensions;
    size_t devices_created = 0;
    size_t devices_destroyed = 0;
    std::vector<std::string> enabled_device_extensions;
    uint32_t queue_family = UINT32_MAX;
    bool feature_present_id = false;
    bool feature_present_wait = false;
    bool device_existed_at_first_display_query = false;
    size_t display_queries = 0;
    std::vector<Barrier> barriers;
    std::vector<Clear> clears;
    size_t command_pools_made = 0;
    size_t command_pools_destroyed = 0;
};
Shim g_shim;

template <size_t N>
void copy_name(char (&dst)[N], const std::string& name)
{
    std::memset(dst, 0, N);
    std::memcpy(dst, name.data(), std::min(name.size(), N - 1));
}

template <class Fn>
PFN_vkVoidFunction as_void(Fn fn)
{
    return reinterpret_cast<PFN_vkVoidFunction>(fn);
}

VkResult VKAPI_CALL shim_EnumerateInstanceExtensionProperties(const char*, uint32_t* count, VkExtensionProperties* out)
{
    std::vector<VkExtensionProperties> list;
    for (const std::string& name : g_shim.instance_extensions) {
        VkExtensionProperties p{};
        copy_name(p.extensionName, name);
        p.specVersion = 1;
        list.push_back(p);
    }
    return fake::write_list(count, out, list);
}

VkResult VKAPI_CALL shim_EnumerateInstanceLayerProperties(uint32_t* count, VkLayerProperties* out)
{
    std::vector<VkLayerProperties> list;
    if (g_shim.layer_listed) {
        VkLayerProperties p{};
        copy_name(p.layerName, dvl::kLayerName);
        p.specVersion = VK_API_VERSION_1_0;
        p.implementationVersion = 1;
        list.push_back(p);
    }
    return fake::write_list(count, out, list);
}

VkResult VKAPI_CALL shim_CreateInstance(const VkInstanceCreateInfo* ci, const VkAllocationCallbacks* alloc,
                                        VkInstance* out)
{
    ChainRig& rig = *g_shim.rig;
    if (rig.instance != VK_NULL_HANDLE) {
        // The rig made an instance of its own; the fake driver has one instance handle, so it goes first.
        rig.fn<PFN_vkDestroyInstance>("vkDestroyInstance")(rig.instance, nullptr);
        rig.instance = VK_NULL_HANDLE;
    }
    g_shim.api_version = ci->pApplicationInfo != nullptr ? ci->pApplicationInfo->apiVersion : 0;
    g_shim.enabled_instance_extensions.clear();
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) {
        g_shim.enabled_instance_extensions.emplace_back(ci->ppEnabledExtensionNames[i]);
    }
    VkLayerInstanceLink link{nullptr, fake::GetInstanceProcAddr, nullptr};
    VkLayerInstanceCreateInfo chain{};
    chain.sType = VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO;
    chain.function = VK_LAYER_LINK_INFO;
    chain.u.pLayerInfo = &link;
    chain.pNext = ci->pNext;
    VkInstanceCreateInfo down = *ci;
    down.pNext = &chain;
    const auto create = reinterpret_cast<PFN_vkCreateInstance>(rig.gipa(VK_NULL_HANDLE, "vkCreateInstance"));
    const VkResult r = create(&down, alloc, out);
    if (r == VK_SUCCESS) {
        rig.instance = *out;
        g_shim.instances_created++;
    }
    return r;
}

void VKAPI_CALL shim_DestroyInstance(VkInstance instance, const VkAllocationCallbacks* alloc)
{
    ChainRig& rig = *g_shim.rig;
    reinterpret_cast<PFN_vkDestroyInstance>(rig.gipa(instance, "vkDestroyInstance"))(instance, alloc);
    rig.instance = VK_NULL_HANDLE;
    g_shim.instances_destroyed++;
}

VkResult VKAPI_CALL shim_EnumeratePhysicalDevices(VkInstance, uint32_t* count, VkPhysicalDevice* out)
{
    return fake::write_list(count, out, g_shim.physical_devices);
}

void VKAPI_CALL shim_GetPhysicalDeviceFeatures2KHR(VkPhysicalDevice, VkPhysicalDeviceFeatures2KHR* features)
{
    for (auto* s = static_cast<VkBaseOutStructure*>(features->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR) {
            reinterpret_cast<VkPhysicalDevicePresentIdFeaturesKHR*>(s)->presentId = g_shim.present_features;
        } else if (s->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_WAIT_FEATURES_KHR) {
            reinterpret_cast<VkPhysicalDevicePresentWaitFeaturesKHR*>(s)->presentWait = g_shim.present_features;
        }
    }
}

// The first display query: was a device there already (Monado's order)?
VkResult VKAPI_CALL shim_GetPhysicalDeviceDisplayPropertiesKHR(VkPhysicalDevice gpu, uint32_t* count,
                                                               VkDisplayPropertiesKHR* out)
{
    if (g_shim.display_queries++ == 0) {
        g_shim.device_existed_at_first_display_query = g_shim.devices_created > g_shim.devices_destroyed;
    }
    ChainRig& rig = *g_shim.rig;
    return rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR")(gpu, count,
                                                                                                         out);
}

VkResult VKAPI_CALL shim_CreateDevice(VkPhysicalDevice gpu, const VkDeviceCreateInfo* ci,
                                      const VkAllocationCallbacks* alloc, VkDevice* out)
{
    g_shim.enabled_device_extensions.clear();
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) {
        g_shim.enabled_device_extensions.emplace_back(ci->ppEnabledExtensionNames[i]);
    }
    g_shim.queue_family = ci->queueCreateInfoCount > 0 ? ci->pQueueCreateInfos[0].queueFamilyIndex : UINT32_MAX;
    g_shim.feature_present_id = false;
    g_shim.feature_present_wait = false;
    for (auto* s = static_cast<const VkBaseInStructure*>(ci->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR) {
            g_shim.feature_present_id = reinterpret_cast<const VkPhysicalDevicePresentIdFeaturesKHR*>(s)->presentId;
        } else if (s->sType == VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_WAIT_FEATURES_KHR) {
            g_shim.feature_present_wait = reinterpret_cast<const VkPhysicalDevicePresentWaitFeaturesKHR*>(s)->presentWait;
        }
    }
    if (g_shim.create_device_result != VK_SUCCESS) {
        return g_shim.create_device_result;
    }
    // The loader puts its link and its loader-data callback in front of what the application chained.
    ChainRig& rig = *g_shim.rig;
    VkLayerDeviceLink link{nullptr, fake::GetInstanceProcAddr, fake::GetDeviceProcAddr};
    VkLayerDeviceCreateInfo callback{};
    callback.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
    callback.function = VK_LOADER_DATA_CALLBACK;
    callback.u.pfnSetDeviceLoaderData = fake::SetDeviceLoaderData;
    callback.pNext = ci->pNext;
    VkLayerDeviceCreateInfo chain{};
    chain.sType = VK_STRUCTURE_TYPE_LOADER_DEVICE_CREATE_INFO;
    chain.function = VK_LAYER_LINK_INFO;
    chain.u.pLayerInfo = &link;
    chain.pNext = &callback;
    VkDeviceCreateInfo down = *ci;
    down.pNext = &chain;
    const VkResult r = rig.fn<PFN_vkCreateDevice>("vkCreateDevice")(gpu, &down, alloc, out);
    if (r == VK_SUCCESS) {
        g_shim.devices_created++;
    }
    return r;
}

// Device commands the fake driver does not have: the tool's semaphores and command buffers. Nothing runs on a GPU, so
// they only record what the tool asked for.
VkResult VKAPI_CALL stub_CreateSemaphore(VkDevice, const VkSemaphoreCreateInfo*, const VkAllocationCallbacks*,
                                         VkSemaphore* out)
{
    static uintptr_t n = 0x5E3A1000;
    *out = reinterpret_cast<VkSemaphore>(++n);
    return VK_SUCCESS;
}
void VKAPI_CALL stub_DestroySemaphore(VkDevice, VkSemaphore, const VkAllocationCallbacks*) {}
VkResult VKAPI_CALL stub_CreateCommandPool(VkDevice, const VkCommandPoolCreateInfo*, const VkAllocationCallbacks*,
                                           VkCommandPool* out)
{
    g_shim.command_pools_made++;
    *out = reinterpret_cast<VkCommandPool>(static_cast<uintptr_t>(0xC0DE0001));
    return VK_SUCCESS;
}
void VKAPI_CALL stub_DestroyCommandPool(VkDevice, VkCommandPool, const VkAllocationCallbacks*)
{
    g_shim.command_pools_destroyed++;
}
VkResult VKAPI_CALL stub_AllocateCommandBuffers(VkDevice, const VkCommandBufferAllocateInfo* info, VkCommandBuffer* out)
{
    static int table;
    static dvl_test::Dispatchable buffers[8] = {{&table}, {&table}, {&table}, {&table},
                                                {&table}, {&table}, {&table}, {&table}};
    for (uint32_t i = 0; i < info->commandBufferCount && i < 8; i++) {
        out[i] = reinterpret_cast<VkCommandBuffer>(&buffers[i]);
    }
    return VK_SUCCESS;
}
VkResult VKAPI_CALL stub_ResetCommandBuffer(VkCommandBuffer, VkCommandBufferResetFlags)
{
    return VK_SUCCESS;
}
VkResult VKAPI_CALL stub_BeginCommandBuffer(VkCommandBuffer, const VkCommandBufferBeginInfo*)
{
    return VK_SUCCESS;
}
VkResult VKAPI_CALL stub_EndCommandBuffer(VkCommandBuffer)
{
    return VK_SUCCESS;
}
void VKAPI_CALL stub_CmdPipelineBarrier(VkCommandBuffer, VkPipelineStageFlags src, VkPipelineStageFlags dst,
                                        VkDependencyFlags, uint32_t, const VkMemoryBarrier*, uint32_t,
                                        const VkBufferMemoryBarrier*, uint32_t image_barriers,
                                        const VkImageMemoryBarrier* barriers)
{
    for (uint32_t i = 0; i < image_barriers; i++) {
        g_shim.barriers.push_back(Barrier{barriers[i].image, barriers[i].oldLayout, barriers[i].newLayout, src, dst});
    }
}
void VKAPI_CALL stub_CmdClearColorImage(VkCommandBuffer, VkImage image, VkImageLayout layout,
                                        const VkClearColorValue* colour, uint32_t, const VkImageSubresourceRange*)
{
    Clear c{image, layout, {}};
    std::memcpy(c.colour, colour->float32, sizeof(c.colour));
    g_shim.clears.push_back(c);
}

// vkGetDeviceQueue as the loader's trampoline leaves it: the queue has its device's dispatch table.
void VKAPI_CALL shim_GetDeviceQueue(VkDevice device, uint32_t family, uint32_t index, VkQueue* out)
{
    *out = g_shim.rig->get_queue(device, family, index);
}

void VKAPI_CALL shim_DestroyDevice(VkDevice device, const VkAllocationCallbacks* alloc)
{
    g_shim.rig->destroy_device(device);
    (void)alloc;
    g_shim.devices_destroyed++;
}

PFN_vkVoidFunction VKAPI_CALL shim_gdpa(VkDevice device, const char* name)
{
    struct Entry {
        const char* name;
        PFN_vkVoidFunction fn;
    };
    static const Entry table[] = {
        {"vkGetDeviceQueue", as_void(shim_GetDeviceQueue)},
        {"vkDestroyDevice", as_void(shim_DestroyDevice)},
        {"vkCreateSemaphore", as_void(stub_CreateSemaphore)},
        {"vkDestroySemaphore", as_void(stub_DestroySemaphore)},
        {"vkCreateCommandPool", as_void(stub_CreateCommandPool)},
        {"vkDestroyCommandPool", as_void(stub_DestroyCommandPool)},
        {"vkAllocateCommandBuffers", as_void(stub_AllocateCommandBuffers)},
        {"vkResetCommandBuffer", as_void(stub_ResetCommandBuffer)},
        {"vkBeginCommandBuffer", as_void(stub_BeginCommandBuffer)},
        {"vkEndCommandBuffer", as_void(stub_EndCommandBuffer)},
        {"vkCmdPipelineBarrier", as_void(stub_CmdPipelineBarrier)},
        {"vkCmdClearColorImage", as_void(stub_CmdClearColorImage)},
    };
    for (const Entry& e : table) {
        if (std::strcmp(e.name, name) == 0) {
            return e.fn;
        }
    }
    return g_shim.rig->gdpa(device, name);
}

PFN_vkVoidFunction VKAPI_CALL shim_gipa(VkInstance instance, const char* name)
{
    auto is = [&](const char* wanted) { return std::strcmp(name, wanted) == 0; };
    if (instance == VK_NULL_HANDLE) {
        if (is("vkEnumerateInstanceExtensionProperties")) {
            return as_void(shim_EnumerateInstanceExtensionProperties);
        }
        if (is("vkEnumerateInstanceLayerProperties")) {
            return as_void(shim_EnumerateInstanceLayerProperties);
        }
        if (is("vkCreateInstance")) {
            return as_void(shim_CreateInstance);
        }
        return nullptr;
    }
    if (is("vkDestroyInstance")) {
        return as_void(shim_DestroyInstance);
    }
    if (is("vkEnumeratePhysicalDevices")) {
        return as_void(shim_EnumeratePhysicalDevices);
    }
    if (is("vkGetPhysicalDeviceFeatures2KHR")) {
        return as_void(shim_GetPhysicalDeviceFeatures2KHR);
    }
    if (is("vkGetPhysicalDeviceDisplayPropertiesKHR")) {
        return as_void(shim_GetPhysicalDeviceDisplayPropertiesKHR);
    }
    if (is("vkCreateDevice")) {
        return as_void(shim_CreateDevice);
    }
    if (is("vkGetDeviceProcAddr")) {
        return as_void(shim_gdpa);
    }
    return g_shim.rig->gipa(instance, name);
}

// --- The rig ----------------------------------------------------------------------------------------------------------

std::vector<std::string> all_instance_extensions()
{
    return {"VK_EXT_debug_report",
            "VK_KHR_external_fence_capabilities",
            "VK_KHR_external_memory_capabilities",
            "VK_KHR_external_semaphore_capabilities",
            "VK_KHR_get_physical_device_properties2",
            "VK_KHR_get_surface_capabilities2",
            "VK_KHR_surface",
            "VK_KHR_display",
            "VK_EXT_display_surface_counter"};
}

struct Check {
    ChainRig rig{dvl_test::default_gpus(), true};
    dvl::CaptureOutput out;
    dvl::VulkanEnv env;
    dvl::Options options;

    explicit Check(int seconds = 2)
    {
        REQUIRE(rig.ready());
        g_shim = Shim{};
        g_shim.rig = &rig;
        g_shim.instance_extensions = all_instance_extensions();
        g_shim.physical_devices = {ChainRig::gpu(0)};
        env.gipa = shim_gipa;
        options.vulkan = true;
        options.seconds = seconds;
    }
    ~Check() { g_shim.rig = nullptr; }

    dvl::ModeResult run() { return dvl::run_vulkan_check(env, options, out); }

    // The index of the first output line that contains `part`, or -1.
    int line_of(const std::string& part) const
    {
        const std::vector<std::string> lines = out.lines();
        for (size_t i = 0; i < lines.size(); i++) {
            if (lines[i].find(part) != std::string::npos) {
                return static_cast<int>(i);
            }
        }
        return -1;
    }
};

bool has(const std::vector<std::string>& list, const char* name)
{
    return std::find(list.begin(), list.end(), name) != list.end();
}

} // namespace

TEST_CASE("vulkan_mode_follows_monados_order_and_passes_against_the_fake_chain")
{
    Check c(2);
    const dvl::ModeResult result = c.run();
    INFO(c.out.text());
    REQUIRE(result.steps_ok);

    // 2 s at 90 Hz: 180 presents and flips, counted over the same window.
    const uint64_t expected = dvl::expected_flips(result.counts.seconds);
    CHECK(result.counts.seconds >= 1.99);
    CHECK(result.counts.presents >= expected * 9 / 10);
    CHECK(result.counts.presents <= expected * 11 / 10);
    CHECK(result.counts.flips >= expected * 9 / 10);
    CHECK(result.counts.flips <= expected * 11 / 10);
    CHECK(result.counts.failures == 0);
    CHECK(result.counts.have_events);
    CHECK(result.counts.events_timeout == 0);
    // One event per present but the first.
    CHECK(result.counts.events_ok + 3 >= result.counts.presents);
    CHECK(result.counts.events_ok <= result.counts.presents);
    const dvl::Verdict verdict = dvl::judge(result.steps_ok, result.failed_step, result.counts);
    INFO(verdict.line);
    CHECK(verdict.pass);

    // The instance: API 1.0, with Monado's extensions (COMP_INSTANCE_EXTENSIONS_COMMON, VK_KHR_display and the layer's
    // VK_EXT_display_surface_counter).
    CHECK(g_shim.api_version == VK_API_VERSION_1_0);
    for (const char* name : {"VK_EXT_debug_report", "VK_KHR_external_fence_capabilities",
                             "VK_KHR_external_memory_capabilities", "VK_KHR_external_semaphore_capabilities",
                             "VK_KHR_get_physical_device_properties2", "VK_KHR_surface", "VK_KHR_display",
                             "VK_EXT_display_surface_counter", "VK_KHR_get_surface_capabilities2"}) {
        INFO(name);
        CHECK(has(g_shim.enabled_instance_extensions, name));
    }

    // The device comes BEFORE the first display query, with VK_EXT_display_control and present id/wait, on the compute
    // family (Monado's compute compositor), not the graphics one.
    CHECK(g_shim.device_existed_at_first_display_query);
    CHECK(g_shim.enabled_device_extensions ==
          std::vector<std::string>{"VK_KHR_swapchain", "VK_KHR_present_id", "VK_KHR_present_wait",
                                   "VK_EXT_display_control"});
    CHECK(g_shim.feature_present_id);
    CHECK(g_shim.feature_present_wait);
    CHECK(g_shim.queue_family == 2);
    CHECK(c.line_of("device: created") >= 0);
    CHECK(c.line_of("device: created") < c.line_of("display query: the layer opens"));

    // Display, plane, mode, surface and swapchain each reported.
    for (const char* step : {"display 0:", "plane:", "mode: 7104x3840 at 90.000 Hz", "surface: created",
                             "surface queries:", "swapchain: created, 3 images"}) {
        INFO(step);
        CHECK(c.line_of(step) >= 0);
    }
    CHECK(c.line_of("display 0: Pimax Dream Air") >= 0);

    // Every frame cleared one of the swapchain's three images and ended in PRESENT_SRC_KHR.
    REQUIRE(g_shim.clears.size() >= 100);
    std::vector<VkImage> images;
    for (const Clear& clear : g_shim.clears) {
        CHECK(clear.layout == VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
        if (std::find(images.begin(), images.end(), clear.image) == images.end()) {
            images.push_back(clear.image);
        }
    }
    CHECK(images.size() == 3);
    CHECK(g_shim.clears.front().colour[0] == 1.0f); // red in the first second
    CHECK(g_shim.clears.front().colour[1] == 0.0f);
    REQUIRE(g_shim.barriers.size() == 2 * g_shim.clears.size());
    for (size_t i = 0; i < g_shim.barriers.size(); i += 2) {
        CHECK(g_shim.barriers[i].old_layout == VK_IMAGE_LAYOUT_UNDEFINED);
        CHECK(g_shim.barriers[i].new_layout == VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
        // The layout change waits for the acquire semaphore, which the submit waits on at TRANSFER (Task 12 review).
        CHECK(g_shim.barriers[i].src_stage == VK_PIPELINE_STAGE_TRANSFER_BIT);
        CHECK(g_shim.barriers[i].dst_stage == VK_PIPELINE_STAGE_TRANSFER_BIT);
        CHECK(g_shim.barriers[i + 1].old_layout == VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
        CHECK(g_shim.barriers[i + 1].new_layout == VK_IMAGE_LAYOUT_PRESENT_SRC_KHR);
        CHECK(g_shim.barriers[i + 1].src_stage == VK_PIPELINE_STAGE_TRANSFER_BIT);
        CHECK(g_shim.barriers[i + 1].image == g_shim.clears[i / 2].image);
    }

    // The fake NVAPI got the Presents the layer's display thread made.
    CHECK(c.rig.api.count("present") >= expected * 9 / 10);

    // Teardown, in Monado's order, left nothing behind.
    CHECK(g_shim.instances_created == 1);
    CHECK(g_shim.instances_destroyed == 1);
    CHECK(g_shim.devices_created == 1);
    CHECK(g_shim.devices_destroyed == 1);
    CHECK(g_shim.command_pools_made == 1);
    CHECK(g_shim.command_pools_destroyed == 1);
    CHECK(g_driver.live_images.empty());
    CHECK(g_driver.live_memory.empty());
    CHECK(g_driver.live_fences() == 0);
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("vulkan_mode_reports_what_the_layers_log_says_about_the_edid_route_and_the_import")
{
    // The lines as the layer stamps them in this process (the layer runs in the tool's process).
    const std::string pid = "[pid " + std::to_string(GetCurrentProcessId()) + " tid 12]";
    {
        Check c(1);
        c.env.layer_log = [pid] {
            return std::optional<std::string>(
                "2026-10-31 10:00:01.100 " + pid + " info  EDID route: registry\n" + "2026-10-31 10:00:01.900 " +
                pid +
                " info  trial import on device 0000: the 3 back-buffers import as R8G8B8A8_UNORM and as "
                "R8G8B8A8_SRGB (imported and released)\n" +
                "2026-10-31 10:00:02.000 " + pid +
                " warn  import on device 0000: vkGetMemoryWin32HandlePropertiesKHR failed for back-buffer 0001, so the "
                "memory type comes from the image's memoryTypeBits (0x7) alone, as Monado's target did\n");
        };
        const dvl::ModeResult result = c.run();
        INFO(c.out.text());
        REQUIRE(result.steps_ok);
        CHECK(c.line_of("EDID route (from the layer's log): registry") >= 0);
        CHECK(c.line_of("back-buffer import (from the layer's log): 2026-10-31 10:00:01.900 " + pid +
                        " info  trial import on device") >= 0);
        CHECK(c.line_of("memory-type fallback (from the layer's log): 2026-10-31 10:00:02.000 " + pid + " warn") >= 0);
    }
    {
        // A log without the lines, and no log at all.
        Check none(1);
        none.env.layer_log = [pid] { return std::optional<std::string>(pid + " nothing of interest\n"); };
        REQUIRE(none.run().steps_ok);
        CHECK(none.line_of("EDID route (from the layer's log): not found in this run's log") >= 0);
        CHECK(none.line_of("no 'trial import on device' line in this run's log") >= 0);
        CHECK(none.line_of("memory-type fallback (from the layer's log): none") >= 0);
    }
    {
        Check unreadable(1);
        unreadable.env.layer_log = [] { return std::optional<std::string>(); };
        REQUIRE(unreadable.run().steps_ok);
        CHECK(unreadable.line_of("layer log: not readable") >= 0);
    }
}

TEST_CASE("vulkan_mode_never_reports_another_processs_lines_of_the_layers_log")
{
    // layer.log holds an older process's lines (this run's layer wrote none, or another process rotated the log):
    // none of them is printed as this run's (Task 12 review).
    wchar_t dir[MAX_PATH];
    REQUIRE(GetTempPathW(MAX_PATH, dir) != 0);
    const std::wstring path = std::wstring(dir) + L"dvl_test_layer_" + std::to_wstring(GetCurrentProcessId()) + L".log";
    const unsigned long other = GetCurrentProcessId() + 4;
    {
        std::ofstream file(path, std::ios::binary | std::ios::trunc);
        REQUIRE(file);
        file << "2026-10-30 22:00:00.000 [pid " << other << " tid 3] info  EDID route: NVAPI\r\n"
             << "2026-10-30 22:00:01.000 [pid " << other << " tid 3] info  trial import on device 0000: ok\r\n"
             << "2026-10-30 22:00:02.000 [pid " << other << " tid 3] warn  import on device 0000: ... so the memory "
             << "type comes from the image's memoryTypeBits (0x7) alone\r\n";
    }
    struct Remove {
        std::wstring path;
        ~Remove() { DeleteFileW(path.c_str()); }
    } remove{path};
    REQUIRE(dvl::read_layer_log_file(path).has_value());

    Check c(1);
    c.env.layer_log = [path] { return dvl::read_layer_log_file(path); };
    const dvl::ModeResult result = c.run();
    INFO(c.out.text());
    REQUIRE(result.steps_ok);
    CHECK(c.line_of("EDID route (from the layer's log): not found in this run's log") >= 0);
    CHECK(c.line_of("back-buffer import (from the layer's log): no 'trial import on device' line in this run's log") >=
          0);
    CHECK(c.line_of("memory-type fallback (from the layer's log): none") >= 0);
    CHECK(c.line_of("EDID route (from the layer's log): NVAPI") < 0);
    CHECK(c.line_of("[pid " + std::to_string(other)) < 0);
}

TEST_CASE("vulkan_mode_tries_the_next_nvidia_gpu_when_the_first_has_no_display")
{
    // Gpu 1 is another NVIDIA GPU (the display is not on its adapter), gpu 2 an Intel one, gpu 0 drives the headset.
    Check c(1);
    g_shim.physical_devices = {ChainRig::gpu(1), ChainRig::gpu(2), ChainRig::gpu(0)};
    const dvl::ModeResult result = c.run();
    INFO(c.out.text());
    REQUIRE(result.steps_ok);
    CHECK(g_shim.devices_created == 2); // one on gpu 1, destroyed again, and the one that was used
    CHECK(g_shim.devices_destroyed == 2);
    CHECK(g_shim.display_queries >= 2);
    CHECK(c.line_of("no display listed") >= 0);
    CHECK(g_shim.instances_destroyed == 1);
}

TEST_CASE("vulkan_mode_names_a_missing_layer_and_makes_no_instance")
{
    {
        Check c(1);
        g_shim.layer_listed = false;
        g_shim.instance_extensions.erase(
            std::remove(g_shim.instance_extensions.begin(), g_shim.instance_extensions.end(),
                        std::string("VK_EXT_display_surface_counter")),
            g_shim.instance_extensions.end());
        const dvl::ModeResult result = c.run();
        CHECK_FALSE(result.steps_ok);
        CHECK(result.failed_step.find("layer is not active") != std::string::npos);
        CHECK(result.failed_step.find("VK_EXT_display_surface_counter") != std::string::npos);
        CHECK(c.line_of("VK_IMPLICIT_LAYER_PATH") >= 0);
        CHECK(c.line_of("NOT registered") >= 0);
        CHECK(g_shim.instances_created == 0);
        CHECK(c.line_of("elevated") < 0); // not said when it is not so
    }
    {
        // Elevated: the advice says why the path variable did not work.
        Check elevated(1);
        elevated.env.elevated = true;
        g_shim.layer_listed = false;
        g_shim.instance_extensions = {"VK_KHR_surface"};
        CHECK_FALSE(elevated.run().steps_ok);
        CHECK(elevated.line_of("elevated") >= 0);
    }
}

TEST_CASE("vulkan_mode_names_a_missing_loader_extension")
{
    Check c(1);
    g_shim.instance_extensions.erase(
        std::remove(g_shim.instance_extensions.begin(), g_shim.instance_extensions.end(),
                    std::string("VK_KHR_display")),
        g_shim.instance_extensions.end());
    const dvl::ModeResult result = c.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("VK_KHR_display") != std::string::npos);
    CHECK(result.failed_step.find("layer is not active") == std::string::npos); // the layer's own one is there
    CHECK(g_shim.instances_created == 0);
}

TEST_CASE("vulkan_mode_fails_cleanly_when_the_layer_lists_no_display")
{
    // Pimax running: the layer's holder check fails the session, so no display is listed on any GPU.
    Check c(1);
    c.rig.running = {"pi_server.exe"};
    g_shim.physical_devices = {ChainRig::gpu(0), ChainRig::gpu(1)};
    const dvl::ModeResult result = c.run();
    INFO(c.out.text());
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("display") != std::string::npos);
    CHECK(result.failed_step.find("layer.log") != std::string::npos);
    CHECK(g_shim.devices_created == 2);
    CHECK(g_shim.devices_destroyed == 2); // none left behind
    CHECK(g_shim.instances_destroyed == 1);
    const dvl::Verdict verdict = dvl::judge(result.steps_ok, result.failed_step, result.counts);
    CHECK_FALSE(verdict.pass);
    CHECK(verdict.line.rfind("FAIL: display", 0) == 0);
}

TEST_CASE("vulkan_mode_prints_the_layers_failure_line_and_what_to_do_when_no_display_is_listed")
{
    // The session's failure line is in the layer's log as an error line of this process; the tool shows it and
    // explains it, as the NVAPI mode does with the session's result.
    const std::string pid = "[pid " + std::to_string(GetCurrentProcessId()) + " tid 12]";
    Check c(1);
    c.rig.api.edid.clear(); // no EDID, and the rig's session finds no board: the display is not listed
    c.env.layer_log = [pid] {
        return std::optional<std::string>("2026-10-31 10:00:01.100 " + pid +
                                          " error Product: the display's EDID could not be read (NVAPI, then the "
                                          "registry), and the Dream Air's board (USB 34A4:0044) is not connected "
                                          "(code -10003)\n");
    };
    const dvl::ModeResult result = c.run();
    INFO(c.out.text());
    CHECK_FALSE(result.steps_ok);
    CHECK(c.line_of("the layer's failure line (from its log): Product: the display's EDID could not be read") >= 0);
    CHECK(c.line_of("what it means: ") >= 0);
    CHECK(c.line_of("what to do: ") >= 0);
}

TEST_CASE("vulkan_mode_fails_cleanly_when_the_machine_has_no_nvidia_gpu")
{
    Check c(1);
    g_shim.physical_devices = {ChainRig::gpu(2)};
    const dvl::ModeResult result = c.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("no NVIDIA GPU") != std::string::npos);
    CHECK(g_shim.devices_created == 0);
    CHECK(g_shim.instances_destroyed == 1);
}

TEST_CASE("vulkan_mode_reports_a_refused_device_with_what_it_asked_for")
{
    Check c(1);
    g_shim.create_device_result = VK_ERROR_EXTENSION_NOT_PRESENT;
    const dvl::ModeResult result = c.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("vkCreateDevice") != std::string::npos);
    CHECK(result.failed_step.find("VK_ERROR_EXTENSION_NOT_PRESENT") != std::string::npos);
    CHECK(result.failed_step.find("VK_EXT_display_control") != std::string::npos);
    CHECK(g_shim.instances_destroyed == 1);
}

TEST_CASE("vulkan_mode_needs_the_present_id_and_present_wait_features")
{
    Check c(1);
    g_shim.present_features = false;
    const dvl::ModeResult result = c.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("presentId") != std::string::npos);
    CHECK(g_shim.devices_created == 0); // refused before a device was made
    CHECK(g_shim.instances_destroyed == 1);
}

TEST_CASE("vulkan_mode_stops_when_interrupted_and_still_tears_down")
{
    Check c(20);
    int polls = 0;
    c.env.interrupted = [&polls] { return ++polls > 30; };
    const dvl::ModeResult result = c.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step == "interrupted");
    CHECK(result.counts.seconds < 5.0);
    CHECK(g_shim.devices_destroyed == 1);
    CHECK(g_shim.instances_destroyed == 1);
    CHECK(g_driver.live_images.empty());
}

TEST_CASE("vulkan_mode_ends_with_a_failing_verdict_instead_of_hanging_when_the_display_is_not_flipping")
{
    Check c(2);
    c.rig.api.stop_counter(); // the link is dead: the counter never moves
    const auto started = std::chrono::steady_clock::now();
    const dvl::ModeResult result = c.run();
    const double took = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    INFO(c.out.text());
    INFO("took " << took << " s");
    // The run, the present-waits and the display events each have bounds of their own: it ends, and not as a pass.
    CHECK(took < 20.0);
    const dvl::Verdict verdict = dvl::judge(result.steps_ok, result.failed_step, result.counts);
    CHECK_FALSE(verdict.pass);
    CHECK(result.counts.flips == 0);
    CHECK(result.counts.presents < 2000); // stopped well before the clears hammered the GPU for the whole run
    CHECK(result.failed_step.find("not flipping") != std::string::npos);
    // Teardown still ran in full.
    CHECK(g_shim.devices_destroyed == 1);
    CHECK(g_shim.instances_destroyed == 1);
    CHECK(g_driver.live_images.empty());
}
