// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// mirador-display-check --vulkan (design 7): the layer exercised the way Monado uses it, in Monado's order
// (comp_vulkan.c, then comp_window_vk_display.c, comp_window_direct.c, comp_target_swapchain.c, comp_renderer.c):
//
//   instance (API 1.0, COMP_INSTANCE_EXTENSIONS_COMMON + VK_KHR_display + VK_EXT_display_surface_counter)
//   -> physical device -> DEVICE (VK_EXT_display_control, present id/wait), BEFORE the first display query
//   -> display properties (the layer opens the display session here) -> plane properties -> modes -> plane
//   capabilities -> display-plane surface -> surface queries -> swapchain
//   -> per frame: acquire, clear, submit, present with an id, wait for the queue to go idle, wait for the present, and
//      (after every present but the first) a display event on a thread of its own, waited on for up to 1 s
//   -> teardown: vblank thread, swapchain, surface, device, instance.
//
// Vulkan is reached only through the vkGetInstanceProcAddr the caller gives (the loader's, or a fake chain's in the
// tests), never through vulkan-1.lib. The layer must be found by the loader: installed, or VK_IMPLICIT_LAYER_PATH.

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <functional>
#include <iterator>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "check/modes.hpp"

namespace dvl {
namespace {

using Clock = std::chrono::steady_clock;

constexpr uint32_t kNvidiaVendorId = 0x10de;
constexpr uint64_t kPresentWaitNs = 500'000'000;   // a present is credited within a refresh; this is generous
constexpr uint64_t kEventWaitNs = 1'000'000'000;    // Monado's vblank thread waits 1 s
constexpr uint64_t kForever = UINT64_MAX;

const char* const kRequiredInstanceExtensions[] = {
    // COMP_INSTANCE_EXTENSIONS_COMMON, less the two that are optional below.
    "VK_KHR_external_fence_capabilities",
    "VK_KHR_external_memory_capabilities",
    "VK_KHR_external_semaphore_capabilities",
    "VK_KHR_get_physical_device_properties2",
    "VK_KHR_surface",
    // What the vk_display target asks for, and what the layer adds.
    "VK_KHR_display",
    "VK_EXT_display_surface_counter",
};
const char* const kOptionalInstanceExtensions[] = {
    "VK_EXT_debug_report",
    "VK_KHR_get_surface_capabilities2",
};
const char* const kDeviceExtensions[] = {
    "VK_KHR_swapchain",
    "VK_KHR_present_id",
    "VK_KHR_present_wait",
    "VK_EXT_display_control", // the layer's: Monado enables it with VK_EXT_display_surface_counter
};

const char* result_name(VkResult r)
{
    switch (r) {
    case VK_SUCCESS: return "VK_SUCCESS";
    case VK_NOT_READY: return "VK_NOT_READY";
    case VK_TIMEOUT: return "VK_TIMEOUT";
    case VK_INCOMPLETE: return "VK_INCOMPLETE";
    case VK_ERROR_OUT_OF_HOST_MEMORY: return "VK_ERROR_OUT_OF_HOST_MEMORY";
    case VK_ERROR_OUT_OF_DEVICE_MEMORY: return "VK_ERROR_OUT_OF_DEVICE_MEMORY";
    case VK_ERROR_INITIALIZATION_FAILED: return "VK_ERROR_INITIALIZATION_FAILED";
    case VK_ERROR_DEVICE_LOST: return "VK_ERROR_DEVICE_LOST";
    case VK_ERROR_LAYER_NOT_PRESENT: return "VK_ERROR_LAYER_NOT_PRESENT";
    case VK_ERROR_EXTENSION_NOT_PRESENT: return "VK_ERROR_EXTENSION_NOT_PRESENT";
    case VK_ERROR_FEATURE_NOT_PRESENT: return "VK_ERROR_FEATURE_NOT_PRESENT";
    case VK_ERROR_INCOMPATIBLE_DRIVER: return "VK_ERROR_INCOMPATIBLE_DRIVER";
    case VK_ERROR_TOO_MANY_OBJECTS: return "VK_ERROR_TOO_MANY_OBJECTS";
    case VK_ERROR_SURFACE_LOST_KHR: return "VK_ERROR_SURFACE_LOST_KHR";
    case VK_SUBOPTIMAL_KHR: return "VK_SUBOPTIMAL_KHR";
    case VK_ERROR_OUT_OF_DATE_KHR: return "VK_ERROR_OUT_OF_DATE_KHR";
    case VK_ERROR_UNKNOWN: return "VK_ERROR_UNKNOWN";
    default: return "an unlisted VkResult";
    }
}

std::string describe(VkResult r)
{
    return std::string(result_name(r)) + " (" + std::to_string(static_cast<int>(r)) + ")";
}

// --- The functions ---------------------------------------------------------------------------------------------

#define DVL_INSTANCE_FUNCTIONS(X)                       \
    X(DestroyInstance)                                  \
    X(EnumeratePhysicalDevices)                         \
    X(GetPhysicalDeviceProperties)                      \
    X(GetPhysicalDeviceQueueFamilyProperties)           \
    X(EnumerateDeviceExtensionProperties)               \
    X(CreateDevice)                                     \
    X(GetDeviceProcAddr)                                \
    X(GetPhysicalDeviceDisplayPropertiesKHR)            \
    X(GetPhysicalDeviceDisplayPlanePropertiesKHR)       \
    X(GetDisplayPlaneSupportedDisplaysKHR)              \
    X(GetDisplayModePropertiesKHR)                      \
    X(GetDisplayPlaneCapabilitiesKHR)                   \
    X(CreateDisplayPlaneSurfaceKHR)                     \
    X(DestroySurfaceKHR)                                \
    X(GetPhysicalDeviceSurfaceSupportKHR)               \
    X(GetPhysicalDeviceSurfaceCapabilitiesKHR)          \
    X(GetPhysicalDeviceSurfaceFormatsKHR)               \
    X(GetPhysicalDeviceSurfacePresentModesKHR)          \
    X(GetPhysicalDeviceSurfaceCapabilities2EXT)

#define DVL_DEVICE_FUNCTIONS(X) \
    X(DestroyDevice)            \
    X(GetDeviceQueue)           \
    X(DeviceWaitIdle)           \
    X(QueueSubmit)              \
    X(QueueWaitIdle)            \
    X(QueuePresentKHR)          \
    X(CreateSwapchainKHR)       \
    X(DestroySwapchainKHR)      \
    X(GetSwapchainImagesKHR)    \
    X(AcquireNextImageKHR)      \
    X(WaitForPresentKHR)        \
    X(RegisterDisplayEventEXT)  \
    X(GetSwapchainCounterEXT)   \
    X(WaitForFences)            \
    X(DestroyFence)             \
    X(CreateSemaphore)          \
    X(DestroySemaphore)         \
    X(CreateCommandPool)        \
    X(DestroyCommandPool)       \
    X(AllocateCommandBuffers)   \
    X(ResetCommandBuffer)       \
    X(BeginCommandBuffer)       \
    X(EndCommandBuffer)         \
    X(CmdPipelineBarrier)       \
    X(CmdClearColorImage)

struct InstanceFns {
#define X(name) PFN_vk##name name = nullptr;
    DVL_INSTANCE_FUNCTIONS(X)
#undef X
    PFN_vkGetPhysicalDeviceFeatures2KHR GetPhysicalDeviceFeatures2KHR = nullptr; // optional
};

struct DeviceFns {
#define X(name) PFN_vk##name name = nullptr;
    DVL_DEVICE_FUNCTIONS(X)
#undef X
};

// Loads every function of the list; `missing` names the ones the chain did not give.
bool load_instance_fns(PFN_vkGetInstanceProcAddr gipa, VkInstance instance, InstanceFns& fns, std::string& missing)
{
#define X(name)                                                                      \
    fns.name = reinterpret_cast<PFN_vk##name>(gipa(instance, "vk" #name));           \
    if (fns.name == nullptr) {                                                       \
        missing += missing.empty() ? "" : ", ";                                      \
        missing += "vk" #name;                                                       \
    }
    DVL_INSTANCE_FUNCTIONS(X)
#undef X
    fns.GetPhysicalDeviceFeatures2KHR = reinterpret_cast<PFN_vkGetPhysicalDeviceFeatures2KHR>(
        gipa(instance, "vkGetPhysicalDeviceFeatures2KHR"));
    return missing.empty();
}

bool load_device_fns(PFN_vkGetDeviceProcAddr gdpa, VkDevice device, DeviceFns& fns, std::string& missing)
{
#define X(name)                                                                      \
    fns.name = reinterpret_cast<PFN_vk##name>(gdpa(device, "vk" #name));             \
    if (fns.name == nullptr) {                                                       \
        missing += missing.empty() ? "" : ", ";                                      \
        missing += "vk" #name;                                                       \
    }
    DVL_DEVICE_FUNCTIONS(X)
#undef X
    return missing.empty();
}

// --- Display events: Monado's vblank thread ---------------------------------------------------------------------------

// After every present but the first (comp_target_swapchain.c): vkRegisterDisplayEventEXT(FIRST_PIXEL_OUT), then
// vkWaitForFences on the event's fence for up to 1 s, a time stamp, vkDestroyFence (also after a timeout).
class VblankThread {
public:
    VblankThread(const DeviceFns& fns, VkDevice device, VkDisplayKHR display)
        : fns_(fns), device_(device), display_(display), thread_([this] { run(); })
    {
    }
    ~VblankThread() { stop_and_join(); }

    // A frame was presented: one more event to register and wait for.
    void presented()
    {
        std::lock_guard<std::mutex> hold(mutex_);
        asked_++;
        wake_.notify_all();
    }
    // Finishes the events asked for, then ends the thread.
    void stop_and_join()
    {
        {
            std::lock_guard<std::mutex> hold(mutex_);
            stopping_ = true;
            wake_.notify_all();
        }
        if (thread_.joinable()) {
            thread_.join();
        }
    }

    struct Totals {
        uint64_t ok = 0;
        uint64_t timeouts = 0;
        uint64_t errors = 0;
        double interval_min_ms = 0;
        double interval_avg_ms = 0;
        double interval_max_ms = 0;
        VkResult first_error = VK_SUCCESS;
    };
    Totals totals() const
    {
        std::lock_guard<std::mutex> hold(mutex_);
        Totals t;
        t.ok = ok_;
        t.timeouts = timeouts_;
        t.errors = errors_;
        t.first_error = first_error_;
        if (intervals_ > 0) {
            t.interval_min_ms = interval_min_ms_;
            t.interval_max_ms = interval_max_ms_;
            t.interval_avg_ms = interval_sum_ms_ / static_cast<double>(intervals_);
        }
        return t;
    }

private:
    void run()
    {
        bool have_last = false;
        Clock::time_point last{};
        for (;;) {
            {
                std::unique_lock<std::mutex> hold(mutex_);
                wake_.wait(hold, [this] { return stopping_ || asked_ > done_; });
                if (asked_ == done_) {
                    return; // stopping, nothing left
                }
                // Stopping: the events of the last frames are still waited for, but only a few. On a dead display each
                // wait is a second long and the rest would hold the end of the run for minutes.
                if (stopping_ && after_stop_++ >= kEventsAfterStop) {
                    return;
                }
                done_++;
            }
            VkDisplayEventInfoEXT info{};
            info.sType = VK_STRUCTURE_TYPE_DISPLAY_EVENT_INFO_EXT;
            info.displayEvent = VK_DISPLAY_EVENT_TYPE_FIRST_PIXEL_OUT_EXT;
            VkFence fence = VK_NULL_HANDLE;
            const VkResult registered = fns_.RegisterDisplayEventEXT(device_, display_, &info, nullptr, &fence);
            if (registered != VK_SUCCESS) {
                note_error(registered);
                continue;
            }
            const VkResult waited = fns_.WaitForFences(device_, 1, &fence, VK_TRUE, kEventWaitNs);
            const Clock::time_point stamp = Clock::now();
            fns_.DestroyFence(device_, fence, nullptr); // also after a timeout, as Monado does
            std::lock_guard<std::mutex> hold(mutex_);
            if (waited == VK_SUCCESS) {
                ok_++;
                if (have_last) {
                    const double ms = std::chrono::duration<double, std::milli>(stamp - last).count();
                    interval_min_ms_ = intervals_ == 0 ? ms : std::min(interval_min_ms_, ms);
                    interval_max_ms_ = std::max(interval_max_ms_, ms);
                    interval_sum_ms_ += ms;
                    intervals_++;
                }
                last = stamp;
                have_last = true;
            } else if (waited == VK_TIMEOUT) {
                timeouts_++;
                have_last = false;
            } else {
                errors_++;
                if (first_error_ == VK_SUCCESS) {
                    first_error_ = waited;
                }
            }
        }
    }

    void note_error(VkResult r)
    {
        std::lock_guard<std::mutex> hold(mutex_);
        errors_++;
        if (first_error_ == VK_SUCCESS) {
            first_error_ = r;
        }
    }

    const DeviceFns& fns_;
    VkDevice device_;
    VkDisplayKHR display_;
    mutable std::mutex mutex_;
    std::condition_variable wake_;
    static constexpr uint64_t kEventsAfterStop = 3;
    uint64_t asked_ = 0;
    uint64_t done_ = 0;
    uint64_t after_stop_ = 0;
    bool stopping_ = false;
    uint64_t ok_ = 0;
    uint64_t timeouts_ = 0;
    uint64_t errors_ = 0;
    VkResult first_error_ = VK_SUCCESS;
    uint64_t intervals_ = 0;
    double interval_min_ms_ = 0;
    double interval_max_ms_ = 0;
    double interval_sum_ms_ = 0;
    std::thread thread_; // last: started once everything above exists
};

// --- The run -----------------------------------------------------------------------------------------------------

// What was created, undone in reverse order (Monado's teardown: vblank thread, swapchain, surface, device, instance).
class Cleanup {
public:
    void add(std::function<void()> undo) { undo_.push_back(std::move(undo)); }
    ~Cleanup()
    {
        while (!undo_.empty()) {
            undo_.back()();
            undo_.pop_back();
        }
    }

private:
    std::vector<std::function<void()>> undo_;
};

class VulkanRun {
public:
    VulkanRun(const VulkanEnv& env, const Options& options, Output& out) : env_(env), options_(options), out_(out)
    {
        result_.counts.requested_seconds = options.seconds;
    }

    ModeResult run()
    {
        bool ok = false;
        {
            Cleanup cleanup;
            ok = steps(cleanup);
        } // everything undone here, before the verdict is printed
        if (!ok && result_.steps_ok) {
            result_.steps_ok = false;
            result_.failed_step = "stopped";
        }
        return result_;
    }

private:
    bool interrupted_now() const { return env_.interrupted && env_.interrupted(); }

    bool fail(const std::string& what)
    {
        result_.steps_ok = false;
        result_.failed_step = what;
        say(out_, "FAILED: %s", what.c_str());
        return false;
    }
    bool fail(const char* step, VkResult r)
    {
        return fail(std::string(step) + " returned " + describe(r));
    }

    bool steps(Cleanup& cleanup)
    {
        return create_instance(cleanup) && create_device_with_display(cleanup) && create_surface(cleanup) &&
               create_swapchain(cleanup) && create_sync(cleanup) && present_frames(cleanup);
    }

    // --- instance ---

    bool create_instance(Cleanup& cleanup)
    {
        gipa_ = env_.gipa;
        const auto enumerate_extensions = reinterpret_cast<PFN_vkEnumerateInstanceExtensionProperties>(
            gipa_(VK_NULL_HANDLE, "vkEnumerateInstanceExtensionProperties"));
        const auto enumerate_layers = reinterpret_cast<PFN_vkEnumerateInstanceLayerProperties>(
            gipa_(VK_NULL_HANDLE, "vkEnumerateInstanceLayerProperties"));
        const auto create_instance = reinterpret_cast<PFN_vkCreateInstance>(gipa_(VK_NULL_HANDLE, "vkCreateInstance"));
        if (enumerate_extensions == nullptr || enumerate_layers == nullptr || create_instance == nullptr) {
            return fail("instance: the Vulkan loader has no vkEnumerateInstanceExtensionProperties, "
                        "vkEnumerateInstanceLayerProperties or vkCreateInstance");
        }

        std::vector<std::string> extensions;
        uint32_t n = 0;
        if (enumerate_extensions(nullptr, &n, nullptr) == VK_SUCCESS) {
            std::vector<VkExtensionProperties> list(n);
            const VkResult r = enumerate_extensions(nullptr, &n, list.data());
            if (r == VK_SUCCESS || r == VK_INCOMPLETE) {
                for (uint32_t i = 0; i < n; i++) {
                    extensions.emplace_back(list[i].extensionName);
                }
            }
        }
        LayerFacts facts;
        facts.layer_listed = false;
        n = 0;
        if (enumerate_layers(&n, nullptr) == VK_SUCCESS) {
            std::vector<VkLayerProperties> list(n);
            const VkResult r = enumerate_layers(&n, list.data());
            if (r == VK_SUCCESS || r == VK_INCOMPLETE) {
                for (uint32_t i = 0; i < n; i++) {
                    facts.layer_listed = facts.layer_listed || std::strcmp(list[i].layerName, kLayerName) == 0;
                }
            }
        }
        facts.elevated = env_.elevated;
        facts.disable_var_set = env_.disable_var_set;
        say(out_, "Vulkan loader: the layer %s is %s", kLayerName, facts.layer_listed ? "registered" : "NOT registered");

        auto offered = [&](const char* name) {
            return std::find(extensions.begin(), extensions.end(), name) != extensions.end();
        };
        std::vector<const char*> enabled;
        std::string missing;
        for (const char* name : kRequiredInstanceExtensions) {
            if (offered(name)) {
                enabled.push_back(name);
            } else {
                missing += missing.empty() ? "" : ", ";
                missing += name;
            }
        }
        for (const char* name : kOptionalInstanceExtensions) {
            if (offered(name)) {
                enabled.push_back(name);
            }
        }
        if (!missing.empty()) {
            if (!offered("VK_EXT_display_surface_counter")) {
                // Only the layer offers that one: the layer is not active in this process.
                for (const std::string& line : diagnose_missing_layer(facts)) {
                    say(out_, "  %s", line.c_str());
                }
                return fail("instance: the layer is not active (the loader does not offer VK_EXT_display_surface_counter; "
                            "missing: " + missing + ")");
            }
            return fail("instance: the Vulkan loader does not offer " + missing);
        }

        VkApplicationInfo app{};
        app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
        app.pApplicationName = "mirador-display-check";
        app.apiVersion = VK_API_VERSION_1_0; // Monado's vk_display instance
        VkInstanceCreateInfo ci{};
        ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
        ci.pApplicationInfo = &app;
        ci.enabledExtensionCount = static_cast<uint32_t>(enabled.size());
        ci.ppEnabledExtensionNames = enabled.data();
        const VkResult created = create_instance(&ci, nullptr, &instance_);
        if (created != VK_SUCCESS) {
            return fail("vkCreateInstance", created);
        }
        say(out_, "instance: created, API 1.0, %u extensions", ci.enabledExtensionCount);

        std::string absent;
        if (!load_instance_fns(gipa_, instance_, instance_fns_, absent)) {
            // Destroy through whatever we got: the instance exists.
            const auto destroy = reinterpret_cast<PFN_vkDestroyInstance>(gipa_(instance_, "vkDestroyInstance"));
            if (destroy != nullptr) {
                destroy(instance_, nullptr);
            }
            return fail("instance: the chain gives no " + absent);
        }
        cleanup.add([this] { instance_fns_.DestroyInstance(instance_, nullptr); });
        return true;
    }

    // --- device before the first display query, then the display ---

    // The NVIDIA physical devices in enumeration order; each gets a device, and the first with the display is used.
    bool create_device_with_display(Cleanup& cleanup)
    {
        uint32_t n = 0;
        VkResult r = instance_fns_.EnumeratePhysicalDevices(instance_, &n, nullptr);
        if (r != VK_SUCCESS) {
            return fail("vkEnumeratePhysicalDevices", r);
        }
        std::vector<VkPhysicalDevice> gpus(n);
        r = instance_fns_.EnumeratePhysicalDevices(instance_, &n, gpus.data());
        if (r != VK_SUCCESS && r != VK_INCOMPLETE) {
            return fail("vkEnumeratePhysicalDevices", r);
        }
        gpus.resize(n);

        size_t tried = 0;
        std::string last_reason;
        for (const VkPhysicalDevice gpu : gpus) {
            VkPhysicalDeviceProperties props{};
            instance_fns_.GetPhysicalDeviceProperties(gpu, &props);
            say(out_, "physical device: %s (vendor 0x%04x, driver %s)", props.deviceName, props.vendorID,
                props.vendorID == kNvidiaVendorId ? nvidia_driver_text(props.driverVersion).c_str() : "-");
            if (props.vendorID != kNvidiaVendorId) {
                continue;
            }
            tried++;
            device_name_ = props.deviceName;
            gpu_ = gpu;
            if (!create_device(gpu)) {
                return false;
            }
            const DisplayQuery query = query_display(gpu);
            if (query == DisplayQuery::Found) {
                cleanup.add([this] { drop_device(); });
                return true;
            }
            drop_device();
            if (query == DisplayQuery::Error) {
                return false;
            }
            // No display on this GPU: its device is gone again; try the next one.
            last_reason = std::string("the layer lists no display on ") + props.deviceName;
        }
        if (tried == 0) {
            return fail("physical device: this machine has no NVIDIA GPU (the layer's display needs one)");
        }
        print_layer_failure();
        return fail("display: " + last_reason +
                    " (the layer's lines above and %LOCALAPPDATA%\\Mirador\\direct-mode\\layer.log say why: a holder, NVAPI, "
                    "the board asleep, the EDID, the import)");
    }

    // The device, if there is one, destroyed (also when only some of it was set up).
    void drop_device()
    {
        if (device_ == VK_NULL_HANDLE) {
            return;
        }
        const auto destroy = device_fns_.DestroyDevice != nullptr
                                 ? device_fns_.DestroyDevice
                                 : reinterpret_cast<PFN_vkDestroyDevice>(
                                       instance_fns_.GetDeviceProcAddr(device_, "vkDestroyDevice"));
        if (destroy != nullptr) {
            destroy(device_, nullptr);
        }
        device_ = VK_NULL_HANDLE;
        queue_ = VK_NULL_HANDLE;
        device_fns_ = DeviceFns{};
    }

    // False leaves no device behind.
    bool create_device(VkPhysicalDevice gpu)
    {
        uint32_t n = 0;
        instance_fns_.GetPhysicalDeviceQueueFamilyProperties(gpu, &n, nullptr);
        std::vector<VkQueueFamilyProperties> families(n);
        instance_fns_.GetPhysicalDeviceQueueFamilyProperties(gpu, &n, families.data());
        // Monado's compute compositor works on the compute family (not the graphics one); both can clear an image.
        uint32_t chosen = UINT32_MAX;
        for (uint32_t i = 0; i < n; i++) {
            const VkQueueFlags f = families[i].queueFlags;
            if ((f & VK_QUEUE_COMPUTE_BIT) != 0 && (f & VK_QUEUE_GRAPHICS_BIT) == 0) {
                chosen = i;
                break;
            }
        }
        for (uint32_t i = 0; i < n && chosen == UINT32_MAX; i++) {
            if ((families[i].queueFlags & (VK_QUEUE_COMPUTE_BIT | VK_QUEUE_GRAPHICS_BIT)) != 0) {
                chosen = i;
            }
        }
        if (chosen == UINT32_MAX) {
            return fail("device: no queue family can clear an image (graphics or compute)");
        }
        family_ = chosen;

        VkPhysicalDevicePresentIdFeaturesKHR present_id{};
        present_id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR;
        VkPhysicalDevicePresentWaitFeaturesKHR present_wait{};
        present_wait.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_WAIT_FEATURES_KHR;
        present_wait.pNext = &present_id;
        if (instance_fns_.GetPhysicalDeviceFeatures2KHR != nullptr) {
            VkPhysicalDeviceFeatures2KHR features{};
            features.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2_KHR;
            features.pNext = &present_wait;
            instance_fns_.GetPhysicalDeviceFeatures2KHR(gpu, &features);
            if (present_id.presentId != VK_TRUE || present_wait.presentWait != VK_TRUE) {
                return fail("device: the driver does not support the presentId and presentWait features the layer's "
                            "present-wait needs");
            }
        }
        present_id.presentId = VK_TRUE;
        present_wait.presentWait = VK_TRUE;

        const float priority = 1.0f;
        VkDeviceQueueCreateInfo queue{};
        queue.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
        queue.queueFamilyIndex = family_;
        queue.queueCount = 1;
        queue.pQueuePriorities = &priority;
        VkDeviceCreateInfo ci{};
        ci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
        ci.pNext = &present_wait;
        ci.queueCreateInfoCount = 1;
        ci.pQueueCreateInfos = &queue;
        ci.enabledExtensionCount = static_cast<uint32_t>(std::size(kDeviceExtensions));
        ci.ppEnabledExtensionNames = kDeviceExtensions;
        const VkResult r = instance_fns_.CreateDevice(gpu, &ci, nullptr, &device_);
        if (r != VK_SUCCESS) {
            std::string wanted;
            for (const char* name : kDeviceExtensions) {
                wanted += wanted.empty() ? "" : ", ";
                wanted += name;
            }
            return fail("vkCreateDevice (queue family " + std::to_string(family_) + ", extensions " + wanted +
                        ") returned " + describe(r));
        }
        std::string absent;
        const auto gdpa = instance_fns_.GetDeviceProcAddr;
        if (!load_device_fns(gdpa, device_, device_fns_, absent)) {
            drop_device();
            return fail("device: the chain gives no " + absent);
        }
        device_fns_.GetDeviceQueue(device_, family_, 0, &queue_);
        if (queue_ == VK_NULL_HANDLE) {
            drop_device();
            return fail("vkGetDeviceQueue gave no queue");
        }
        say(out_, "device: created on %s, queue family %u (before the first display query, as Monado does), with "
                  "VK_EXT_display_control and present id/wait", device_name_.c_str(), family_);
        return true;
    }

    enum class DisplayQuery { Found, None, Error };

    DisplayQuery query_display(VkPhysicalDevice gpu)
    {
        say(out_, "display query: the layer opens the display session now (NVAPI, direct mode, BindDevice; waits up to "
                  "10 s for the display) and proves the back-buffer import");
        uint32_t count = 0;
        VkResult r = instance_fns_.GetPhysicalDeviceDisplayPropertiesKHR(gpu, &count, nullptr);
        if (r != VK_SUCCESS) {
            fail("vkGetPhysicalDeviceDisplayPropertiesKHR", r);
            return DisplayQuery::Error;
        }
        if (count == 0) {
            say(out_, "display query: no display listed on %s", device_name_.c_str());
            return DisplayQuery::None;
        }
        std::vector<VkDisplayPropertiesKHR> displays(count);
        r = instance_fns_.GetPhysicalDeviceDisplayPropertiesKHR(gpu, &count, displays.data());
        if (r != VK_SUCCESS && r != VK_INCOMPLETE) {
            fail("vkGetPhysicalDeviceDisplayPropertiesKHR", r);
            return DisplayQuery::Error;
        }
        displays.resize(count);
        for (uint32_t i = 0; i < count; i++) {
            say(out_, "  display %u: %s, %ux%u", i, displays[i].displayName != nullptr ? displays[i].displayName : "?",
                displays[i].physicalResolution.width, displays[i].physicalResolution.height);
        }
        // XRT_COMPOSITOR_FORCE_VK_DISPLAY=0: the first display, which the layer lists first.
        display_ = displays[0].display;
        display_name_ = displays[0].displayName != nullptr ? displays[0].displayName : "?";
        return DisplayQuery::Found;
    }

    // --- plane, mode, surface ---

    bool create_surface(Cleanup& cleanup)
    {
        // Planes (vk_enumerate_physical_display_plane_properties): Monado always uses plane 0.
        uint32_t plane_count = 0;
        VkResult r = instance_fns_.GetPhysicalDeviceDisplayPlanePropertiesKHR(gpu_, &plane_count, nullptr);
        if (r != VK_SUCCESS) {
            return fail("vkGetPhysicalDeviceDisplayPlanePropertiesKHR", r);
        }
        if (plane_count == 0) {
            return fail("plane: the layer lists no display plane");
        }
        std::vector<VkDisplayPlanePropertiesKHR> planes(plane_count);
        r = instance_fns_.GetPhysicalDeviceDisplayPlanePropertiesKHR(gpu_, &plane_count, planes.data());
        if (r != VK_SUCCESS && r != VK_INCOMPLETE) {
            return fail("vkGetPhysicalDeviceDisplayPlanePropertiesKHR", r);
        }
        const uint32_t plane_index = 0;
        const uint32_t stack_index = planes[plane_index].currentStackIndex;
        say(out_, "plane: %u planes, using plane %u (stack index %u), shows the display: %s", plane_count, plane_index,
            stack_index, planes[plane_index].currentDisplay == display_ ? "yes" : "no");

        // Modes (vk_enumerate_display_mode_properties): the first.
        uint32_t mode_count = 0;
        r = instance_fns_.GetDisplayModePropertiesKHR(gpu_, display_, &mode_count, nullptr);
        if (r != VK_SUCCESS) {
            return fail("vkGetDisplayModePropertiesKHR", r);
        }
        if (mode_count == 0) {
            return fail("mode: the display has no mode");
        }
        std::vector<VkDisplayModePropertiesKHR> modes(mode_count);
        r = instance_fns_.GetDisplayModePropertiesKHR(gpu_, display_, &mode_count, modes.data());
        if (r != VK_SUCCESS && r != VK_INCOMPLETE) {
            return fail("vkGetDisplayModePropertiesKHR", r);
        }
        const VkDisplayModePropertiesKHR& mode = modes[0];
        extent_ = mode.parameters.visibleRegion;
        refresh_mhz_ = mode.parameters.refreshRate;
        say(out_, "mode: %ux%u at %.3f Hz", extent_.width, extent_.height, refresh_mhz_ / 1000.0);

        // Not called by Monado on this path; the layer implements it for callers like this one. A wrong answer is
        // worth a line, not a failed check.
        uint32_t supported_count = 0;
        if (instance_fns_.GetDisplayPlaneSupportedDisplaysKHR(gpu_, plane_index, &supported_count, nullptr) ==
                VK_SUCCESS &&
            supported_count > 0) {
            std::vector<VkDisplayKHR> supported(supported_count);
            instance_fns_.GetDisplayPlaneSupportedDisplaysKHR(gpu_, plane_index, &supported_count, supported.data());
            const bool listed = std::find(supported.begin(), supported.end(), display_) != supported.end();
            if (!listed) {
                say(out_, "warning: plane %u does not list the display among its supported displays", plane_index);
            }
        }

        VkDisplayPlaneCapabilitiesKHR plane_caps{};
        r = instance_fns_.GetDisplayPlaneCapabilitiesKHR(gpu_, mode.displayMode, plane_index, &plane_caps);
        if (r != VK_SUCCESS) {
            return fail("vkGetDisplayPlaneCapabilitiesKHR", r);
        }

        // comp_window_direct.c: choose_alpha_mode.
        VkDisplayPlaneAlphaFlagBitsKHR alpha = VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR;
        if ((plane_caps.supportedAlpha & VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_PREMULTIPLIED_BIT_KHR) != 0) {
            alpha = VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_PREMULTIPLIED_BIT_KHR;
        } else if ((plane_caps.supportedAlpha & VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR) != 0) {
            alpha = VK_DISPLAY_PLANE_ALPHA_PER_PIXEL_BIT_KHR;
        }
        VkDisplaySurfaceCreateInfoKHR ci{};
        ci.sType = VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR;
        ci.displayMode = mode.displayMode;
        ci.planeIndex = plane_index;
        ci.planeStackIndex = stack_index;
        ci.transform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        ci.globalAlpha = 1.0f;
        ci.alphaMode = alpha;
        ci.imageExtent = extent_;
        r = instance_fns_.CreateDisplayPlaneSurfaceKHR(instance_, &ci, nullptr, &surface_);
        if (r != VK_SUCCESS) {
            return fail("vkCreateDisplayPlaneSurfaceKHR", r);
        }
        cleanup.add([this] { instance_fns_.DestroySurfaceKHR(instance_, surface_, nullptr); });
        say(out_, "surface: created on plane %u, alpha mode 0x%x", plane_index, static_cast<unsigned>(alpha));

        // The surface queries Monado makes (vk_surface_info_fill_in, comp_target_swapchain.c): none may fail.
        VkBool32 can_present = VK_FALSE;
        r = instance_fns_.GetPhysicalDeviceSurfaceSupportKHR(gpu_, family_, surface_, &can_present);
        if (r != VK_SUCCESS) {
            return fail("vkGetPhysicalDeviceSurfaceSupportKHR", r);
        }
        if (can_present != VK_TRUE) {
            return fail("surface: queue family " + std::to_string(family_) + " cannot present to the surface");
        }
        r = instance_fns_.GetPhysicalDeviceSurfaceCapabilitiesKHR(gpu_, surface_, &caps_);
        if (r != VK_SUCCESS) {
            return fail("vkGetPhysicalDeviceSurfaceCapabilitiesKHR", r);
        }
        say(out_, "surface caps: %u..%u images, extent %ux%u, usage 0x%x", caps_.minImageCount, caps_.maxImageCount,
            caps_.currentExtent.width, caps_.currentExtent.height, static_cast<unsigned>(caps_.supportedUsageFlags));
        if (caps_.currentExtent.width != extent_.width || caps_.currentExtent.height != extent_.height) {
            return fail("surface: its extent is not the mode's");
        }
        if ((caps_.supportedUsageFlags & VK_IMAGE_USAGE_TRANSFER_DST_BIT) == 0) {
            return fail("surface: the images cannot be a transfer destination, so they cannot be cleared");
        }

        uint32_t format_count = 0;
        r = instance_fns_.GetPhysicalDeviceSurfaceFormatsKHR(gpu_, surface_, &format_count, nullptr);
        if (r != VK_SUCCESS) {
            return fail("vkGetPhysicalDeviceSurfaceFormatsKHR", r);
        }
        std::vector<VkSurfaceFormatKHR> formats(format_count);
        r = instance_fns_.GetPhysicalDeviceSurfaceFormatsKHR(gpu_, surface_, &format_count, formats.data());
        if (r != VK_SUCCESS && r != VK_INCOMPLETE) {
            return fail("vkGetPhysicalDeviceSurfaceFormatsKHR", r);
        }
        formats.resize(format_count);
        const bool has_format = std::any_of(formats.begin(), formats.end(), [](const VkSurfaceFormatKHR& f) {
            return f.format == VK_FORMAT_R8G8B8A8_UNORM && f.colorSpace == VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
        });
        if (!has_format) {
            return fail("surface: R8G8B8A8_UNORM with SRGB_NONLINEAR is not among its " + std::to_string(format_count) +
                        " formats");
        }

        uint32_t mode_n = 0;
        r = instance_fns_.GetPhysicalDeviceSurfacePresentModesKHR(gpu_, surface_, &mode_n, nullptr);
        if (r != VK_SUCCESS) {
            return fail("vkGetPhysicalDeviceSurfacePresentModesKHR", r);
        }
        std::vector<VkPresentModeKHR> present_modes(mode_n);
        r = instance_fns_.GetPhysicalDeviceSurfacePresentModesKHR(gpu_, surface_, &mode_n, present_modes.data());
        if (r != VK_SUCCESS && r != VK_INCOMPLETE) {
            return fail("vkGetPhysicalDeviceSurfacePresentModesKHR", r);
        }
        present_modes.resize(mode_n);
        if (std::find(present_modes.begin(), present_modes.end(), VK_PRESENT_MODE_FIFO_KHR) == present_modes.end()) {
            return fail("surface: FIFO is not among its present modes");
        }

        VkSurfaceCapabilities2EXT counters{};
        counters.sType = VK_STRUCTURE_TYPE_SURFACE_CAPABILITIES_2_EXT;
        r = instance_fns_.GetPhysicalDeviceSurfaceCapabilities2EXT(gpu_, surface_, &counters);
        if (r != VK_SUCCESS) {
            return fail("vkGetPhysicalDeviceSurfaceCapabilities2EXT", r);
        }
        if ((counters.supportedSurfaceCounters & VK_SURFACE_COUNTER_VBLANK_BIT_EXT) == 0) {
            return fail("surface: no VBLANK surface counter");
        }
        say(out_, "surface queries: support, capabilities, %u formats, %u present modes, VBLANK counter: all ok",
            format_count, mode_n);
        return true;
    }

    // --- swapchain, sync objects ---

    bool create_swapchain(Cleanup& cleanup)
    {
        // Monado's compute compositor: R8G8B8A8_UNORM, storage usage, FIFO, the minimum image count; plus the transfer
        // destination this tool clears with.
        VkSwapchainCreateInfoKHR ci{};
        ci.sType = VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR;
        ci.surface = surface_;
        ci.minImageCount = std::max<uint32_t>(caps_.minImageCount, 2);
        ci.imageFormat = VK_FORMAT_R8G8B8A8_UNORM;
        ci.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
        ci.imageExtent = extent_;
        ci.imageArrayLayers = 1;
        ci.imageUsage = VK_IMAGE_USAGE_TRANSFER_DST_BIT |
                        ((caps_.supportedUsageFlags & VK_IMAGE_USAGE_STORAGE_BIT) != 0 ? VK_IMAGE_USAGE_STORAGE_BIT : 0);
        ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        ci.preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        ci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
        ci.presentMode = VK_PRESENT_MODE_FIFO_KHR;
        ci.clipped = VK_TRUE;
        VkResult r = device_fns_.CreateSwapchainKHR(device_, &ci, nullptr, &swapchain_);
        if (r != VK_SUCCESS) {
            return fail("vkCreateSwapchainKHR", r);
        }
        cleanup.add([this] { device_fns_.DestroySwapchainKHR(device_, swapchain_, nullptr); });

        uint32_t n = 0;
        r = device_fns_.GetSwapchainImagesKHR(device_, swapchain_, &n, nullptr);
        if (r != VK_SUCCESS) {
            return fail("vkGetSwapchainImagesKHR", r);
        }
        images_.resize(n);
        r = device_fns_.GetSwapchainImagesKHR(device_, swapchain_, &n, images_.data());
        if (r != VK_SUCCESS && r != VK_INCOMPLETE) {
            return fail("vkGetSwapchainImagesKHR", r);
        }
        images_.resize(n);
        if (images_.empty()) {
            return fail("swapchain: it has no images");
        }
        say(out_, "swapchain: created, %zu images, R8G8B8A8_UNORM, usage 0x%x, FIFO", images_.size(),
            static_cast<unsigned>(ci.imageUsage));
        return true;
    }

    bool create_sync(Cleanup& cleanup)
    {
        VkSemaphoreCreateInfo si{};
        si.sType = VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO;
        VkResult r = device_fns_.CreateSemaphore(device_, &si, nullptr, &acquire_semaphore_);
        if (r != VK_SUCCESS) {
            return fail("vkCreateSemaphore", r);
        }
        r = device_fns_.CreateSemaphore(device_, &si, nullptr, &render_semaphore_);
        if (r != VK_SUCCESS) {
            device_fns_.DestroySemaphore(device_, acquire_semaphore_, nullptr);
            return fail("vkCreateSemaphore", r);
        }
        cleanup.add([this] {
            device_fns_.DestroySemaphore(device_, render_semaphore_, nullptr);
            device_fns_.DestroySemaphore(device_, acquire_semaphore_, nullptr);
        });

        VkCommandPoolCreateInfo pi{};
        pi.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
        pi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        pi.queueFamilyIndex = family_;
        r = device_fns_.CreateCommandPool(device_, &pi, nullptr, &pool_);
        if (r != VK_SUCCESS) {
            return fail("vkCreateCommandPool", r);
        }
        cleanup.add([this] { device_fns_.DestroyCommandPool(device_, pool_, nullptr); });
        VkCommandBufferAllocateInfo ai{};
        ai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
        ai.commandPool = pool_;
        ai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        ai.commandBufferCount = static_cast<uint32_t>(images_.size());
        commands_.resize(images_.size());
        r = device_fns_.AllocateCommandBuffers(device_, &ai, commands_.data());
        if (r != VK_SUCCESS) {
            return fail("vkAllocateCommandBuffers", r);
        }
        // Registered after the objects above, so it runs before they are destroyed: everything submitted has finished.
        cleanup.add([this] { device_fns_.DeviceWaitIdle(device_); });
        return true;
    }

    // The session's EDID route and the back-buffer import, as the layer logged them in this process: lines of another
    // process (an older run's file, Task 12 review) are never reported as this run's.
    void print_layer_log_facts(const std::optional<std::string>& log_text)
    {
        if (!log_text) {
            say(out_, "layer log: not readable; the layer's own lines above name the EDID route ('EDID read through') "
                      "and the back-buffer import ('trial import')");
            return;
        }
        const LayerLogFacts facts = layer_log_facts(*log_text, env_.pid);
        say(out_, "EDID route (from the layer's log): %s",
            facts.edid_route ? facts.edid_route->c_str() : "not found in this run's log");
        say(out_, "back-buffer import (from the layer's log): %s",
            facts.trial_import ? facts.trial_import->c_str() : "no 'trial import on device' line in this run's log");
        say(out_, "memory-type fallback (from the layer's log): %s",
            facts.memory_fallback ? facts.memory_fallback->c_str() : "none");
    }

    // The display session's failure line, as the layer logged it in this process (an error line), and what it means.
    void print_layer_failure()
    {
        const std::optional<std::string> log_text = env_.layer_log ? env_.layer_log() : std::nullopt;
        if (!log_text) {
            return;
        }
        const LayerLogFacts facts = layer_log_facts(*log_text, env_.pid);
        if (!facts.failure) {
            return;
        }
        say(out_, "the layer's failure line (from its log): %s", facts.failure->c_str());
        for (const std::string& line : explain_failure(*facts.failure)) {
            say(out_, "  %s", line.c_str());
        }
    }

    // --- the frames ---

    // One clear of `image`: UNDEFINED -> TRANSFER_DST, clear, TRANSFER_DST -> PRESENT_SRC_KHR (the layout the target
    // presented the back-buffers in).
    VkResult record_clear(VkCommandBuffer cmd, VkImage image, const float colour[4])
    {
        VkResult r = device_fns_.ResetCommandBuffer(cmd, 0);
        if (r != VK_SUCCESS) {
            return r;
        }
        VkCommandBufferBeginInfo bi{};
        bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
        bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        r = device_fns_.BeginCommandBuffer(cmd, &bi);
        if (r != VK_SUCCESS) {
            return r;
        }
        VkImageSubresourceRange range{VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
        VkImageMemoryBarrier to_clear{};
        to_clear.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
        to_clear.srcAccessMask = 0;
        to_clear.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        to_clear.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        to_clear.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        to_clear.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        to_clear.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        to_clear.image = image;
        to_clear.subresourceRange = range;
        // From TRANSFER, the stage the submit waits for the acquire semaphore at (present_frames), so the layout
        // change comes after that wait (Task 12 review); from TOP_OF_PIPE it would not.
        device_fns_.CmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0,
                                       nullptr, 0, nullptr, 1, &to_clear);
        VkClearColorValue value{};
        std::memcpy(value.float32, colour, sizeof(value.float32));
        device_fns_.CmdClearColorImage(cmd, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &value, 1, &range);
        VkImageMemoryBarrier to_present = to_clear;
        to_present.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        to_present.dstAccessMask = 0;
        to_present.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
        to_present.newLayout = VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;
        device_fns_.CmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, 0, 0,
                                       nullptr, 0, nullptr, 1, &to_present);
        return device_fns_.EndCommandBuffer(cmd);
    }

    bool present_frames(Cleanup& cleanup)
    {
        static const float kColours[4][4] = {{1, 0, 0, 1}, {0, 1, 0, 1}, {0, 0, 1, 1}, {1, 1, 1, 1}};

        uint64_t counter_before = 0;
        VkResult r = device_fns_.GetSwapchainCounterEXT(device_, swapchain_, VK_SURFACE_COUNTER_VBLANK_BIT_EXT,
                                                        &counter_before);
        if (r != VK_SUCCESS) {
            return fail("vkGetSwapchainCounterEXT", r);
        }

        const auto vblank = std::make_shared<VblankThread>(device_fns_, device_, display_);
        VblankThread* const events = vblank.get();
        // Joined first of all, as in Monado's teardown (also when a step below fails).
        cleanup.add([vblank] { vblank->stop_and_join(); });

        const double refresh_hz = refresh_mhz_ != 0 ? refresh_mhz_ / 1000.0 : kRefreshHz;
        result_.counts.refresh_hz = refresh_hz;
        say(out_, "presenting for %d s (%llu presents at %.0f Hz expected), a display event per frame", options_.seconds,
            static_cast<unsigned long long>(expected_flips(options_.seconds, refresh_hz)), refresh_hz);

        const Clock::time_point start = Clock::now();
        const Clock::time_point deadline = start + std::chrono::seconds(options_.seconds);
        Clock::time_point next_progress = start + std::chrono::seconds(5);
        uint64_t presented = 0;
        uint64_t failures = 0;
        uint64_t wait_timeouts = 0;
        bool broke = false; // the loop ended on an error, not on time
        std::string first_problem;
        std::string last_problem; // what ended the loop, when it ended on an error
        auto problem = [&](const std::string& text) {
            failures++;
            last_problem = text;
            if (first_problem.empty()) {
                first_problem = text;
                say(out_, "problem: %s", text.c_str());
            }
        };

        for (uint64_t id = 1; Clock::now() < deadline && !interrupted_now(); id++) {
            uint32_t index = UINT32_MAX;
            r = device_fns_.AcquireNextImageKHR(device_, swapchain_, kForever, acquire_semaphore_, VK_NULL_HANDLE,
                                                &index);
            if (r != VK_SUCCESS || index >= images_.size()) {
                // The layer's acquire with an infinite timeout never fails (design 6.6).
                problem("vkAcquireNextImageKHR returned " + describe(r));
                broke = true;
                break;
            }
            const int64_t second =
                std::chrono::duration_cast<std::chrono::seconds>(Clock::now() - start).count();
            r = record_clear(commands_[index], images_[index], kColours[second % 4]);
            if (r != VK_SUCCESS) {
                problem("recording the clear returned " + describe(r));
                broke = true;
                break;
            }
            const VkPipelineStageFlags wait_stage = VK_PIPELINE_STAGE_TRANSFER_BIT;
            VkSubmitInfo submit{};
            submit.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
            submit.waitSemaphoreCount = 1;
            submit.pWaitSemaphores = &acquire_semaphore_;
            submit.pWaitDstStageMask = &wait_stage;
            submit.commandBufferCount = 1;
            submit.pCommandBuffers = &commands_[index];
            submit.signalSemaphoreCount = 1;
            submit.pSignalSemaphores = &render_semaphore_;
            r = device_fns_.QueueSubmit(queue_, 1, &submit, VK_NULL_HANDLE);
            if (r != VK_SUCCESS) {
                problem("vkQueueSubmit returned " + describe(r));
                broke = true;
                break;
            }

            VkPresentIdKHR present_id{};
            present_id.sType = VK_STRUCTURE_TYPE_PRESENT_ID_KHR;
            present_id.swapchainCount = 1;
            present_id.pPresentIds = &id;
            VkPresentInfoKHR present{};
            present.sType = VK_STRUCTURE_TYPE_PRESENT_INFO_KHR;
            present.pNext = &present_id;
            present.waitSemaphoreCount = 1;
            present.pWaitSemaphores = &render_semaphore_;
            present.swapchainCount = 1;
            present.pSwapchains = &swapchain_;
            present.pImageIndices = &index;
            r = device_fns_.QueuePresentKHR(queue_, &present);
            if (r != VK_SUCCESS) {
                problem("vkQueuePresentKHR returned " + describe(r));
                broke = true;
                break;
            }
            presented++;

            // Monado waits for its queue to go idle after every present (renderer_wait_queue_idle): the layer's
            // acquire semaphore depends on it (design 6.6).
            r = device_fns_.QueueWaitIdle(queue_);
            if (r != VK_SUCCESS) {
                problem("vkQueueWaitIdle returned " + describe(r));
                broke = true;
                break;
            }
            if (id > 1) {
                events->presented(); // the first present has no event (comp_target_swapchain.c)
            }
            r = device_fns_.WaitForPresentKHR(device_, swapchain_, id, kPresentWaitNs);
            if (r != VK_SUCCESS) {
                wait_timeouts++;
                problem("vkWaitForPresentKHR(" + std::to_string(id) + ") returned " + describe(r));
            }

            // A dead display makes the layer complete every present at once (design 6.7 step 5): the loop would run at
            // the speed of the clears and hammer the GPU for the rest of the run. The mode's rate cannot do 3 times its count.
            if (presented > 3 * expected_flips(options_.seconds, refresh_hz) + 100) {
                problem("presenting " + std::to_string(presented) + " frames in " +
                        std::to_string(std::chrono::duration<double>(Clock::now() - start).count()) +
                        " s, far above the mode's rate: the display is not flipping (the layer completes presents at once while "
                        "flips are stopped)");
                broke = true;
                break;
            }
            if (Clock::now() >= next_progress) {
                next_progress += std::chrono::seconds(5);
                uint64_t now_counter = 0;
                device_fns_.GetSwapchainCounterEXT(device_, swapchain_, VK_SURFACE_COUNTER_VBLANK_BIT_EXT, &now_counter);
                say(out_, "  %.0f s: %llu presents, %llu flips", std::chrono::duration<double>(Clock::now() - start).count(),
                    static_cast<unsigned long long>(presented), static_cast<unsigned long long>(now_counter - counter_before));
            }
        }
        const Clock::time_point end = Clock::now();

        uint64_t counter_after = counter_before;
        r = device_fns_.GetSwapchainCounterEXT(device_, swapchain_, VK_SURFACE_COUNTER_VBLANK_BIT_EXT, &counter_after);
        if (r != VK_SUCCESS) {
            problem("vkGetSwapchainCounterEXT returned " + describe(r));
        }

        // The events asked for finish before their numbers are read.
        events->stop_and_join();
        const VblankThread::Totals totals = events->totals();
        if (totals.errors > 0) {
            problem("display events: " + std::to_string(totals.errors) + " calls failed, the first with " +
                    describe(totals.first_error));
        }

        result_.counts.presents = presented;
        result_.counts.flips = counter_after - counter_before;
        result_.counts.failures = failures;
        result_.counts.seconds = std::chrono::duration<double>(end - start).count();
        result_.counts.have_events = true;
        result_.counts.events_ok = totals.ok;
        result_.counts.events_timeout = totals.timeouts + totals.errors;

        say(out_, "frames: %llu presented, %llu flips, %llu present-wait timeouts; display events: %llu ok, %llu timed "
                  "out, wake interval %.2f ms avg (%.2f to %.2f)",
            static_cast<unsigned long long>(presented), static_cast<unsigned long long>(result_.counts.flips),
            static_cast<unsigned long long>(wait_timeouts), static_cast<unsigned long long>(totals.ok),
            static_cast<unsigned long long>(totals.timeouts), totals.interval_avg_ms, totals.interval_min_ms,
            totals.interval_max_ms);

        if (env_.layer_log) {
            print_layer_log_facts(env_.layer_log());
        }
        if (broke) {
            if (last_problem != first_problem) {
                say(out_, "problem: %s", last_problem.c_str());
            }
            return fail(last_problem);
        }
        if (interrupted_now()) {
            return fail("interrupted");
        }
        return true;
    }

    const VulkanEnv& env_;
    const Options& options_;
    Output& out_;
    ModeResult result_;

    PFN_vkGetInstanceProcAddr gipa_ = nullptr;
    InstanceFns instance_fns_;
    DeviceFns device_fns_;
    VkInstance instance_ = VK_NULL_HANDLE;
    VkPhysicalDevice gpu_ = VK_NULL_HANDLE;
    std::string device_name_;
    uint32_t family_ = 0;
    VkDevice device_ = VK_NULL_HANDLE;
    VkQueue queue_ = VK_NULL_HANDLE;
    VkDisplayKHR display_ = VK_NULL_HANDLE;
    std::string display_name_;
    VkExtent2D extent_{};
    uint32_t refresh_mhz_ = 0;
    VkSurfaceKHR surface_ = VK_NULL_HANDLE;
    VkSurfaceCapabilitiesKHR caps_{};
    VkSwapchainKHR swapchain_ = VK_NULL_HANDLE;
    std::vector<VkImage> images_;
    VkSemaphore acquire_semaphore_ = VK_NULL_HANDLE;
    VkSemaphore render_semaphore_ = VK_NULL_HANDLE;
    VkCommandPool pool_ = VK_NULL_HANDLE;
    std::vector<VkCommandBuffer> commands_;
};

bool is_elevated()
{
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) {
        return false;
    }
    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    const bool ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size) != 0;
    CloseHandle(token);
    return ok && elevation.TokenIsElevated != 0;
}

// The layer's log (design 8): %LOCALAPPDATA%\Mirador\direct-mode\layer.log. The layer starts a new file per process, but
// the newest file can still be an older process's (this one's layer wrote nothing), so the facts are taken from this
// process's lines only (layer_log_facts).
std::optional<std::string> read_layer_log()
{
    wchar_t base[MAX_PATH];
    const DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        return std::nullopt;
    }
    return read_layer_log_file(std::wstring(base) + L"\\Mirador\\direct-mode\\layer.log");
}

} // namespace

std::optional<std::string> read_layer_log_file(const std::wstring& path)
{
    std::ifstream file(path, std::ios::binary);
    if (!file) {
        return std::nullopt;
    }
    std::stringstream text;
    text << file.rdbuf();
    return text.str();
}

ModeResult run_vulkan_check(const VulkanEnv& env, const Options& options, Output& out)
{
    VulkanRun run(env, options, out);
    return run.run();
}

ModeResult run_vulkan_mode(const Options& options, Output& out)
{
    // Before anything loads Vulkan: the loader reads the enable variables when it builds the instance's layer list.
    apply_environment(options);

    ModeResult result;
    result.counts.requested_seconds = options.seconds;
    // The system loader. Monado ships its own vulkan-1.dll next to monado-service.exe; this tool uses the one Windows
    // finds, which is the one the layer is registered with.
    const HMODULE loader = LoadLibraryW(L"vulkan-1.dll"); // never freed: the layer pins its own DLL and the display session
    const auto gipa = loader != nullptr
                          ? reinterpret_cast<PFN_vkGetInstanceProcAddr>(GetProcAddress(loader, "vkGetInstanceProcAddr"))
                          : nullptr;
    if (gipa == nullptr) {
        result.steps_ok = false;
        result.failed_step = "vulkan-1.dll could not be loaded (is a Vulkan driver installed?)";
        say(out, "FAILED: %s", result.failed_step.c_str());
        return result;
    }
    wchar_t path[MAX_PATH];
    if (GetModuleFileNameW(loader, path, MAX_PATH) != 0) {
        std::string narrow(static_cast<size_t>(MAX_PATH) * 3, '\0');
        const int size = WideCharToMultiByte(CP_UTF8, 0, path, -1, &narrow[0], static_cast<int>(narrow.size()), nullptr, nullptr);
        if (size > 1) {
            say(out, "Vulkan loader: %s", narrow.c_str());
        }
    }

    VulkanEnv env;
    env.gipa = gipa;
    env.elevated = is_elevated();
    env.disable_var_set = GetEnvironmentVariableW(L"DISABLE_MIRADOR_DIRECT_MODE", nullptr, 0) != 0;
    env.interrupted = [] { return interrupted(); };
    env.layer_log = read_layer_log;
    return run_vulkan_check(env, options, out);
}

} // namespace dvl
