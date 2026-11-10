// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// What the layer does with each command of build/generated/handle_commands.inc (tools/gen_handle_commands.py): every
// command in vk.xml, in all extensions, that can be handed one of the layer's own VkDisplayKHR, VkDisplayModeKHR,
// VkSurfaceKHR or VkSwapchainKHR (design 6.3, last bullet). Each is one of
//   - intercepted (kIntercepted), or
//   - not applicable, with the reason (kNotApplicable).
// The test every_handle_command_is_classified fails for a command that is neither, and the runtime guard
// (check_command_coverage) logs an error when an application enables an extension that has such a command.

#include "layer/handled_commands.hpp"

#include <algorithm>
#include <initializer_list>
#include <iterator>
#include <mutex>
#include <set>

#include "layer/log.hpp"

namespace dvl {
namespace {

// Intercepted now: the entries of the layer's proc-address tables (layer_main.cpp, and the lists in intercepts.hpp). A
// task adds the commands it intercepts, also the ones the generated list does not have because they take none of our
// objects (vkCreateInstance, vkDeviceWaitIdle, vkRegisterDeviceEventEXT, the fence commands).
const std::initializer_list<std::string_view> kIntercepted = {
    "vkGetInstanceProcAddr", "vkGetDeviceProcAddr", "vkCreateInstance", "vkDestroyInstance", "vkCreateDevice",
    "vkDestroyDevice",
    // Task 9: the private queue's mutex (design 6.2).
    "vkDeviceWaitIdle",
    // Task 9: the display, plane and mode queries (display_intercepts.cpp). The layer lists its own display first and
    // answers for it; a call on a display, mode or plane that is not its goes down (design 6.3).
    "vkGetPhysicalDeviceDisplayPropertiesKHR", "vkGetPhysicalDeviceDisplayProperties2KHR",
    "vkGetPhysicalDeviceDisplayPlanePropertiesKHR", "vkGetPhysicalDeviceDisplayPlaneProperties2KHR",
    "vkGetDisplayPlaneSupportedDisplaysKHR", "vkGetDisplayModePropertiesKHR", "vkGetDisplayModeProperties2KHR",
    "vkCreateDisplayModeKHR", "vkGetDisplayPlaneCapabilitiesKHR", "vkGetDisplayPlaneCapabilities2KHR",
    // VK_EXT_direct_mode_display: nothing is acquired from the layer's display, so releasing it does nothing; another
    // display goes down.
    "vkReleaseDisplayEXT",
    // VK_NV_acquire_winrt_display, which NVIDIA's Windows driver offers: refused for the layer's display, any other
    // goes down. vkGetWinrtDisplayNV only ever returns the driver's display, so it always goes down.
    "vkAcquireWinrtDisplayNV", "vkGetWinrtDisplayNV",
    // Task 9: the surface (surface_intercepts.cpp), and every surface query (design 6.4). None fails on the layer's
    // surface; a surface that is not its goes down (vkGetPhysicalDeviceSurfaceCapabilities2EXT included: the loader
    // emulates it).
    "vkCreateDisplayPlaneSurfaceKHR", "vkDestroySurfaceKHR", "vkGetPhysicalDeviceSurfaceSupportKHR",
    "vkGetPhysicalDeviceSurfaceCapabilitiesKHR", "vkGetPhysicalDeviceSurfaceFormatsKHR",
    "vkGetPhysicalDeviceSurfacePresentModesKHR", "vkGetPhysicalDeviceSurfaceCapabilities2KHR",
    "vkGetPhysicalDeviceSurfaceFormats2KHR", "vkGetPhysicalDeviceSurfaceCapabilities2EXT",
    "vkGetPhysicalDevicePresentRectanglesKHR", "vkGetDeviceGroupSurfacePresentModesKHR",
    // VK_EXT_full_screen_exclusive's two queries are surface queries too (present modes with a
    // VkPhysicalDeviceSurfaceInfo2KHR, as ...Formats2KHR has); acquire and release are Task 11's.
    "vkGetPhysicalDeviceSurfacePresentModes2EXT", "vkGetDeviceGroupSurfacePresentModes2EXT",
    // Task 11: the swapchain (swapchain.cpp), acquire, present and present-wait (present.cpp). A swapchain of the
    // layer's is never shared presentable (vkGetSwapchainStatusKHR: VK_SUCCESS).
    "vkCreateSwapchainKHR", "vkDestroySwapchainKHR", "vkGetSwapchainImagesKHR", "vkGetSwapchainStatusKHR",
    "vkAcquireNextImageKHR", "vkAcquireNextImage2KHR", "vkQueuePresentKHR", "vkWaitForPresentKHR",
    // Task 11: VK_EXT_display_control (events.cpp), the layer's own extension, and the fence commands that answer for
    // its display events (vkRegisterDeviceEventEXT and the fence commands take none of our objects: not in the
    // generated list).
    "vkRegisterDisplayEventEXT", "vkDisplayPowerControlEXT", "vkGetSwapchainCounterEXT", "vkRegisterDeviceEventEXT",
    "vkWaitForFences", "vkGetFenceStatus", "vkResetFences", "vkDestroyFence",
    // Task 11: the rest of design 6.3's list (misc_intercepts.cpp): accepted and answered, or refused with a log line
    // (vkCreateSharedSwapchainsKHR), for the layer's own handles; anything else goes down.
    "vkSetHdrMetadataEXT", "vkCreateSharedSwapchainsKHR", "vkAcquireFullScreenExclusiveModeEXT",
    "vkReleaseFullScreenExclusiveModeEXT", "vkReleaseSwapchainImagesEXT", "vkGetRefreshCycleDurationGOOGLE",
    "vkGetPastPresentationTimingGOOGLE", "vkSetDebugUtilsObjectNameEXT", "vkSetDebugUtilsObjectTagEXT",
    "vkDebugMarkerSetObjectNameEXT", "vkDebugMarkerSetObjectTagEXT",
    //  - Private data on a swapchain, surface, display or mode of the layer: the layer stores it itself; the private
    //    data of any other object goes down.
    "vkSetPrivateData", "vkGetPrivateData", "vkSetPrivateDataEXT", "vkGetPrivateDataEXT",
    //  - VK_NV_low_latency2 (NVIDIA's driver offers it) and VK_AMD_display_native_hdr: accepted and ignored on the
    //    layer's swapchain (vkGetLatencyTimingsNV: no timings; vkLatencySleepNV signals its semaphore at once).
    "vkSetLatencySleepModeNV", "vkLatencySleepNV", "vkSetLatencyMarkerNV", "vkGetLatencyTimingsNV",
    "vkSetLocalDimmingAMD",
    //  - An image made for a swapchain (VkImageSwapchainCreateInfoKHR in the pNext of vkCreateImage's create info or of
    //    the create info of the vkGetDeviceImage* queries; VkBindImageMemorySwapchainInfoKHR in vkBindImageMemory2's):
    //    one pNext check shared by all of them. They go down unless the chain names the layer's swapchain; then they
    //    are refused with a log line. (The spec forbids the swapchain struct in the create info of the memory-
    //    requirements queries, VUID-VkDeviceImageMemoryRequirements-pCreateInfo-06416, but the layer does not rely on
    //    an application obeying it.)
    "vkCreateImage", "vkBindImageMemory2", "vkBindImageMemory2KHR", "vkGetDeviceImageMemoryRequirements",
    "vkGetDeviceImageMemoryRequirementsKHR", "vkGetDeviceImageSparseMemoryRequirements",
    "vkGetDeviceImageSparseMemoryRequirementsKHR", "vkGetDeviceImageSubresourceLayout",
    "vkGetDeviceImageSubresourceLayoutKHR",
    // Present timing (design 6.11), from the 1.4.363 registry on; NVIDIA's driver offers all of these.
    //  - VK_KHR_present_wait2 waits as vkWaitForPresentKHR does (present.cpp), and VK_KHR_swapchain_maintenance1's KHR
    //    name of vkReleaseSwapchainImagesEXT releases as it does (misc_intercepts.cpp).
    "vkWaitForPresent2KHR", "vkReleaseSwapchainImagesKHR",
    //  - VK_EXT_present_timing (present_timing_intercepts.cpp): answered from the flips for the layer's swapchain.
    "vkSetSwapchainPresentTimingQueueSizeEXT", "vkGetSwapchainTimingPropertiesEXT",
    "vkGetSwapchainTimeDomainPropertiesEXT", "vkGetPastPresentationTimingEXT",
    //  - Calibrated timestamps, whose VkSwapchainCalibratedTimestampInfoEXT can name the layer's swapchain: its local
    //    domains are answered from the layer below's QPC reading, without the handle.
    "vkGetCalibratedTimestampsKHR", "vkGetCalibratedTimestampsEXT",
};

// Commands that cannot do anything to the layer's objects here, with the reason: they cannot be handed one (another
// platform), or only pass it on as a number. Surface creators only return a surface of the driver or the loader, never
// the layer's, which comes from vkCreateDisplayPlaneSurfaceKHR alone.
const std::initializer_list<NotApplicableCommand> kNotApplicable = {
    {"vkCreateAndroidSurfaceKHR", "Android window surface: Android only, and it returns the driver's surface, not the layer's"},
    {"vkCreateDirectFBSurfaceEXT", "DirectFB surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateHeadlessSurfaceEXT", "headless surface made by the loader, tied to no display: never the layer's"},
    {"vkCreateIOSSurfaceMVK", "iOS surface: Apple only, and it returns the driver's surface, not the layer's"},
    {"vkCreateImagePipeSurfaceFUCHSIA", "Fuchsia surface: Fuchsia only, and it returns the driver's surface, not the layer's"},
    {"vkCreateMacOSSurfaceMVK", "macOS surface: Apple only, and it returns the driver's surface, not the layer's"},
    {"vkCreateMetalSurfaceEXT", "Metal surface: Apple only, and it returns the driver's surface, not the layer's"},
    {"vkCreateScreenSurfaceQNX", "QNX Screen surface: QNX only, and it returns the driver's surface, not the layer's"},
    {"vkCreateStreamDescriptorSurfaceGGP", "Google Stadia (GGP) surface: GGP only, and it returns the driver's surface, not the layer's"},
    {"vkCreateSurfaceOHOS", "OpenHarmony surface: OpenHarmony only, and it returns the driver's surface, not the layer's"},
    {"vkCreateUbmSurfaceSEC", "Samsung UBM surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateViSurfaceNN", "Nintendo VI surface: Nintendo Switch only, and it returns the driver's surface, not the layer's"},
    {"vkCreateWaylandSurfaceKHR", "Wayland surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateWin32SurfaceKHR", "window surface for an application's own window (Monado's peek window): returns the loader's surface, never the layer's; it goes down"},
    {"vkCreateXcbSurfaceKHR", "XCB surface: Linux only, and it returns the driver's surface, not the layer's"},
    {"vkCreateXlibSurfaceKHR", "Xlib surface: Linux only, and it returns the driver's surface, not the layer's"},

    {"vkAcquireXlibDisplayEXT", "acquires a display through X11 RandR: Linux only, and the layer's display is not an X11 output"},
    {"vkGetRandROutputDisplayEXT", "finds the display of an X11 RandR output: Linux only, so a driver display, never the layer's"},
    {"vkAcquireDrmDisplayEXT", "acquires a display through a DRM file descriptor: Linux only, and the layer's display is not a DRM connector"},
    {"vkGetDrmDisplayEXT", "finds the display of a DRM connector: Linux only, so a driver display, never the layer's"},

    {"vkSetBufferCollectionImageConstraintsFUCHSIA", "Fuchsia sysmem buffer collections (VK_FUCHSIA_buffer_collection): Fuchsia only"},

    // A message about an object: the object is only a number in the text.
    {"vkDebugReportMessageEXT", "object handles are only forwarded as values to message callbacks, never dereferenced by the loader or driver"},
    {"vkSubmitDebugUtilsMessageEXT", "object handles are only forwarded as values to message callbacks, never dereferenced by the loader or driver"},
};

// The generated file has three kinds of line; each use defines the three macros.
const HandleCommand kGenerated[] = {
#define DVL_HANDLE_COMMAND(name, extension) {name, extension},
#define DVL_KNOWN_EXTENSION(name)
#define DVL_REGISTRY_VERSION(version)
#include "handle_commands.inc"
#undef DVL_HANDLE_COMMAND
#undef DVL_KNOWN_EXTENSION
#undef DVL_REGISTRY_VERSION
};

const std::string_view kKnownExtensions[] = {
#define DVL_HANDLE_COMMAND(name, extension)
#define DVL_KNOWN_EXTENSION(name) name,
#define DVL_REGISTRY_VERSION(version)
#include "handle_commands.inc"
#undef DVL_HANDLE_COMMAND
#undef DVL_KNOWN_EXTENSION
#undef DVL_REGISTRY_VERSION
};

constexpr std::string_view kRegistryVersion =
#define DVL_HANDLE_COMMAND(name, extension)
#define DVL_KNOWN_EXTENSION(name)
#define DVL_REGISTRY_VERSION(version) version
#include "handle_commands.inc"
#undef DVL_HANDLE_COMMAND
#undef DVL_KNOWN_EXTENSION
#undef DVL_REGISTRY_VERSION
    ;

bool classified_command(std::string_view name)
{
    return handled_command(name) || not_applicable(name) != nullptr;
}

} // namespace

const std::vector<HandleCommand>& handle_commands()
{
    // Made at first use and never destroyed, like the other process-wide state (design 6.10).
    static const std::vector<HandleCommand>* const list =
        new std::vector<HandleCommand>(std::begin(kGenerated), std::end(kGenerated));
    return *list;
}

const std::vector<std::string_view>& known_extensions()
{
    static const std::vector<std::string_view>* const list =
        new std::vector<std::string_view>(std::begin(kKnownExtensions), std::end(kKnownExtensions));
    return *list;
}

std::string_view registry_version()
{
    return kRegistryVersion;
}

const std::vector<std::string_view>& intercepted_commands()
{
    static const std::vector<std::string_view>* const list =
        new std::vector<std::string_view>(kIntercepted.begin(), kIntercepted.end());
    return *list;
}

bool handled_command(std::string_view name)
{
    for (const std::string_view& n : kIntercepted) {
        if (n == name) {
            return true;
        }
    }
    return false;
}

const char* not_applicable(std::string_view name)
{
    for (const NotApplicableCommand& n : kNotApplicable) {
        if (n.name == name) {
            return n.reason;
        }
    }
    return nullptr;
}

const std::vector<NotApplicableCommand>& not_applicable_commands()
{
    static const std::vector<NotApplicableCommand>* const list =
        new std::vector<NotApplicableCommand>(kNotApplicable.begin(), kNotApplicable.end());
    return *list;
}

std::vector<std::string> coverage_errors(const std::vector<HandleCommand>& list, const char* const* extensions,
                                         uint32_t count, const std::function<bool(std::string_view)>& classified,
                                         std::string_view where)
{
    std::vector<std::string> lines;
    for (uint32_t i = 0; i < count; i++) {
        const std::string_view extension = extensions[i];
        std::string commands;
        for (const HandleCommand& command : list) {
            if (command.extension == extension && !classified(command.name)) {
                commands += (commands.empty() ? "" : ", ");
                commands += command.name;
            }
        }
        if (!commands.empty()) {
            lines.push_back(std::string(where) + ": extension " + std::string(extension) +
                            " is enabled, but the layer neither handles nor lists as not applicable its command(s) " +
                            commands + ", which can be handed the layer's own display, mode, surface or swapchain "
                            "(layer/handled_commands.cpp)");
        }
    }
    return lines;
}

std::vector<std::string> unknown_extension_warnings(const std::vector<std::string_view>& known,
                                                    std::string_view version, const char* const* extensions,
                                                    uint32_t count)
{
    std::vector<std::string> lines;
    for (uint32_t i = 0; i < count; i++) {
        const std::string_view extension = extensions[i];
        if (std::find(known.begin(), known.end(), extension) != known.end()) {
            continue;
        }
        std::string line = std::string(extension) + ": unknown to the layer's Vulkan registry (" + std::string(version) +
                           "); its commands are not checked for the layer's handles";
        if (std::find(lines.begin(), lines.end(), line) == lines.end()) {
            lines.push_back(std::move(line));
        }
    }
    return lines;
}

void check_command_coverage(const char* const* extensions, uint32_t count, const char* where) noexcept
{
    try {
        // A process creates several instances; the lines are about the layer, so each is written once.
        static std::mutex* const mutex = new std::mutex;
        static std::set<std::string>* const written = new std::set<std::string>;
        const auto write_once = [](Level level, const std::string& line) {
            bool first;
            {
                std::lock_guard<std::mutex> lock(*mutex);
                first = written->insert(line).second;
            }
            if (first) {
                log(level, "%s", line.c_str());
            }
        };
        for (const std::string& line :
             coverage_errors(handle_commands(), extensions, count, classified_command, where)) {
            write_once(Level::Error, line);
        }
        for (const std::string& line :
             unknown_extension_warnings(known_extensions(), registry_version(), extensions, count)) {
            write_once(Level::Warn, line);
        }
    } catch (...) {
    }
}

} // namespace dvl
