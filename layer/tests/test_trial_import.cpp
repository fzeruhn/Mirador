// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The trial import (design 6.3) and the import it runs (design 6.4), through the layer's chain to a fake driver that
// records every import call and can fail any of them, with the display session on a fake NVAPI.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstddef>
#include <string>
#include <vector>

#include "fakes/fake_chain.hpp"
#include "layer/import_rules.hpp"
#include "layer/surface.hpp"

namespace {

using dvl_test::ChainRig;
using dvl_test::FakeDriver;
using dvl_test::g_driver;

constexpr VkExternalMemoryHandleTypeFlagBits kKmt = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_KMT_BIT;
const char* const kFormatQuery = "vkGetPhysicalDeviceImageFormatProperties2KHR";

uint32_t display_count(ChainRig& rig, size_t gpu)
{
    uint32_t n = 0;
    const auto query = rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
    REQUIRE(query(ChainRig::gpu(gpu), &n, nullptr) == VK_SUCCESS);
    return n;
}

// A device as Monado's compute compositor makes it (the layer adds the import extensions).
VkDevice monado_device(ChainRig& rig, size_t gpu = 0)
{
    dvl_test::MonadoQueue monado;
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(gpu, {monado.info}, {"VK_KHR_swapchain"}, &device) == VK_SUCCESS);
    return device;
}

// Our display's mode, as an application finds it (the display must be listed).
VkDisplayModeKHR our_mode(ChainRig& rig)
{
    const auto query = rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
    uint32_t n = 1;
    VkDisplayPropertiesKHR display{};
    REQUIRE(query(ChainRig::gpu(0), &n, &display) == VK_SUCCESS);
    REQUIRE(n == 1);
    const auto modes = rig.fn<PFN_vkGetDisplayModePropertiesKHR>("vkGetDisplayModePropertiesKHR");
    VkDisplayModePropertiesKHR mode{};
    n = 1;
    REQUIRE(modes(ChainRig::gpu(0), display.display, &n, &mode) == VK_SUCCESS);
    return mode.displayMode;
}

VkResult create_surface(ChainRig& rig, VkDisplayModeKHR mode, VkSurfaceKHR* surface)
{
    VkDisplaySurfaceCreateInfoKHR ci{VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR};
    ci.displayMode = mode;
    ci.transform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    ci.globalAlpha = 1.0f;
    ci.alphaMode = VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR;
    ci.imageExtent = {7104, 3840};
    return rig.fn<PFN_vkCreateDisplayPlaneSurfaceKHR>("vkCreateDisplayPlaneSurfaceKHR")(rig.instance, &ci, nullptr,
                                                                                        surface);
}

// The usage our surface offers, from a surface on our mode.
VkImageUsageFlags surface_usage(ChainRig& rig)
{
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    REQUIRE(create_surface(rig, our_mode(rig), &surface) == VK_SUCCESS);
    VkSurfaceCapabilitiesKHR caps{};
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR>("vkGetPhysicalDeviceSurfaceCapabilitiesKHR")(
                ChainRig::gpu(0), surface, &caps) == VK_SUCCESS);
    rig.fn<PFN_vkDestroySurfaceKHR>("vkDestroySurfaceKHR")(rig.instance, surface, nullptr);
    return caps.supportedUsageFlags;
}

// The import calls of one configuration in the trial: three imports (design 6.4's four checks each), then the three
// images and their memory released, before the next configuration starts.
std::vector<std::string> one_config()
{
    std::vector<std::string> calls;
    for (int i = 0; i < 3; i++) {
        calls.insert(calls.end(), {kFormatQuery, "vkCreateImage", "vkGetImageMemoryRequirements",
                                   "vkGetMemoryWin32HandlePropertiesKHR", "vkAllocateMemory", "vkBindImageMemory"});
    }
    for (int i = 0; i < 3; i++) {
        calls.insert(calls.end(), {"vkDestroyImage", "vkFreeMemory"});
    }
    return calls;
}

// One device's trial: the compute configuration, then the graphics one.
std::vector<std::string> one_trial()
{
    std::vector<std::string> calls = one_config();
    const std::vector<std::string> second = one_config();
    calls.insert(calls.end(), second.begin(), second.end());
    return calls;
}

void check_query(const FakeDriver::FormatQuery& q, const dvl::ImportConfig& config)
{
    CHECK(q.format == config.format);
    CHECK(q.type == VK_IMAGE_TYPE_2D);
    CHECK(q.tiling == VK_IMAGE_TILING_OPTIMAL);
    CHECK(q.usage == config.usage);
    CHECK(q.flags == VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT);
    CHECK(q.external);
    CHECK(q.handle_type == kKmt);
    CHECK(q.format_list);
    CHECK(q.view_formats == *dvl::view_formats(config.format, config.usage));
}

void check_image(const FakeDriver::ImageMade& made, const dvl::ImportConfig& config, VkDevice device)
{
    CHECK(made.device == device);
    CHECK(made.info.sType == VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO);
    CHECK(made.info.flags == VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT);
    CHECK(made.info.imageType == VK_IMAGE_TYPE_2D);
    CHECK(made.info.format == config.format);
    CHECK(made.info.extent.width == 7104);
    CHECK(made.info.extent.height == 3840);
    CHECK(made.info.extent.depth == 1);
    CHECK(made.info.mipLevels == 1);
    CHECK(made.info.arrayLayers == 1);
    CHECK(made.info.samples == VK_SAMPLE_COUNT_1_BIT);
    CHECK(made.info.tiling == VK_IMAGE_TILING_OPTIMAL);
    CHECK(made.info.usage == config.usage);
    CHECK(made.info.sharingMode == VK_SHARING_MODE_EXCLUSIVE);
    CHECK(made.info.queueFamilyIndexCount == 0);
    CHECK(made.info.initialLayout == VK_IMAGE_LAYOUT_UNDEFINED);
    CHECK(made.external);
    CHECK(made.handle_types == static_cast<VkExternalMemoryHandleTypeFlags>(kKmt));
    CHECK(made.format_list);
    CHECK(made.view_formats == *dvl::view_formats(config.format, config.usage));
}

// Everything the trial made was released again.
void check_nothing_left()
{
    CHECK(g_driver.live_images.empty());
    CHECK(g_driver.live_memory.empty());
    size_t images = 0;
    for (const FakeDriver::ImageMade& made : g_driver.images_made) {
        images += made.image != VK_NULL_HANDLE ? 1 : 0;
    }
    size_t memory = 0;
    for (const FakeDriver::Allocation& a : g_driver.allocations) {
        memory += a.memory != VK_NULL_HANDLE ? 1 : 0;
    }
    CHECK(g_driver.images_destroyed == images);
    CHECK(g_driver.memory_freed == memory);
}

size_t images_on(VkDevice device)
{
    size_t n = 0;
    for (const FakeDriver::ImageMade& made : g_driver.images_made) {
        n += made.device == device ? 1 : 0;
    }
    return n;
}

} // namespace

TEST_CASE("trial_import_at_the_display_query_both_configs_three_buffers")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDevice device = monado_device(rig); // Monado's device exists before it asks for displays
    CHECK(g_driver.import_calls().empty());     // nothing at vkCreateDevice: NVAPI is not loaded yet

    CHECK(display_count(rig, 0) == 1);

    // The format queries for the surface's usage, then the trial on the device.
    std::vector<std::string> expected = {kFormatQuery, kFormatQuery};
    const std::vector<std::string> trial = one_trial();
    expected.insert(expected.end(), trial.begin(), trial.end());
    CHECK(g_driver.import_calls() == expected);

    // The format queries: design 6.4's first check, with the image's format, flags, usage and view formats.
    REQUIRE(g_driver.format_queries.size() == 8);
    check_query(g_driver.format_queries[0], dvl::kComputeConfig);
    check_query(g_driver.format_queries[1], dvl::kGraphicsConfig);
    for (size_t i = 0; i < 6; i++) {
        INFO("import " << i);
        check_query(g_driver.format_queries[2 + i], i < 3 ? dvl::kComputeConfig : dvl::kGraphicsConfig);
    }

    // Six images: the three back-buffers in each configuration.
    REQUIRE(g_driver.images_made.size() == 6);
    REQUIRE(g_driver.handle_queries.size() == 6);
    REQUIRE(g_driver.allocations.size() == 6);
    REQUIRE(g_driver.binds.size() == 6);
    for (size_t i = 0; i < 6; i++) {
        INFO("import " << i);
        const dvl::ImportConfig& config = i < 3 ? dvl::kComputeConfig : dvl::kGraphicsConfig;
        const HANDLE shared = rig.session->back_buffer(static_cast<uint32_t>(i % 3)).shared;
        const FakeDriver::ImageMade& made = g_driver.images_made[i];
        check_image(made, config, device);

        // The handle's memory types, then a dedicated import of the handle into a type both allow.
        CHECK(g_driver.handle_queries[i].type == kKmt);
        CHECK(g_driver.handle_queries[i].handle == shared);
        const FakeDriver::Allocation& a = g_driver.allocations[i];
        CHECK(a.imported);
        CHECK(a.handle_type == kKmt);
        CHECK(a.handle == shared);
        CHECK(a.name == nullptr);
        CHECK(a.dedicated);
        CHECK(a.dedicated_image == made.image);
        CHECK(a.dedicated_buffer == VK_NULL_HANDLE);
        CHECK(a.type == 2u); // the handle allows type 2 only; the image all three

        CHECK(g_driver.binds[i].image == made.image);
        CHECK(g_driver.binds[i].memory == a.memory);
        CHECK(g_driver.binds[i].offset == 0);
    }

    // Destroyed before the query returned.
    check_nothing_left();
    CHECK(g_driver.images_destroyed == 6);

    // The surface offers what the format queries allowed: both configurations' usage.
    CHECK(surface_usage(rig) == dvl::kSurfaceUsage);
    rig.destroy_device(device);
}

TEST_CASE("trial_import_runs_once_per_device")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDevice device = monado_device(rig);
    CHECK(display_count(rig, 0) == 1);
    g_driver.clear_calls();

    // Every later query in the instance, our surface, and the device's first swapchain: no import again.
    CHECK(display_count(rig, 0) == 1);
    const auto planes =
        rig.fn<PFN_vkGetPhysicalDeviceDisplayPlanePropertiesKHR>("vkGetPhysicalDeviceDisplayPlanePropertiesKHR");
    uint32_t n = 0;
    REQUIRE(planes(ChainRig::gpu(0), &n, nullptr) == VK_SUCCESS);
    CHECK(surface_usage(rig) == dvl::kSurfaceUsage);
    CHECK(rig.trial_import_at_swapchain(device) == 1);
    CHECK(rig.trial_import_at_swapchain(device) == 1);
    // trial_import() itself keeps its answer too.
    CHECK(rig.trial_import(device) == 1);
    CHECK(g_driver.import_calls().empty());
    rig.destroy_device(device);
}

TEST_CASE("trial_import_itself_runs_once")
{
    // Its first call on a device runs the trial; every later one gives that answer without a call, a failure too.
    ChainRig rig;
    REQUIRE(rig.ready());
    CHECK(display_count(rig, 0) == 1); // no device yet: nothing tried
    const VkDevice passing = monado_device(rig);
    const VkDevice failing = monado_device(rig);
    g_driver.clear_calls();
    CHECK(rig.trial_import(passing) == 1);
    CHECK(g_driver.import_calls() == one_trial());
    g_driver.fail_call("vkBindImageMemory", 7, VK_ERROR_OUT_OF_DEVICE_MEMORY); // the second device's first bind
    CHECK(rig.trial_import(failing) == 0);
    check_nothing_left();
    g_driver.clear_calls();
    CHECK(rig.trial_import(passing) == 1);
    CHECK(rig.trial_import(failing) == 0);
    CHECK(rig.trial_import_at_swapchain(failing) == 0);
    CHECK(g_driver.import_calls().empty());
    rig.destroy_device(passing);
    rig.destroy_device(failing);
}

TEST_CASE("trial_import_on_every_device_of_the_gpu")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDevice first = monado_device(rig);
    const VkDevice second = monado_device(rig);
    const VkDevice other_gpu = monado_device(rig, 1); // another NVIDIA adapter: not the display's
    const VkDevice intel = monado_device(rig, 2);

    CHECK(display_count(rig, 0) == 1);
    CHECK(g_driver.images_made.size() == 12);
    CHECK(images_on(first) == 6);
    CHECK(images_on(second) == 6);
    check_nothing_left();

    // The other GPUs never list the display, so nothing is imported there.
    g_driver.clear_calls();
    CHECK(display_count(rig, 1) == 0);
    CHECK(display_count(rig, 2) == 0);
    CHECK(g_driver.import_calls().empty());
    CHECK(images_on(other_gpu) == 0);
    CHECK(images_on(intel) == 0);

    for (const VkDevice device : {first, second, other_gpu, intel}) {
        rig.destroy_device(device);
    }
}

TEST_CASE("trial_import_failure_at_any_step_hides_the_display")
{
    struct Failure {
        const char* name;
        size_t nth; // counted from the first call of the decision
        VkResult result;
        const char* what;
    };
    // The decision's two format queries come first; the trial's imports follow (six calls each of the four checks,
    // the format queries numbered 3 to 8).
    const Failure failures[] = {
        {kFormatQuery, 3, VK_ERROR_FORMAT_NOT_SUPPORTED, "the first import's format query"},
        {kFormatQuery, 5, VK_ERROR_FORMAT_NOT_SUPPORTED, "the third compute import's format query"},
        {kFormatQuery, 8, VK_ERROR_OUT_OF_HOST_MEMORY, "the last import's format query"},
        {"vkCreateImage", 1, VK_ERROR_OUT_OF_DEVICE_MEMORY, "the first image"},
        {"vkCreateImage", 5, VK_ERROR_OUT_OF_DEVICE_MEMORY, "the second graphics image"},
        {"vkAllocateMemory", 3, VK_ERROR_INVALID_EXTERNAL_HANDLE, "the third compute import's memory"},
        {"vkAllocateMemory", 6, VK_ERROR_OUT_OF_DEVICE_MEMORY, "the last import's memory"},
        {"vkBindImageMemory", 1, VK_ERROR_OUT_OF_DEVICE_MEMORY, "the first bind"},
        {"vkBindImageMemory", 4, VK_ERROR_OUT_OF_DEVICE_MEMORY, "the first graphics bind"},
    };
    for (const Failure& f : failures) {
        INFO("failing " << f.what);
        ChainRig rig;
        REQUIRE(rig.ready());
        const VkDevice device = monado_device(rig);
        g_driver.fail_call(f.name, f.nth, f.result);

        CHECK(display_count(rig, 0) == 0); // the driver's displays only (none)
        CHECK(rig.session->is_open());     // the session is fine; the import is not
        check_nothing_left();

        // The answer is kept: no second trial, from a query or from the device's swapchain.
        const size_t calls = g_driver.import_calls().size();
        CHECK(display_count(rig, 0) == 0);
        CHECK(rig.trial_import_at_swapchain(device) == 0);
        CHECK(g_driver.import_calls().size() == calls);
        rig.destroy_device(device);
    }
}

TEST_CASE("trial_import_stops_at_the_first_failure")
{
    // The second compute image fails: the first one is released, and nothing more is tried.
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDevice device = monado_device(rig);
    g_driver.fail_call("vkCreateImage", 2, VK_ERROR_OUT_OF_DEVICE_MEMORY);
    CHECK(display_count(rig, 0) == 0);
    const std::vector<std::string> expected = {
        kFormatQuery,      kFormatQuery,      // the decision's
        kFormatQuery,      "vkCreateImage",   "vkGetImageMemoryRequirements",
        "vkGetMemoryWin32HandlePropertiesKHR", "vkAllocateMemory", "vkBindImageMemory", // back-buffer 0
        kFormatQuery,      "vkCreateImage",   // back-buffer 1 fails
        "vkDestroyImage",  "vkFreeMemory",    // back-buffer 0 released
    };
    CHECK(g_driver.import_calls() == expected);
    check_nothing_left();
    rig.destroy_device(device);
}

TEST_CASE("one_failing_device_hides_the_display_for_all")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDevice first = monado_device(rig);
    const VkDevice second = monado_device(rig);
    g_driver.fail_call("vkCreateImage", 7, VK_ERROR_OUT_OF_DEVICE_MEMORY); // the second device's first image
    CHECK(display_count(rig, 0) == 0);
    check_nothing_left();
    // Both devices had their trial at the decision: neither is tried again.
    const size_t calls = g_driver.import_calls().size();
    const int a = rig.trial_import_at_swapchain(first);
    const int b = rig.trial_import_at_swapchain(second);
    CHECK(a + b == 1); // one passed, one failed
    CHECK(g_driver.import_calls().size() == calls);
    rig.destroy_device(first);
    rig.destroy_device(second);
}

TEST_CASE("format_query_refused_at_the_decision_hides_the_display")
{
    for (size_t nth : {1, 2}) {
        INFO("format query " << nth << " (" << (nth == 1 ? "compute" : "graphics") << ") refused");
        ChainRig rig;
        REQUIRE(rig.ready());
        const VkDevice device = monado_device(rig);
        g_driver.fail_call(kFormatQuery, nth, VK_ERROR_FORMAT_NOT_SUPPORTED);
        CHECK(display_count(rig, 0) == 0);
        check_nothing_left();
        // The device had its trial at the decision all the same: not again at a swapchain.
        const size_t calls = g_driver.import_calls().size();
        rig.trial_import_at_swapchain(device);
        CHECK(g_driver.import_calls().size() == calls);
        rig.destroy_device(device);
    }
}

TEST_CASE("not_importable_hides_the_display")
{
    for (const VkFormat format : {VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB}) {
        INFO("format " << format);
        ChainRig rig;
        REQUIRE(rig.ready());
        const VkDevice device = monado_device(rig);
        g_driver.import_features[format] = VK_EXTERNAL_MEMORY_FEATURE_EXPORTABLE_BIT;
        CHECK(display_count(rig, 0) == 0);
        // The trial stopped at the query, before any image of that format.
        for (const FakeDriver::ImageMade& made : g_driver.images_made) {
            CHECK(made.info.format != format);
        }
        check_nothing_left();
        rig.destroy_device(device);
    }
}

TEST_CASE("dedicated_only_is_honoured")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDevice device = monado_device(rig);
    for (const VkFormat format : {VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB}) {
        g_driver.import_features[format] =
            VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT | VK_EXTERNAL_MEMORY_FEATURE_DEDICATED_ONLY_BIT;
    }
    CHECK(display_count(rig, 0) == 1);
    REQUIRE(g_driver.allocations.size() == 6);
    for (const FakeDriver::Allocation& a : g_driver.allocations) {
        CHECK(a.dedicated);
        CHECK(a.dedicated_image != VK_NULL_HANDLE);
    }
    rig.destroy_device(device);
}

TEST_CASE("memory_type_falls_back_to_the_image_bits")
{
    SECTION("vkGetMemoryWin32HandlePropertiesKHR fails")
    {
        ChainRig rig;
        REQUIRE(rig.ready());
        const VkDevice device = monado_device(rig);
        for (size_t nth = 1; nth <= 6; nth++) {
            g_driver.fail_call("vkGetMemoryWin32HandlePropertiesKHR", nth, VK_ERROR_INVALID_EXTERNAL_HANDLE);
        }
        CHECK(display_count(rig, 0) == 1);
        REQUIRE(g_driver.allocations.size() == 6);
        for (const FakeDriver::Allocation& a : g_driver.allocations) {
            CHECK(a.type == 1u); // the first device-local type among the image's
        }
        rig.destroy_device(device);
    }
    SECTION("the handle gives no bits")
    {
        ChainRig rig;
        REQUIRE(rig.ready());
        const VkDevice device = monado_device(rig);
        g_driver.handle_memory_bits = 0;
        CHECK(display_count(rig, 0) == 1);
        REQUIRE(g_driver.allocations.size() == 6);
        for (const FakeDriver::Allocation& a : g_driver.allocations) {
            CHECK(a.type == 1u);
        }
        rig.destroy_device(device);
    }
    SECTION("the handle and the image share no type")
    {
        ChainRig rig;
        REQUIRE(rig.ready());
        const VkDevice device = monado_device(rig);
        g_driver.handle_memory_bits = 0b001;
        g_driver.image_memory_bits = 0b110;
        CHECK(display_count(rig, 0) == 0);
        CHECK(g_driver.allocations.empty()); // no type to allocate with
        check_nothing_left();
        rig.destroy_device(device);
    }
}

TEST_CASE("device_without_the_import_extensions_fails_the_trial")
{
    std::vector<dvl_test::FakeGpu> gpus = dvl_test::default_gpus();
    std::vector<std::string>& ext = gpus[0].extensions;
    ext.erase(std::remove(ext.begin(), ext.end(), std::string("VK_KHR_external_memory_win32")), ext.end());
    ChainRig rig(gpus);
    REQUIRE(rig.ready());
    const VkDevice device = monado_device(rig);
    CHECK(display_count(rig, 0) == 0);
    for (const std::string& name : g_driver.import_calls()) {
        CHECK(name == kFormatQuery); // the decision's format queries; no image on that device
    }
    CHECK(g_driver.images_made.empty());
    rig.destroy_device(device);
}

TEST_CASE("no_device_at_the_display_query_defers_the_trial_to_the_swapchain")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    // No device yet (a caller other than Monado): listed, after the format queries alone.
    CHECK(display_count(rig, 0) == 1);
    CHECK(g_driver.import_calls() == std::vector<std::string>{kFormatQuery, kFormatQuery});
    CHECK(surface_usage(rig) == dvl::kSurfaceUsage);

    // A device made now gets its trial at its first vkCreateSwapchainKHR, once.
    const VkDevice late = monado_device(rig);
    g_driver.clear_calls();
    CHECK(rig.trial_import_at_swapchain(late) == 1);
    CHECK(g_driver.import_calls() == one_trial());
    CHECK(images_on(late) == 6);
    check_nothing_left();
    g_driver.clear_calls();
    CHECK(rig.trial_import_at_swapchain(late) == 1);
    CHECK(g_driver.import_calls().empty());

    // Another late device whose import fails: the swapchain is refused (a design 6.10 exception), once.
    const VkDevice failing = monado_device(rig);
    g_driver.fail_call("vkCreateImage", 7, VK_ERROR_OUT_OF_DEVICE_MEMORY);
    CHECK(rig.trial_import_at_swapchain(failing) == 0);
    check_nothing_left();
    g_driver.clear_calls();
    CHECK(rig.trial_import_at_swapchain(failing) == 0);
    CHECK(g_driver.import_calls().empty());
    // The display stays listed for the instance.
    CHECK(display_count(rig, 0) == 1);

    rig.destroy_device(late);
    rig.destroy_device(failing);
}

TEST_CASE("no_device_and_a_format_query_refused_hides_the_display")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    g_driver.import_features[VK_FORMAT_R8G8B8A8_SRGB] = VK_EXTERNAL_MEMORY_FEATURE_EXPORTABLE_BIT;
    CHECK(display_count(rig, 0) == 0);
    CHECK(g_driver.images_made.empty());
}

TEST_CASE("a_retry_trials_its_new_device_on_the_open_session")
{
    // Monado's retry (design 6.3): a new instance and device in a process whose display session is open.
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDevice first = monado_device(rig);
    CHECK(display_count(rig, 0) == 1);
    rig.destroy_device(first);
    rig.new_instance();
    REQUIRE(rig.ready());

    const VkDevice second = monado_device(rig);
    const size_t made = g_driver.images_made.size();
    const size_t api_calls = rig.api.calls().size();
    CHECK(display_count(rig, 0) == 1);
    CHECK(rig.api.calls().size() == api_calls); // the session was open: no NVAPI call
    CHECK(g_driver.images_made.size() == made + 6);
    CHECK(images_on(second) == 6);
    check_nothing_left();
    rig.destroy_device(second);
}

TEST_CASE("our_mode_in_an_instance_that_does_not_list_it_is_refused")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkDisplayModeKHR mode = our_mode(rig);
    rig.new_instance(); // has asked for no display yet: the surface has no usage to offer
    REQUIRE(rig.ready());
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    CHECK(create_surface(rig, mode, &surface) == VK_ERROR_UNKNOWN);
    CHECK(surface == VK_NULL_HANDLE);
}
