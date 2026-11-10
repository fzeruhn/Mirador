// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The surface on the layer's display (design 6.4): the answers alone, then the surface commands through the layer's
// chain to a fake driver. No surface query on our surface fails or reaches the driver; any other surface (Monado's
// peek window) goes down unchanged.

#include <catch2/catch_test_macros.hpp>

#include <vulkan/vk_icd.h>

#include <algorithm>
#include <string>
#include <vector>

#include "fakes/fake_chain.hpp"
#include "layer/surface.hpp"

namespace {

using dvl_test::ChainRig;
using dvl_test::g_driver;

const dvl::SurfaceSpec kSpec{{7104, 3840}, dvl::kSurfaceUsage};

bool has_mode(const std::vector<VkPresentModeKHR>& modes, VkPresentModeKHR mode)
{
    return std::find(modes.begin(), modes.end(), mode) != modes.end();
}

VkSwapchainCreateInfoKHR swapchain_info(VkFormat format, VkImageUsageFlags usage)
{
    VkSwapchainCreateInfoKHR ci{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
    ci.minImageCount = 3;
    ci.imageFormat = format;
    ci.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
    ci.imageExtent = {7104, 3840};
    ci.imageArrayLayers = 1;
    ci.imageUsage = usage;
    ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    ci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    ci.presentMode = VK_PRESENT_MODE_FIFO_KHR;
    return ci;
}

// The layer's surface on its display's mode, plane 0, with Monado's GLOBAL alpha.
VkSurfaceKHR make_our_surface(ChainRig& rig)
{
    const auto query = rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
    uint32_t n = 1;
    VkDisplayPropertiesKHR display{};
    REQUIRE(query(ChainRig::gpu(0), &n, &display) == VK_SUCCESS);
    const auto modes = rig.fn<PFN_vkGetDisplayModePropertiesKHR>("vkGetDisplayModePropertiesKHR");
    VkDisplayModePropertiesKHR mode{};
    n = 1;
    REQUIRE(modes(ChainRig::gpu(0), display.display, &n, &mode) == VK_SUCCESS);

    VkDisplaySurfaceCreateInfoKHR ci{VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR};
    ci.displayMode = mode.displayMode;
    ci.planeIndex = 0;
    ci.planeStackIndex = 0;
    ci.transform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    ci.globalAlpha = 1.0f;
    ci.alphaMode = VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR;
    ci.imageExtent = {7104, 3840};
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    const auto create = rig.fn<PFN_vkCreateDisplayPlaneSurfaceKHR>("vkCreateDisplayPlaneSurfaceKHR");
    REQUIRE(create(rig.instance, &ci, nullptr, &surface) == VK_SUCCESS);
    REQUIRE(surface != VK_NULL_HANDLE);
    return surface;
}

void check_our_caps(const VkSurfaceCapabilitiesKHR& c)
{
    CHECK(c.minImageCount == 3);
    CHECK(c.maxImageCount == 3);
    CHECK(c.currentExtent.width == 7104);
    CHECK(c.currentExtent.height == 3840);
    CHECK(c.minImageExtent.width == 7104);
    CHECK(c.maxImageExtent.height == 3840);
    CHECK(c.maxImageArrayLayers == 1);
    CHECK(c.supportedTransforms == VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR);
    CHECK(c.currentTransform == VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR);
    CHECK(c.supportedCompositeAlpha == VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR);
    CHECK((c.supportedUsageFlags & VK_IMAGE_USAGE_STORAGE_BIT) != 0);
}

// A capability struct the layer does not know: sType, pNext, one VkBool32.
struct PresentFlag {
    VkStructureType sType;
    void* pNext;
    VkBool32 supported;
};

} // namespace

TEST_CASE("surface_caps_values")
{
    const VkSurfaceCapabilitiesKHR c = dvl::surface_capabilities(kSpec);
    check_our_caps(c);
    CHECK(c.minImageExtent.height == 3840);
    CHECK(c.maxImageExtent.width == 7104);
    CHECK(c.supportedUsageFlags == dvl::kSurfaceUsage);
    // The usage comes from the spec (Task 10 gives the trial import's).
    CHECK(dvl::surface_capabilities({{7104, 3840}, VK_IMAGE_USAGE_STORAGE_BIT}).supportedUsageFlags ==
          VK_IMAGE_USAGE_STORAGE_BIT);

    VkSurfaceCapabilities2EXT ext{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_2_EXT};
    int marker = 0;
    ext.pNext = &marker;
    dvl::fill_surface_capabilities_2ext(kSpec, ext);
    CHECK(ext.sType == VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_2_EXT);
    CHECK(ext.pNext == &marker);
    CHECK(ext.minImageCount == 3);
    CHECK(ext.maxImageCount == 3);
    CHECK(ext.currentExtent.width == 7104);
    CHECK(ext.maxImageArrayLayers == 1);
    CHECK(ext.supportedCompositeAlpha == VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR);
    CHECK(ext.supportedUsageFlags == dvl::kSurfaceUsage);
    CHECK(ext.supportedSurfaceCounters == VK_SURFACE_COUNTER_VBLANK_BIT_EXT);
}

TEST_CASE("formats_unorm_and_srgb")
{
    const std::vector<VkSurfaceFormatKHR> formats = dvl::surface_formats();
    REQUIRE(formats.size() == 2);
    CHECK(formats[0].format == VK_FORMAT_R8G8B8A8_UNORM);
    CHECK(formats[0].colorSpace == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR);
    CHECK(formats[1].format == VK_FORMAT_R8G8B8A8_SRGB);
    CHECK(formats[1].colorSpace == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR);
}

TEST_CASE("present_modes_include_fifo_mailbox_immediate_relaxed")
{
    const std::vector<VkPresentModeKHR> modes = dvl::present_modes();
    CHECK(modes.size() == 4);
    CHECK(modes[0] == VK_PRESENT_MODE_FIFO_KHR);
    CHECK(has_mode(modes, VK_PRESENT_MODE_MAILBOX_KHR));
    CHECK(has_mode(modes, VK_PRESENT_MODE_IMMEDIATE_KHR));
    CHECK(has_mode(modes, VK_PRESENT_MODE_FIFO_RELAXED_KHR));
}

TEST_CASE("graphics_compositor_swapchain_accepted")
{
    // Review focus 4: Monado with XRT_COMPOSITOR_COMPUTE=0 renders into SRGB with colour-attachment usage.
    const dvl::SwapchainCheck graphics = dvl::check_swapchain_parameters(
        swapchain_info(VK_FORMAT_R8G8B8A8_SRGB, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT),
        kSpec);
    CHECK_FALSE(graphics.refused);
    CHECK(graphics.notes.empty());
    // Its default compute compositor: UNORM with storage.
    const dvl::SwapchainCheck compute = dvl::check_swapchain_parameters(
        swapchain_info(VK_FORMAT_R8G8B8A8_UNORM, VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT), kSpec);
    CHECK_FALSE(compute.refused);
    CHECK(compute.notes.empty());

    // SRGB with storage is the one refused combination, whatever else is in the usage.
    for (const VkFormat format : {VK_FORMAT_R8G8B8A8_UNORM, VK_FORMAT_R8G8B8A8_SRGB}) {
        constexpr uint32_t kStorage = VK_IMAGE_USAGE_STORAGE_BIT;
        constexpr uint32_t kLastBit = VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT;
        for (uint32_t bit = 1; bit <= kLastBit; bit <<= 1) {
            const dvl::SwapchainCheck check = dvl::check_swapchain_parameters(swapchain_info(format, bit), kSpec);
            INFO("format " << format << " usage " << bit);
            CHECK(check.refused == (format == VK_FORMAT_R8G8B8A8_SRGB && bit == kStorage));
        }
    }
    const dvl::SwapchainCheck refused = dvl::check_swapchain_parameters(
        swapchain_info(VK_FORMAT_R8G8B8A8_SRGB, VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT),
        kSpec);
    CHECK(refused.refused);
    CHECK(refused.reason.find("SRGB") != std::string::npos);

    // What the caps would not allow is accepted and named (the swapchain is made from the caps).
    VkSwapchainCreateInfoKHR odd = swapchain_info(VK_FORMAT_B8G8R8A8_UNORM, VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT);
    odd.imageExtent = {1920, 1080};
    odd.minImageCount = 5;
    odd.compositeAlpha = VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR;
    const dvl::SwapchainCheck accepted = dvl::check_swapchain_parameters(odd, kSpec);
    CHECK_FALSE(accepted.refused);
    CHECK(accepted.notes.size() == 4);
}

TEST_CASE("capabilities_chain_offers_present_ids_and_timing_and_nothing_else_optional")
{
    VkSharedPresentSurfaceCapabilitiesKHR shared{VK_STRUCTURE_TYPE_SHARED_PRESENT_SURFACE_CAPABILITIES_KHR};
    shared.sharedPresentSupportedUsageFlags = VK_IMAGE_USAGE_STORAGE_BIT;
    VkSurfaceProtectedCapabilitiesKHR protect{VK_STRUCTURE_TYPE_SURFACE_PROTECTED_CAPABILITIES_KHR, &shared, VK_TRUE};
    VkSurfaceCapabilitiesFullScreenExclusiveEXT exclusive{
        VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_FULL_SCREEN_EXCLUSIVE_EXT, &protect, VK_TRUE};
    // Design 6.11: present timing, with the present-id2 ids it reports, and present-wait2 on them.
    VkPresentTimingSurfaceCapabilitiesEXT timing{VK_STRUCTURE_TYPE_PRESENT_TIMING_SURFACE_CAPABILITIES_EXT, &exclusive};
    timing.presentAtAbsoluteTimeSupported = VK_TRUE;
    timing.presentAtRelativeTimeSupported = VK_TRUE;
    VkSurfaceCapabilitiesPresentWait2KHR wait2{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_PRESENT_WAIT_2_KHR, &timing,
                                               VK_FALSE};
    VkSurfaceCapabilitiesPresentId2KHR id2{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_PRESENT_ID_2_KHR, &wait2, VK_FALSE};
    VkPresentModeKHR compatible[4] = {};
    VkSurfacePresentModeCompatibilityEXT compat{VK_STRUCTURE_TYPE_SURFACE_PRESENT_MODE_COMPATIBILITY_EXT, &id2, 4,
                                                compatible};
    VkSurfacePresentScalingCapabilitiesEXT scaling{VK_STRUCTURE_TYPE_SURFACE_PRESENT_SCALING_CAPABILITIES_EXT,
                                                   &compat};
    scaling.supportedPresentScaling = VK_PRESENT_SCALING_STRETCH_BIT_EXT;
    // A struct the layer does not know stays as it was.
    PresentFlag unknown{static_cast<VkStructureType>(1000999000), &scaling, VK_TRUE};

    dvl_test::ScopedEnv present_at(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", nullptr);
    dvl::fill_surface_capabilities_chain(&unknown, kSpec, VK_PRESENT_MODE_MAILBOX_KHR);
    CHECK(unknown.supported == VK_TRUE);
    CHECK(scaling.supportedPresentScaling == 0);
    CHECK(scaling.minScaledImageExtent.width == 7104);
    CHECK(scaling.maxScaledImageExtent.height == 3840);
    CHECK(compat.presentModeCount == 1);
    CHECK(compatible[0] == VK_PRESENT_MODE_MAILBOX_KHR);
    CHECK(id2.presentId2Supported == VK_TRUE);
    CHECK(wait2.presentWait2Supported == VK_TRUE);
    CHECK(timing.presentTimingSupported == VK_TRUE);
    CHECK(timing.presentAtAbsoluteTimeSupported == VK_TRUE); // design 6.11: present at a time
    CHECK(timing.presentAtRelativeTimeSupported == VK_FALSE);
    CHECK(timing.presentStageQueries == (VK_PRESENT_STAGE_QUEUE_OPERATIONS_END_BIT_EXT |
                                         VK_PRESENT_STAGE_REQUEST_DEQUEUED_BIT_EXT |
                                         VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT));
    CHECK(exclusive.fullScreenExclusiveSupported == VK_FALSE);
    CHECK(protect.supportsProtected == VK_FALSE);
    CHECK(shared.sharedPresentSupportedUsageFlags == 0);

    // The input side: the queried present mode, if any.
    VkSurfacePresentModeEXT mode{VK_STRUCTURE_TYPE_SURFACE_PRESENT_MODE_EXT, nullptr, VK_PRESENT_MODE_IMMEDIATE_KHR};
    VkSurfaceFullScreenExclusiveInfoEXT fse{VK_STRUCTURE_TYPE_SURFACE_FULL_SCREEN_EXCLUSIVE_INFO_EXT, &mode};
    CHECK(dvl::queried_present_mode(&fse) == VK_PRESENT_MODE_IMMEDIATE_KHR);
    CHECK_FALSE(dvl::queried_present_mode(nullptr).has_value());
    // Without one, no mode is compatible; a count query gives the count.
    compat.pPresentModes = nullptr;
    compat.presentModeCount = 9;
    dvl::fill_surface_capabilities_chain(&compat, kSpec, std::nullopt);
    CHECK(compat.presentModeCount == 0);
    dvl::fill_surface_capabilities_chain(&compat, kSpec, VK_PRESENT_MODE_FIFO_KHR);
    CHECK(compat.presentModeCount == 1);
}

TEST_CASE("capabilities_chain_offers_no_present_at_a_time_with_MIRADOR_DIRECT_MODE_NO_PRESENT_AT")
{
    VkPresentTimingSurfaceCapabilitiesEXT timing{VK_STRUCTURE_TYPE_PRESENT_TIMING_SURFACE_CAPABILITIES_EXT};
    {
        dvl_test::ScopedEnv off(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", L"1");
        dvl::fill_surface_capabilities_chain(&timing, kSpec, VK_PRESENT_MODE_FIFO_KHR);
    }
    CHECK(timing.presentTimingSupported == VK_TRUE); // timings stay
    CHECK(timing.presentAtAbsoluteTimeSupported == VK_FALSE);
    CHECK(timing.presentAtRelativeTimeSupported == VK_FALSE);
}

TEST_CASE("surface_registry_knows_only_its_own")
{
    auto record = std::make_unique<dvl::SurfaceRecord>();
    record->spec = kSpec;
    const dvl::SurfaceRecord* const raw = record.get();
    const VkSurfaceKHR surface = dvl::add_surface(std::move(record));
    CHECK(surface == reinterpret_cast<VkSurfaceKHR>(const_cast<dvl::SurfaceRecord*>(raw)));
    CHECK(dvl::find_surface(surface) == raw);
    CHECK(dvl::find_surface(VK_NULL_HANDLE) == nullptr);
    CHECK(dvl::find_surface(dvl_test::kPeekSurface) == nullptr);
    CHECK(dvl::remove_surface(dvl_test::kPeekSurface) == nullptr);
    std::unique_ptr<dvl::SurfaceRecord> back = dvl::remove_surface(surface);
    CHECK(back.get() == raw);
    CHECK(dvl::find_surface(surface) == nullptr);
}

TEST_CASE("our_surface_handle_reads_as_a_vk_icd_surface_display")
{
    // Design 5's first fallback: a layer above ours that looks inside a surface handle, as ICD WSI
    // code does, finds a display surface (VkIcdSurfaceDisplay of vk_icd.h) with what the application asked for, not a
    // pointer it would take for another platform's surface.
    ChainRig rig;
    REQUIRE(rig.ready());
    const auto query = rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
    uint32_t n = 1;
    VkDisplayPropertiesKHR display{};
    REQUIRE(query(ChainRig::gpu(0), &n, &display) == VK_SUCCESS);
    const auto modes = rig.fn<PFN_vkGetDisplayModePropertiesKHR>("vkGetDisplayModePropertiesKHR");
    VkDisplayModePropertiesKHR mode{};
    n = 1;
    REQUIRE(modes(ChainRig::gpu(0), display.display, &n, &mode) == VK_SUCCESS);
    VkDisplaySurfaceCreateInfoKHR ci{VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR};
    ci.displayMode = mode.displayMode;
    ci.planeIndex = 0;
    ci.planeStackIndex = 0;
    ci.transform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    ci.globalAlpha = 0.5f;
    ci.alphaMode = VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR;
    ci.imageExtent = {7104, 3840};
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    REQUIRE(rig.fn<PFN_vkCreateDisplayPlaneSurfaceKHR>("vkCreateDisplayPlaneSurfaceKHR")(rig.instance, &ci, nullptr,
                                                                                          &surface) == VK_SUCCESS);

    const auto* header = reinterpret_cast<const VkIcdSurfaceDisplay*>(surface);
    CHECK(header->base.platform == VK_ICD_WSI_PLATFORM_DISPLAY);
    CHECK(header->displayMode == mode.displayMode);
    CHECK(header->planeIndex == 0);
    CHECK(header->planeStackIndex == 0);
    CHECK(header->transform == VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR);
    CHECK(header->globalAlpha == 0.5f);
    CHECK(header->alphaMode == VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR);
    CHECK(header->imageExtent.width == 7104);
    CHECK(header->imageExtent.height == 3840);

    rig.fn<PFN_vkDestroySurfaceKHR>("vkDestroySurfaceKHR")(rig.instance, surface, nullptr);
}

TEST_CASE("our_surface_answers_every_query_without_calling_down")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkSurfaceKHR surface = make_our_surface(rig);
    const size_t session_lines = rig.session_log.size(); // the driver version and "display session open"
    g_driver.clear_calls();
    const VkPhysicalDevice gpu = ChainRig::gpu(0);

    VkBool32 supported = VK_FALSE;
    const auto support = rig.fn<PFN_vkGetPhysicalDeviceSurfaceSupportKHR>("vkGetPhysicalDeviceSurfaceSupportKHR");
    for (uint32_t family = 0; family < 3; family++) {
        supported = VK_FALSE;
        REQUIRE(support(gpu, family, surface, &supported) == VK_SUCCESS);
        CHECK(supported == VK_TRUE);
    }

    VkSurfaceCapabilitiesKHR caps{};
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR>("vkGetPhysicalDeviceSurfaceCapabilitiesKHR")(
                gpu, surface, &caps) == VK_SUCCESS);
    check_our_caps(caps);

    const auto formats = rig.fn<PFN_vkGetPhysicalDeviceSurfaceFormatsKHR>("vkGetPhysicalDeviceSurfaceFormatsKHR");
    uint32_t n = 0;
    REQUIRE(formats(gpu, surface, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 2);
    VkSurfaceFormatKHR first{};
    n = 1;
    CHECK(formats(gpu, surface, &n, &first) == VK_INCOMPLETE);
    CHECK(first.format == VK_FORMAT_R8G8B8A8_UNORM);

    const auto modes =
        rig.fn<PFN_vkGetPhysicalDeviceSurfacePresentModesKHR>("vkGetPhysicalDeviceSurfacePresentModesKHR");
    std::vector<VkPresentModeKHR> list(4);
    n = 4;
    REQUIRE(modes(gpu, surface, &n, list.data()) == VK_SUCCESS);
    CHECK(list == dvl::present_modes());

    VkPhysicalDeviceSurfaceInfo2KHR info{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SURFACE_INFO_2_KHR, nullptr, surface};
    VkPresentTimingSurfaceCapabilitiesEXT timing{VK_STRUCTURE_TYPE_PRESENT_TIMING_SURFACE_CAPABILITIES_EXT};
    VkSurfaceCapabilitiesPresentWait2KHR wait2{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_PRESENT_WAIT_2_KHR, &timing,
                                               VK_FALSE};
    VkSurfaceCapabilities2KHR caps2{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_2_KHR, &wait2};
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceCapabilities2KHR>("vkGetPhysicalDeviceSurfaceCapabilities2KHR")(
                gpu, &info, &caps2) == VK_SUCCESS);
    CHECK(caps2.pNext == &wait2);
    check_our_caps(caps2.surfaceCapabilities);
    CHECK(wait2.presentWait2Supported == VK_TRUE);
    CHECK(timing.presentTimingSupported == VK_TRUE);

    const auto formats2 = rig.fn<PFN_vkGetPhysicalDeviceSurfaceFormats2KHR>("vkGetPhysicalDeviceSurfaceFormats2KHR");
    std::vector<VkSurfaceFormat2KHR> list2(2, VkSurfaceFormat2KHR{VK_STRUCTURE_TYPE_SURFACE_FORMAT_2_KHR});
    VkImageCompressionPropertiesEXT compression{VK_STRUCTURE_TYPE_IMAGE_COMPRESSION_PROPERTIES_EXT};
    compression.imageCompressionFlags = VK_IMAGE_COMPRESSION_FIXED_RATE_DEFAULT_EXT;
    list2[1].pNext = &compression;
    n = 2;
    REQUIRE(formats2(gpu, &info, &n, list2.data()) == VK_SUCCESS);
    CHECK(list2[0].sType == VK_STRUCTURE_TYPE_SURFACE_FORMAT_2_KHR);
    CHECK(list2[1].surfaceFormat.format == VK_FORMAT_R8G8B8A8_SRGB);
    CHECK(list2[1].pNext == &compression);
    CHECK(compression.imageCompressionFlags == VK_IMAGE_COMPRESSION_DEFAULT_EXT);

    VkSurfaceCapabilities2EXT ext{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_2_EXT};
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceCapabilities2EXT>("vkGetPhysicalDeviceSurfaceCapabilities2EXT")(
                gpu, surface, &ext) == VK_SUCCESS);
    CHECK(ext.supportedSurfaceCounters == VK_SURFACE_COUNTER_VBLANK_BIT_EXT);
    CHECK(ext.minImageCount == 3);

    VkRect2D rect{};
    n = 1;
    REQUIRE(rig.fn<PFN_vkGetPhysicalDevicePresentRectanglesKHR>("vkGetPhysicalDevicePresentRectanglesKHR")(
                gpu, surface, &n, &rect) == VK_SUCCESS);
    CHECK(rect.offset.x == 0);
    CHECK(rect.offset.y == 0);
    CHECK(rect.extent.width == 7104);
    CHECK(rect.extent.height == 3840);

    n = 0;
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfacePresentModes2EXT>("vkGetPhysicalDeviceSurfacePresentModes2EXT")(
                gpu, &info, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 4);

    // The device-group queries, on a device.
    dvl_test::MonadoQueue monado;
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(0, {monado.info}, {"VK_KHR_swapchain"}, &device) == VK_SUCCESS);
    g_driver.clear_calls();
    VkDeviceGroupPresentModeFlagsKHR group = 0;
    REQUIRE(rig.device_fn<PFN_vkGetDeviceGroupSurfacePresentModesKHR>(
                device, "vkGetDeviceGroupSurfacePresentModesKHR")(device, surface, &group) == VK_SUCCESS);
    CHECK(group == VK_DEVICE_GROUP_PRESENT_MODE_LOCAL_BIT_KHR);
    group = 0;
    REQUIRE(rig.device_fn<PFN_vkGetDeviceGroupSurfacePresentModes2EXT>(
                device, "vkGetDeviceGroupSurfacePresentModes2EXT")(device, &info, &group) == VK_SUCCESS);
    CHECK(group == VK_DEVICE_GROUP_PRESENT_MODE_LOCAL_BIT_KHR);

    // None of it reached the driver, and nothing was logged as failing.
    CHECK(g_driver.calls().empty());
    CHECK(rig.session_log.size() == session_lines);

    // Destroying it does not go down either.
    rig.fn<PFN_vkDestroySurfaceKHR>("vkDestroySurfaceKHR")(rig.instance, surface, nullptr);
    CHECK(g_driver.count("vkDestroySurfaceKHR") == 0);
    rig.destroy_device(device);
}

TEST_CASE("foreign_surface_queries_go_down_unchanged")
{
    // Monado's peek window: a Win32 surface on the same physical device, next to our display.
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkSurfaceKHR ours = make_our_surface(rig);
    g_driver.clear_calls();
    const VkPhysicalDevice gpu = ChainRig::gpu(0);
    const VkSurfaceKHR peek = dvl_test::kPeekSurface;
    const uint64_t peek_value = dvl_test::handle_value(peek);

    VkBool32 supported = VK_FALSE;
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceSupportKHR>("vkGetPhysicalDeviceSurfaceSupportKHR")(
                gpu, 1, peek, &supported) == VK_SUCCESS);
    CHECK(g_driver.last("vkGetPhysicalDeviceSurfaceSupportKHR").handle == peek_value);
    CHECK(g_driver.last("vkGetPhysicalDeviceSurfaceSupportKHR").index == 1);

    VkSurfaceCapabilitiesKHR caps{};
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR>("vkGetPhysicalDeviceSurfaceCapabilitiesKHR")(
                gpu, peek, &caps) == VK_SUCCESS);
    CHECK(caps.minImageCount == 2); // the driver's answer
    uint32_t n = 0;
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceFormatsKHR>("vkGetPhysicalDeviceSurfaceFormatsKHR")(
                gpu, peek, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 1);
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfacePresentModesKHR>("vkGetPhysicalDeviceSurfacePresentModesKHR")(
                gpu, peek, &n, nullptr) == VK_SUCCESS);
    CHECK(n == 1);
    VkPhysicalDeviceSurfaceInfo2KHR info{VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SURFACE_INFO_2_KHR, nullptr, peek};
    VkSurfaceCapabilities2KHR caps2{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_2_KHR};
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceCapabilities2KHR>("vkGetPhysicalDeviceSurfaceCapabilities2KHR")(
                gpu, &info, &caps2) == VK_SUCCESS);
    CHECK(caps2.surfaceCapabilities.minImageCount == 2);
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceFormats2KHR>("vkGetPhysicalDeviceSurfaceFormats2KHR")(
                gpu, &info, &n, nullptr) == VK_SUCCESS);
    // ...Capabilities2EXT goes down too: the loader's terminator emulates it for a driver without it.
    VkSurfaceCapabilities2EXT ext{VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_2_EXT};
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfaceCapabilities2EXT>("vkGetPhysicalDeviceSurfaceCapabilities2EXT")(
                gpu, peek, &ext) == VK_SUCCESS);
    CHECK(ext.supportedSurfaceCounters == 0);
    REQUIRE(rig.fn<PFN_vkGetPhysicalDevicePresentRectanglesKHR>("vkGetPhysicalDevicePresentRectanglesKHR")(
                gpu, peek, &n, nullptr) == VK_SUCCESS);
    REQUIRE(rig.fn<PFN_vkGetPhysicalDeviceSurfacePresentModes2EXT>("vkGetPhysicalDeviceSurfacePresentModes2EXT")(
                gpu, &info, &n, nullptr) == VK_SUCCESS);

    for (const char* name :
         {"vkGetPhysicalDeviceSurfaceCapabilitiesKHR", "vkGetPhysicalDeviceSurfaceFormatsKHR",
          "vkGetPhysicalDeviceSurfacePresentModesKHR", "vkGetPhysicalDeviceSurfaceCapabilities2KHR",
          "vkGetPhysicalDeviceSurfaceFormats2KHR", "vkGetPhysicalDeviceSurfaceCapabilities2EXT",
          "vkGetPhysicalDevicePresentRectanglesKHR", "vkGetPhysicalDeviceSurfacePresentModes2EXT"}) {
        INFO(name);
        CHECK(g_driver.count(name) == 1);
        CHECK(g_driver.last(name).handle == peek_value);
    }

    dvl_test::MonadoQueue monado;
    VkDevice device = VK_NULL_HANDLE;
    REQUIRE(rig.create_device(0, {monado.info}, {"VK_KHR_swapchain"}, &device) == VK_SUCCESS);
    VkDeviceGroupPresentModeFlagsKHR group = 0;
    REQUIRE(rig.device_fn<PFN_vkGetDeviceGroupSurfacePresentModesKHR>(
                device, "vkGetDeviceGroupSurfacePresentModesKHR")(device, peek, &group) == VK_SUCCESS);
    CHECK(group == VK_DEVICE_GROUP_PRESENT_MODE_REMOTE_BIT_KHR);
    REQUIRE(rig.device_fn<PFN_vkGetDeviceGroupSurfacePresentModes2EXT>(
                device, "vkGetDeviceGroupSurfacePresentModes2EXT")(device, &info, &group) == VK_SUCCESS);
    CHECK(g_driver.last("vkGetDeviceGroupSurfacePresentModes2EXT").handle == peek_value);

    rig.fn<PFN_vkDestroySurfaceKHR>("vkDestroySurfaceKHR")(rig.instance, peek, nullptr);
    CHECK(g_driver.last("vkDestroySurfaceKHR").handle == peek_value);
    rig.fn<PFN_vkDestroySurfaceKHR>("vkDestroySurfaceKHR")(rig.instance, ours, nullptr);
    CHECK(g_driver.count("vkDestroySurfaceKHR") == 1);
    rig.destroy_device(device);
}

TEST_CASE("our_surface_not_presentable_from_another_gpu")
{
    ChainRig rig;
    REQUIRE(rig.ready());
    const VkSurfaceKHR surface = make_our_surface(rig);
    const auto support = rig.fn<PFN_vkGetPhysicalDeviceSurfaceSupportKHR>("vkGetPhysicalDeviceSurfaceSupportKHR");
    VkBool32 supported = VK_TRUE;
    REQUIRE(support(ChainRig::gpu(2), 0, surface, &supported) == VK_SUCCESS);
    CHECK(supported == VK_FALSE);
    rig.fn<PFN_vkDestroySurfaceKHR>("vkDestroySurfaceKHR")(rig.instance, surface, nullptr);
}
