// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The swapchain, acquire, present, present-wait and display events (design 6.4-6.10) through the layer's chain: the
// test build of the layer DLL, a fake driver below it, and a display session on a FakeApi whose clock runs at the real
// rate, so the layer's real display thread (DisplayThread, DisplayLoop, Win32Waiter) presents to it and its counter
// flips every 1/90 s. Monado is played as it calls the layer: its render loop (acquire, present with a present id,
// present-wait) and its vblank thread (register a display event, wait on its fence for up to 1 s, destroy it). Nothing
// here reaches the real NVAPI or the real display session.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <set>
#include <string>
#include <thread>
#include <vector>

#include "core/clock.hpp"
#include "core/fence_pool.hpp"
#include "core/trace.hpp"
#include "fakes/fake_chain.hpp"

namespace {

using namespace std::chrono_literals;
using dvl_test::ChainRig;
using dvl_test::g_driver;
using dvl_test::handle_value;
using dvl_test::LogCapture;
using Clock = std::chrono::steady_clock;

constexpr uint64_t kForever = UINT64_MAX;
constexpr uint64_t kMs = 1'000'000;
constexpr int64_t kRefresh = nvdm::FakeApi::kDefaultRefreshNs;

template <class T>
T fake_handle(uintptr_t value)
{
    return reinterpret_cast<T>(value);
}

// Monado's semaphores: the fake driver only counts them.
const VkSemaphore kAcquireSemaphore = fake_handle<VkSemaphore>(0x5E3A0001);
const VkSemaphore kRenderSemaphore = fake_handle<VkSemaphore>(0x5E3A0002);

// The rig with Monado's setup done, in Monado's order: instance, device (compute compositor queue, present id and wait,
// display control), the display query (which runs the trial import on the device), the mode, the surface on plane 0.
struct Monado {
    ChainRig rig{dvl_test::default_gpus(), true};
    VkDevice device = VK_NULL_HANDLE;
    VkQueue queue = VK_NULL_HANDLE; // Monado's main queue: family 2, index 0
    VkDisplayKHR display = VK_NULL_HANDLE;
    VkSurfaceKHR surface = VK_NULL_HANDLE;

    PFN_vkCreateSwapchainKHR create_swapchain = nullptr;
    PFN_vkDestroySwapchainKHR destroy_swapchain = nullptr;
    PFN_vkGetSwapchainImagesKHR get_images = nullptr;
    PFN_vkAcquireNextImageKHR acquire = nullptr;
    PFN_vkQueuePresentKHR present = nullptr;
    PFN_vkWaitForPresentKHR wait_for_present = nullptr;
    PFN_vkRegisterDisplayEventEXT register_event = nullptr;
    PFN_vkWaitForFences wait_fences = nullptr;
    PFN_vkGetFenceStatus fence_status = nullptr;
    PFN_vkResetFences reset_fences = nullptr;
    PFN_vkDestroyFence destroy_fence = nullptr;
    PFN_vkCreateFence create_fence = nullptr;

    explicit Monado(bool with_display_control = true)
    {
        REQUIRE(rig.ready());
        dvl_test::MonadoQueue monado;
        std::vector<const char*> extensions = {"VK_KHR_swapchain", "VK_KHR_present_id", "VK_KHR_present_wait"};
        if (with_display_control) {
            extensions.push_back("VK_EXT_display_control");
        }
        REQUIRE(rig.create_device(0, {monado.info}, extensions, &device) == VK_SUCCESS);
        queue = rig.get_queue(device, 2, 0);

        const auto query =
            rig.fn<PFN_vkGetPhysicalDeviceDisplayPropertiesKHR>("vkGetPhysicalDeviceDisplayPropertiesKHR");
        uint32_t n = 1;
        VkDisplayPropertiesKHR props{};
        REQUIRE(query(ChainRig::gpu(0), &n, &props) == VK_SUCCESS);
        REQUIRE(n == 1);
        display = props.display;
        const auto modes = rig.fn<PFN_vkGetDisplayModePropertiesKHR>("vkGetDisplayModePropertiesKHR");
        VkDisplayModePropertiesKHR mode{};
        n = 1;
        REQUIRE(modes(ChainRig::gpu(0), display, &n, &mode) == VK_SUCCESS);
        VkDisplaySurfaceCreateInfoKHR sci{VK_STRUCTURE_TYPE_DISPLAY_SURFACE_CREATE_INFO_KHR};
        sci.displayMode = mode.displayMode;
        sci.transform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        sci.globalAlpha = 1.0f;
        sci.alphaMode = VK_DISPLAY_PLANE_ALPHA_GLOBAL_BIT_KHR;
        sci.imageExtent = {7104, 3840};
        REQUIRE(rig.fn<PFN_vkCreateDisplayPlaneSurfaceKHR>("vkCreateDisplayPlaneSurfaceKHR")(rig.instance, &sci,
                                                                                              nullptr, &surface) ==
                VK_SUCCESS);

        create_swapchain = fn<PFN_vkCreateSwapchainKHR>("vkCreateSwapchainKHR");
        destroy_swapchain = fn<PFN_vkDestroySwapchainKHR>("vkDestroySwapchainKHR");
        get_images = fn<PFN_vkGetSwapchainImagesKHR>("vkGetSwapchainImagesKHR");
        acquire = fn<PFN_vkAcquireNextImageKHR>("vkAcquireNextImageKHR");
        present = fn<PFN_vkQueuePresentKHR>("vkQueuePresentKHR");
        wait_for_present = fn<PFN_vkWaitForPresentKHR>("vkWaitForPresentKHR");
        register_event = fn<PFN_vkRegisterDisplayEventEXT>("vkRegisterDisplayEventEXT");
        wait_fences = fn<PFN_vkWaitForFences>("vkWaitForFences");
        fence_status = fn<PFN_vkGetFenceStatus>("vkGetFenceStatus");
        reset_fences = fn<PFN_vkResetFences>("vkResetFences");
        destroy_fence = fn<PFN_vkDestroyFence>("vkDestroyFence");
        create_fence = fn<PFN_vkCreateFence>("vkCreateFence");
        g_driver.clear_calls();
    }

    template <class Fn>
    Fn fn(const char* name) const
    {
        return rig.device_fn<Fn>(device, name);
    }

    // Monado's compute compositor's swapchain: R8G8B8A8_UNORM, storage, FIFO, three images.
    VkSwapchainCreateInfoKHR swapchain_info(VkSwapchainKHR old = VK_NULL_HANDLE) const
    {
        VkSwapchainCreateInfoKHR ci{VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR};
        ci.surface = surface;
        ci.minImageCount = 3;
        ci.imageFormat = VK_FORMAT_R8G8B8A8_UNORM;
        ci.imageColorSpace = VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
        ci.imageExtent = {7104, 3840};
        ci.imageArrayLayers = 1;
        ci.imageUsage = VK_IMAGE_USAGE_STORAGE_BIT;
        ci.imageSharingMode = VK_SHARING_MODE_EXCLUSIVE;
        ci.preTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        ci.compositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
        ci.presentMode = VK_PRESENT_MODE_FIFO_KHR;
        ci.clipped = VK_TRUE;
        ci.oldSwapchain = old;
        return ci;
    }

    VkSwapchainKHR make_swapchain(VkSwapchainKHR old = VK_NULL_HANDLE)
    {
        const VkSwapchainCreateInfoKHR ci = swapchain_info(old);
        VkSwapchainKHR swapchain = VK_NULL_HANDLE;
        REQUIRE(create_swapchain(device, &ci, nullptr, &swapchain) == VK_SUCCESS);
        REQUIRE(swapchain != VK_NULL_HANDLE);
        return swapchain;
    }

    std::vector<VkImage> images(VkSwapchainKHR swapchain) const
    {
        uint32_t n = 0;
        REQUIRE(get_images(device, swapchain, &n, nullptr) == VK_SUCCESS);
        std::vector<VkImage> list(n);
        REQUIRE(get_images(device, swapchain, &n, list.data()) == VK_SUCCESS);
        return list;
    }

    VkResult present_one(VkSwapchainKHR swapchain, uint32_t index, uint64_t id)
    {
        VkPresentIdKHR present_id{VK_STRUCTURE_TYPE_PRESENT_ID_KHR};
        present_id.swapchainCount = 1;
        present_id.pPresentIds = &id;
        VkPresentInfoKHR info{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
        info.pNext = &present_id;
        info.waitSemaphoreCount = 1;
        info.pWaitSemaphores = &kRenderSemaphore;
        info.swapchainCount = 1;
        info.pSwapchains = &swapchain;
        info.pImageIndices = &index;
        VkResult result = VK_RESULT_MAX_ENUM;
        info.pResults = &result;
        const VkResult r = present(queue, &info);
        CHECK(result == r);
        return r;
    }

    // Monado's vblank thread's registration: a FIRST_PIXEL_OUT event on the layer's display, its fence.
    VkFence display_event()
    {
        VkDisplayEventInfoEXT info{VK_STRUCTURE_TYPE_DISPLAY_EVENT_INFO_EXT};
        info.displayEvent = VK_DISPLAY_EVENT_TYPE_FIRST_PIXEL_OUT_EXT;
        VkFence fence = VK_NULL_HANDLE;
        REQUIRE(register_event(device, display, &info, nullptr, &fence) == VK_SUCCESS);
        return fence;
    }

    // One frame of Monado's render loop: acquire (UINT64_MAX, a semaphore), present with its id, wait for it.
    uint32_t frame(VkSwapchainKHR swapchain, uint64_t id, uint64_t present_wait_timeout = 500 * kMs)
    {
        uint32_t index = UINT32_MAX;
        REQUIRE(acquire(device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
        REQUIRE(index < 3);
        REQUIRE(present_one(swapchain, index, id) == VK_SUCCESS);
        REQUIRE(wait_for_present(device, swapchain, id, present_wait_timeout) == VK_SUCCESS);
        return index;
    }

    void destroy_device()
    {
        if (device != VK_NULL_HANDLE) {
            rig.destroy_device(device);
            device = VK_NULL_HANDLE;
        }
    }
};

// Monado's vblank thread (comp_target_swapchain.c): after every present but the first, register a FIRST_PIXEL_OUT
// event, wait on its fence for up to 1 s, stamp the time, destroy the fence (also after a timeout).
class VblankThread {
public:
    explicit VblankThread(Monado& m) : m_(m), thread_([this] { run(); }) {}
    ~VblankThread() { stop(); }

    // The render loop presented a frame.
    void presented()
    {
        std::lock_guard<std::mutex> hold(mutex_);
        ++asked_;
        cv_.notify_all();
    }
    void stop()
    {
        {
            std::lock_guard<std::mutex> hold(mutex_);
            stopping_ = true;
            cv_.notify_all();
        }
        if (thread_.joinable()) {
            thread_.join();
        }
    }

    struct Wait {
        VkFence fence;
        VkResult result;
        int64_t registered; // on the session's clock
        int64_t stamp;      // when the wait returned
    };
    std::vector<Wait> waits; // read after stop()

private:
    void run()
    {
        for (;;) {
            {
                std::unique_lock<std::mutex> hold(mutex_);
                cv_.wait(hold, [this] { return stopping_ || asked_ > done_; });
                if (asked_ == done_) {
                    return; // stopping, nothing left
                }
                ++done_;
            }
            VkDisplayEventInfoEXT info{VK_STRUCTURE_TYPE_DISPLAY_EVENT_INFO_EXT};
            info.displayEvent = VK_DISPLAY_EVENT_TYPE_FIRST_PIXEL_OUT_EXT;
            VkFence fence = VK_NULL_HANDLE;
            if (m_.register_event(m_.device, m_.display, &info, nullptr, &fence) != VK_SUCCESS) {
                waits.push_back(Wait{VK_NULL_HANDLE, VK_ERROR_UNKNOWN, 0, 0});
                continue;
            }
            const int64_t registered = m_.rig.clock.now_ns();
            const VkResult r = m_.wait_fences(m_.device, 1, &fence, VK_TRUE, 1'000'000'000);
            const int64_t stamp = m_.rig.clock.now_ns();
            m_.destroy_fence(m_.device, fence, nullptr);
            waits.push_back(Wait{fence, r, registered, stamp});
        }
    }

    Monado& m_;
    std::mutex mutex_;
    std::condition_variable cv_;
    uint64_t asked_ = 0;
    uint64_t done_ = 0;
    bool stopping_ = false;
    std::thread thread_;
};

// The FakeApi's flips: at k * refresh on the session's clock (it was made at clock 0 and never stopped here).
int64_t flip_at_or_before(int64_t t)
{
    return (t / kRefresh) * kRefresh;
}

std::vector<uint64_t> present_tags(const nvdm::FakeApi& api)
{
    std::vector<uint64_t> tags;
    for (const nvdm::FakeApi::Call& c : api.calls()) {
        if (c.name == "present") {
            tags.push_back(c.arg);
        }
    }
    return tags;
}

// How often the display thread reads the stats over `ms` milliseconds of real time.
size_t stats_reads_during(const nvdm::FakeApi& api, int ms)
{
    const size_t before = api.count("present_stats");
    std::this_thread::sleep_for(std::chrono::milliseconds(ms));
    return api.count("present_stats") - before;
}

// A folder for MIRADOR_DIRECT_MODE_TRACE that is gone again at the end of the test, and the file the layer makes in it.
class TraceDir {
public:
    TraceDir()
    {
        path_ = std::filesystem::temp_directory_path() / ("dvl_test_trace_" + std::to_string(GetCurrentProcessId()));
        std::filesystem::remove_all(path_);
        std::filesystem::create_directories(path_);
    }
    ~TraceDir()
    {
        std::error_code ec;
        std::filesystem::remove_all(path_, ec);
    }
    TraceDir(const TraceDir&) = delete;
    TraceDir& operator=(const TraceDir&) = delete;

    std::wstring folder() const { return path_.wstring(); }
    std::filesystem::path file() const
    {
        return path_ / ("mirador-trace-" + std::to_string(GetCurrentProcessId()) + ".csv");
    }
    // The file's lines; none while it is not there.
    std::vector<std::string> lines() const
    {
        std::vector<std::string> out;
        std::ifstream in(file(), std::ios::binary);
        for (std::string line; std::getline(in, line);) {
            out.push_back(line);
        }
        return out;
    }

private:
    std::filesystem::path path_;
};

// How many of a trace's lines are of this kind (the second column).
size_t records_of_kind(const std::vector<std::string>& lines, nvdm::TraceKind kind)
{
    const std::string wanted = std::to_string(static_cast<uint32_t>(kind));
    return static_cast<size_t>(std::count_if(lines.begin(), lines.end(), [&](const std::string& line) {
        const size_t from = line.find(',');
        return from != std::string::npos && line.compare(from + 1, line.find(',', from + 1) - from - 1, wanted) == 0;
    }));
}

} // namespace

TEST_CASE("monado_frame_loop_against_the_fake")
{
    // A few hundred frames of Monado's pattern: every acquire, present, present-wait and display-event wait succeeds;
    // each frame gets its own NVAPI Present of the back-buffer it acquired; each event fires at the refresh after it
    // was registered; the event fences never reach the driver but to be made and destroyed.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    const std::vector<VkImage> images = m.images(swapchain);
    REQUIRE(images.size() == 3);
    const std::set<VkImage> distinct(images.begin(), images.end());
    CHECK(distinct.size() == 3);

    constexpr uint64_t kFrames = 300;
    std::vector<uint64_t> acquired_tags;
    {
        VblankThread vblank(m);
        for (uint64_t id = 1; id <= kFrames; ++id) {
            const uint32_t index = m.frame(swapchain, id);
            acquired_tags.push_back(0x100 + index); // the FakeApi's tag of back-buffer `index`
            if (id > 1) {
                vblank.presented();
            }
        }
        vblank.stop();

        REQUIRE(vblank.waits.size() == kFrames - 1);
        std::vector<int64_t> late;
        for (const VblankThread::Wait& w : vblank.waits) {
            CHECK(w.result == VK_SUCCESS);
            CHECK(w.stamp >= w.registered);
            // The event fired at a refresh after its registration (the test reads the clock just after the layer
            // did), and the wait returned soon after that refresh.
            const int64_t flip = flip_at_or_before(w.stamp);
            CHECK(flip + 1'000'000 > w.registered);
            late.push_back(w.stamp - flip);
        }
        std::sort(late.begin(), late.end());
        INFO("median wake after the refresh: " << late[late.size() / 2] << " ns");
        CHECK(late[late.size() / 2] < 2'000'000);

        // The event fences: made and destroyed by the driver, never waited on, read or reset there.
        for (const VblankThread::Wait& w : vblank.waits) {
            CHECK(g_driver.count_on("vkCreateFence", handle_value(w.fence)) == 1);
            CHECK(g_driver.count_on("vkDestroyFence", handle_value(w.fence)) == 1);
            CHECK(g_driver.count_on("vkWaitForFences", handle_value(w.fence)) == 0);
            CHECK(g_driver.count_on("vkGetFenceStatus", handle_value(w.fence)) == 0);
            CHECK(g_driver.count_on("vkResetFences", handle_value(w.fence)) == 0);
        }
    }

    // One NVAPI Present per frame, of the back-buffer acquired for it, in order.
    CHECK(present_tags(m.rig.api) == acquired_tags);

    // The driver never saw our swapchain: acquire, present and present-wait were the layer's alone.
    for (const char* name : {"vkAcquireNextImageKHR", "vkQueuePresentKHR", "vkWaitForPresentKHR"}) {
        INFO(name);
        CHECK(g_driver.count_on(name, handle_value(swapchain)) == 0);
        CHECK(g_driver.count(name) == 0);
    }

    // The acquire semaphore was signalled on the private queue (one empty submit each), and every present was one
    // submit on Monado's queue waiting on its semaphore at ALL_COMMANDS and signalling a frame fence.
    const VkQueue private_queue = reinterpret_cast<VkQueue>(&g_driver.queue_objects.front());
    REQUIRE(private_queue != m.queue);
    const std::vector<dvl_test::FakeDriver::Submit> on_private = g_driver.submits_on(private_queue);
    CHECK(on_private.size() == kFrames);
    for (const auto& s : on_private) {
        CHECK(s.signals == 1);
        CHECK(s.waits == 0);
    }
    const std::vector<dvl_test::FakeDriver::Submit> on_monado = g_driver.submits_on(m.queue);
    CHECK(on_monado.size() == kFrames);
    std::set<VkFence> frame_fences;
    for (const auto& s : on_monado) {
        CHECK(s.waits == 1);
        CHECK(s.stages == std::vector<VkPipelineStageFlags>{VK_PIPELINE_STAGE_ALL_COMMANDS_BIT});
        CHECK(s.fence != VK_NULL_HANDLE);
        frame_fences.insert(s.fence);
    }
    CHECK(frame_fences.size() <= nvdm::kFencePoolCap); // the pool's fences, reused
    CHECK(g_driver.bad_fence_uses == 0);

    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
    CHECK(g_driver.live_fences() == 0); // every frame fence destroyed with the device
    CHECK(g_driver.live_images.empty());
    CHECK(g_driver.live_memory.empty());
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("driving_the_headset_keeps_monados_timer_resolution_when_its_console_is_hidden")
{
    // Design 6.7, Monado's timers (2026-11-02): with Monado's console minimized, Windows 11 ignored its
    // timeBeginPeriod(1), its compositor slept to the 15.6 ms tick and missed a third of the refreshes. Once the layer
    // drives the headset (the first swapchain), the process is out of timer-resolution throttling. ctest runs each test
    // case in a process of its own, so this is the layer's doing.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    PROCESS_POWER_THROTTLING_STATE state{};
    state.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
    REQUIRE(GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, sizeof(state)));
    CHECK((state.ControlMask & PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION) != 0); // the layer decides...
    CHECK((state.StateMask & PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION) == 0);   // ...not throttled
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("recreate_with_old_swapchain_moves_images")
{
    Monado m;
    const VkSwapchainKHR a = m.make_swapchain();
    const std::vector<VkImage> images_a = m.images(a);
    m.frame(a, 1);
    const size_t made = g_driver.images_made.size();

    // The same parameters: the new swapchain wraps the same images and memory, with no second import.
    const VkSwapchainKHR b = m.make_swapchain(a);
    CHECK(b != a);
    CHECK(m.images(b) == images_a);
    CHECK(g_driver.images_made.size() == made);

    // Ids are per swapchain record: b starts from nothing, a keeps what it completed.
    CHECK(m.wait_for_present(m.device, b, 1, 0) == VK_TIMEOUT);
    CHECK(m.wait_for_present(m.device, a, 1, 0) == VK_SUCCESS);

    // The old one goes: its images stay (b has them), and the display thread runs on for b.
    g_driver.clear_calls();
    m.destroy_swapchain(m.device, a, nullptr);
    CHECK(g_driver.count("vkDestroyImage") == 0);
    CHECK(g_driver.count("vkFreeMemory") == 0);
    CHECK(stats_reads_during(m.rig.api, 60) > 0);
    m.frame(b, 2);
    m.frame(b, 3);

    // Other parameters (Monado's graphics compositor: SRGB, no storage): imported again, the old images untouched.
    VkSwapchainCreateInfoKHR ci = m.swapchain_info(b);
    ci.imageFormat = VK_FORMAT_R8G8B8A8_SRGB;
    ci.imageUsage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
    VkSwapchainKHR c = VK_NULL_HANDLE;
    REQUIRE(m.create_swapchain(m.device, &ci, nullptr, &c) == VK_SUCCESS);
    CHECK(g_driver.images_made.size() == made + 3);
    const std::vector<VkImage> images_c = m.images(c);
    for (const VkImage image : images_c) {
        CHECK(std::find(images_a.begin(), images_a.end(), image) == images_a.end());
    }
    m.frame(c, 1);

    // b goes: it owned a's images, now released; c keeps its own.
    g_driver.clear_calls();
    m.destroy_swapchain(m.device, b, nullptr);
    CHECK(g_driver.count("vkDestroyImage") == 3);
    CHECK(g_driver.count("vkFreeMemory") == 3);
    for (const VkImage image : images_a) {
        CHECK(g_driver.count_on("vkDestroyImage", handle_value(image)) == 1);
    }
    m.destroy_swapchain(m.device, c, nullptr);
    CHECK(g_driver.live_images.empty());
    CHECK(g_driver.live_memory.empty());
    m.destroy_device();
}

TEST_CASE("destroying_the_last_swapchain_stops_the_display_thread")
{
    Monado m;
    CHECK(stats_reads_during(m.rig.api, 50) == 0); // no swapchain, no display thread
    const VkSwapchainKHR a = m.make_swapchain();
    CHECK(stats_reads_during(m.rig.api, 60) > 0); // the first swapchain started it
    const VkSwapchainKHR b = m.make_swapchain(a);
    m.destroy_swapchain(m.device, a, nullptr);
    CHECK(stats_reads_during(m.rig.api, 60) > 0); // b still exists
    m.frame(b, 1);
    m.destroy_swapchain(m.device, b, nullptr);
    CHECK(stats_reads_during(m.rig.api, 80) == 0); // the last one stopped it, joined

    // A new swapchain starts a new epoch, and frames run again.
    const VkSwapchainKHR c = m.make_swapchain();
    m.frame(c, 1);
    m.frame(c, 2);
    m.destroy_swapchain(m.device, c, nullptr);
    CHECK(stats_reads_during(m.rig.api, 80) == 0);
    m.destroy_device();
}

TEST_CASE("destroy_device_tears_down_in_order")
{
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    for (uint64_t id = 1; id <= 20; ++id) {
        m.frame(swapchain, id);
    }
    // A frame queued whose GPU work never ends by itself: its fence is outstanding at teardown.
    g_driver.hold_fences = true;
    uint32_t index = 0;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    REQUIRE(m.present_one(swapchain, index, 21) == VK_SUCCESS);
    m.destroy_swapchain(m.device, swapchain, nullptr);
    CHECK(stats_reads_during(m.rig.api, 50) == 0);

    g_driver.clear_calls();
    const size_t fences = g_driver.live_fences();
    REQUIRE(fences > 0);
    m.destroy_device();
    const std::vector<std::string> order = g_driver.names(
        {"vkWaitForFences", "vkQueueWaitIdle", "vkDestroyFence", "vkDestroyDevice", "vkDestroyImage"});
    // Design 6.10: outstanding frame fences waited for (at most 100 ms), the private queue idled, the frame fences
    // destroyed, then the device.
    REQUIRE_FALSE(order.empty());
    CHECK(order.front() == "vkWaitForFences");
    CHECK(order.back() == "vkDestroyDevice");
    const auto idle = std::find(order.begin(), order.end(), "vkQueueWaitIdle");
    REQUIRE(idle != order.end());
    const auto first_destroy = std::find(order.begin(), order.end(), "vkDestroyFence");
    REQUIRE(first_destroy != order.end());
    CHECK(idle < first_destroy);
    CHECK(std::count(order.begin(), order.end(), "vkDestroyFence") == static_cast<long>(fences));
    CHECK(std::count(order.begin(), order.end(), "vkDestroyImage") == 0); // released with the swapchain already
    const VkQueue private_queue = reinterpret_cast<VkQueue>(&g_driver.queue_objects.front());
    CHECK(g_driver.last("vkQueueWaitIdle").handle == handle_value(private_queue));
    CHECK(g_driver.live_fences() == 0);
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("destroy_device_with_its_swapchain_still_alive")
{
    // An application error, but the device's swapchain is not left behind: destroyed first (images released, the
    // display thread stopped), then the device's teardown.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    m.frame(swapchain, 1);
    m.frame(swapchain, 2);
    g_driver.clear_calls();
    m.destroy_device();
    const std::vector<std::string> order =
        g_driver.names({"vkDestroyImage", "vkQueueWaitIdle", "vkDestroyFence", "vkDestroyDevice"});
    REQUIRE_FALSE(order.empty());
    CHECK(order.front() == "vkDestroyImage");
    CHECK(order.back() == "vkDestroyDevice");
    CHECK(std::count(order.begin(), order.end(), "vkDestroyImage") == 3);
    CHECK(g_driver.live_images.empty());
    CHECK(g_driver.live_fences() == 0);
    CHECK(stats_reads_during(m.rig.api, 50) == 0);
}

TEST_CASE("present_mixing_our_swapchain_with_another_is_refused")
{
    Monado m;
    const VkSwapchainKHR ours = m.make_swapchain();
    VkSwapchainCreateInfoKHR peek = m.swapchain_info();
    peek.surface = dvl_test::kPeekSurface;
    VkSwapchainKHR theirs = VK_NULL_HANDLE;
    REQUIRE(m.create_swapchain(m.device, &peek, nullptr, &theirs) == VK_SUCCESS);

    uint32_t index = 0;
    REQUIRE(m.acquire(m.device, ours, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    g_driver.clear_calls();
    const size_t presents = m.rig.api.count("present");
    const VkSwapchainKHR both[] = {theirs, ours};
    const uint32_t indices[] = {1, index};
    VkResult results[2] = {VK_SUCCESS, VK_SUCCESS};
    VkPresentInfoKHR info{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
    info.swapchainCount = 2;
    info.pSwapchains = both;
    info.pImageIndices = indices;
    info.pResults = results;
    CHECK(m.present(m.queue, &info) == VK_ERROR_UNKNOWN);
    CHECK(results[0] == VK_ERROR_UNKNOWN);
    CHECK(results[1] == VK_ERROR_UNKNOWN);
    CHECK(g_driver.count("vkQueuePresentKHR") == 0); // nothing went down: the driver never sees our handle
    CHECK(g_driver.count("vkQueueSubmit") == 0);
    std::this_thread::sleep_for(std::chrono::milliseconds(30));
    CHECK(m.rig.api.count("present") == presents);

    // The acquired image is still ours to present alone.
    REQUIRE(m.present_one(ours, index, 1) == VK_SUCCESS);
    CHECK(m.wait_for_present(m.device, ours, 1, 500 * kMs) == VK_SUCCESS);
    m.destroy_swapchain(m.device, theirs, nullptr);
    m.destroy_swapchain(m.device, ours, nullptr);
    m.destroy_device();
}

TEST_CASE("foreign_swapchains_and_fences_go_down")
{
    // Monado's peek window: a Win32 surface and a driver swapchain on the same device. Every call on it, and every
    // ordinary fence, goes down unchanged; no display thread runs for it.
    Monado m;
    VkSwapchainCreateInfoKHR peek = m.swapchain_info();
    peek.surface = dvl_test::kPeekSurface;
    VkSwapchainKHR theirs = VK_NULL_HANDLE;
    REQUIRE(m.create_swapchain(m.device, &peek, nullptr, &theirs) == VK_SUCCESS);
    CHECK(g_driver.count_on("vkCreateSwapchainKHR", handle_value(dvl_test::kPeekSurface)) == 1);
    CHECK(theirs == dvl_test::fake::driver_swapchain(0));
    CHECK(m.images(theirs).size() == 2);
    uint32_t index = 0;
    CHECK(m.acquire(m.device, theirs, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    CHECK(index == 1);
    VkPresentInfoKHR info{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
    info.swapchainCount = 1;
    info.pSwapchains = &theirs;
    info.pImageIndices = &index;
    CHECK(m.present(m.queue, &info) == VK_SUCCESS);
    CHECK(m.wait_for_present(m.device, theirs, 1, 0) == VK_SUCCESS);
    CHECK(g_driver.count_on("vkQueuePresentKHR", handle_value(theirs)) == 1);
    CHECK(g_driver.count_on("vkAcquireNextImageKHR", handle_value(theirs)) == 1);
    CHECK(g_driver.count_on("vkWaitForPresentKHR", handle_value(theirs)) == 1);
    CHECK(m.rig.api.count("present_stats") == 0);
    m.destroy_swapchain(m.device, theirs, nullptr);
    CHECK(g_driver.count_on("vkDestroySwapchainKHR", handle_value(theirs)) == 1);

    // Ordinary fences, with no display event registered and with one registered.
    VkFenceCreateInfo fci{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    fci.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    VkFence fence = VK_NULL_HANDLE;
    REQUIRE(m.create_fence(m.device, &fci, nullptr, &fence) == VK_SUCCESS);
    for (int pass = 0; pass < 2; ++pass) {
        VkFence event = VK_NULL_HANDLE;
        if (pass == 1) {
            VkDisplayEventInfoEXT ei{VK_STRUCTURE_TYPE_DISPLAY_EVENT_INFO_EXT};
            REQUIRE(m.register_event(m.device, m.display, &ei, nullptr, &event) == VK_SUCCESS);
        }
        g_driver.clear_calls();
        CHECK(m.wait_fences(m.device, 1, &fence, VK_TRUE, 0) == VK_SUCCESS);
        CHECK(m.fence_status(m.device, fence) == VK_SUCCESS);
        CHECK(m.reset_fences(m.device, 1, &fence) == VK_SUCCESS);
        CHECK(m.fence_status(m.device, fence) == VK_NOT_READY);
        CHECK(g_driver.count_on("vkWaitForFences", handle_value(fence)) == 1);
        CHECK(g_driver.count_on("vkGetFenceStatus", handle_value(fence)) == 2);
        CHECK(g_driver.count_on("vkResetFences", handle_value(fence)) == 1);
        if (event != VK_NULL_HANDLE) {
            m.destroy_fence(m.device, event, nullptr);
        }
        // Signalled again for the next pass, the driver's way.
        VkFence again = VK_NULL_HANDLE;
        REQUIRE(m.create_fence(m.device, &fci, nullptr, &again) == VK_SUCCESS);
        m.destroy_fence(m.device, fence, nullptr);
        fence = again;
    }
    m.destroy_fence(m.device, fence, nullptr);
    CHECK(g_driver.live_fences() == 0);
    m.destroy_device();
}

TEST_CASE("display_event_fences_are_answered_by_the_layer")
{
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    VkDisplayEventInfoEXT ei{VK_STRUCTURE_TYPE_DISPLAY_EVENT_INFO_EXT};
    ei.displayEvent = VK_DISPLAY_EVENT_TYPE_FIRST_PIXEL_OUT_EXT;

    // Not signalled before the next refresh; signalled by it; vkResetFences unsignals it for good.
    VkFence event = VK_NULL_HANDLE;
    REQUIRE(m.register_event(m.device, m.display, &ei, nullptr, &event) == VK_SUCCESS);
    const VkResult early = m.fence_status(m.device, event);
    CHECK((early == VK_NOT_READY || early == VK_SUCCESS)); // a refresh may land right after the registration
    CHECK(m.wait_fences(m.device, 1, &event, VK_TRUE, 1'000'000'000) == VK_SUCCESS);
    CHECK(m.fence_status(m.device, event) == VK_SUCCESS);
    CHECK(m.reset_fences(m.device, 1, &event) == VK_SUCCESS);
    CHECK(m.fence_status(m.device, event) == VK_NOT_READY);
    CHECK(m.wait_fences(m.device, 1, &event, VK_TRUE, 30 * kMs) == VK_TIMEOUT); // the wait honours the timeout
    m.destroy_fence(m.device, event, nullptr);
    CHECK(g_driver.count_on("vkDestroyFence", handle_value(event)) == 1); // the real fence goes down
    CHECK(g_driver.count_on("vkWaitForFences", handle_value(event)) == 0);
    CHECK(g_driver.count_on("vkGetFenceStatus", handle_value(event)) == 0);
    CHECK(g_driver.count_on("vkResetFences", handle_value(event)) == 0);

    // A list mixing an event and an ordinary fence: split, the ordinary one down alone, "wait all" and "wait any".
    VkFenceCreateInfo fci{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    fci.flags = VK_FENCE_CREATE_SIGNALED_BIT;
    VkFence ordinary = VK_NULL_HANDLE;
    REQUIRE(m.create_fence(m.device, &fci, nullptr, &ordinary) == VK_SUCCESS);
    REQUIRE(m.register_event(m.device, m.display, &ei, nullptr, &event) == VK_SUCCESS);
    const VkFence mixed[] = {event, ordinary};
    CHECK(m.wait_fences(m.device, 2, mixed, VK_FALSE, 1'000'000'000) == VK_SUCCESS); // the ordinary one is signalled
    CHECK(m.wait_fences(m.device, 2, mixed, VK_TRUE, 1'000'000'000) == VK_SUCCESS);  // and the event at its refresh
    CHECK(m.fence_status(m.device, event) == VK_SUCCESS);
    CHECK(g_driver.count_on("vkWaitForFences", handle_value(event)) == 0);
    CHECK(g_driver.count_on("vkWaitForFences", handle_value(ordinary)) >= 2);
    const VkFence both[] = {event, ordinary};
    CHECK(m.reset_fences(m.device, 2, both) == VK_SUCCESS);
    CHECK(g_driver.count_on("vkResetFences", handle_value(ordinary)) == 1);
    CHECK(g_driver.count_on("vkResetFences", handle_value(event)) == 0);
    m.destroy_fence(m.device, event, nullptr);
    m.destroy_fence(m.device, ordinary, nullptr);

    // The counter and the other display-control commands.
    const auto counter = m.fn<PFN_vkGetSwapchainCounterEXT>("vkGetSwapchainCounterEXT");
    uint64_t first = 0;
    uint64_t later = 0;
    REQUIRE(counter(m.device, swapchain, VK_SURFACE_COUNTER_VBLANK_BIT_EXT, &first) == VK_SUCCESS);
    std::this_thread::sleep_for(std::chrono::milliseconds(60));
    REQUIRE(counter(m.device, swapchain, VK_SURFACE_COUNTER_VBLANK_BIT_EXT, &later) == VK_SUCCESS);
    CHECK(later > first); // the flip count runs while the display thread does
    const auto power = m.fn<PFN_vkDisplayPowerControlEXT>("vkDisplayPowerControlEXT");
    VkDisplayPowerInfoEXT on{VK_STRUCTURE_TYPE_DISPLAY_POWER_INFO_EXT};
    on.powerState = VK_DISPLAY_POWER_STATE_ON_EXT;
    CHECK(power(m.device, m.display, &on) == VK_SUCCESS);
    const VkDisplayKHR not_ours = dvl_test::driver_display(0, 0);
    CHECK(power(m.device, not_ours, &on) == VK_ERROR_UNKNOWN);
    VkFence none = VK_NULL_HANDLE;
    CHECK(m.register_event(m.device, not_ours, &ei, nullptr, &none) == VK_ERROR_UNKNOWN);
    CHECK(none == VK_NULL_HANDLE);
    const auto device_event = m.fn<PFN_vkRegisterDeviceEventEXT>("vkRegisterDeviceEventEXT");
    VkDeviceEventInfoEXT de{VK_STRUCTURE_TYPE_DEVICE_EVENT_INFO_EXT};
    CHECK(device_event(m.device, &de, nullptr, &none) == VK_ERROR_UNKNOWN);

    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
    CHECK(g_driver.live_fences() == 0);
}

TEST_CASE("acquire_with_a_finite_timeout")
{
    // Three back-buffers, nothing on screen yet: three acquires succeed; a fourth (more than Vulkan allows with an
    // infinite timeout) gives VK_NOT_READY for 0 and VK_TIMEOUT for a finite timeout, after it.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    std::set<uint32_t> indices;
    for (int i = 0; i < 3; ++i) {
        uint32_t index = UINT32_MAX;
        REQUIRE(m.acquire(m.device, swapchain, 0, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
        indices.insert(index);
    }
    CHECK(indices == std::set<uint32_t>{0, 1, 2});
    uint32_t index = UINT32_MAX;
    CHECK(m.acquire(m.device, swapchain, 0, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_NOT_READY);
    const auto start = std::chrono::steady_clock::now();
    CHECK(m.acquire(m.device, swapchain, 40 * kMs, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_TIMEOUT);
    CHECK(std::chrono::steady_clock::now() - start >= std::chrono::milliseconds(39));

    // vkAcquireNextImage2KHR, and an acquire fence signalled on the private queue.
    for (uint32_t i : {0u, 1u}) {
        REQUIRE(m.present_one(swapchain, i, i + 1) == VK_SUCCESS);
    }
    CHECK(m.wait_for_present(m.device, swapchain, 2, 500 * kMs) == VK_SUCCESS);
    VkFenceCreateInfo fci{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    VkFence fence = VK_NULL_HANDLE;
    REQUIRE(m.create_fence(m.device, &fci, nullptr, &fence) == VK_SUCCESS);
    VkAcquireNextImageInfoKHR ai{VK_STRUCTURE_TYPE_ACQUIRE_NEXT_IMAGE_INFO_KHR};
    ai.swapchain = swapchain;
    ai.timeout = kForever;
    ai.fence = fence;
    ai.deviceMask = 1;
    g_driver.clear_calls();
    REQUIRE(m.fn<PFN_vkAcquireNextImage2KHR>("vkAcquireNextImage2KHR")(m.device, &ai, &index) == VK_SUCCESS);
    CHECK(index < 3);
    CHECK(g_driver.count_on("vkQueueSubmit", handle_value(fence)) == 1);
    CHECK(m.fence_status(m.device, fence) == VK_SUCCESS);
    m.destroy_fence(m.device, fence, nullptr);
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("swapchain_refusals_are_design_6_10s_exceptions")
{
    Monado m;
    // An SRGB swapchain with storage usage cannot be made on this driver.
    VkSwapchainCreateInfoKHR srgb = m.swapchain_info();
    srgb.imageFormat = VK_FORMAT_R8G8B8A8_SRGB;
    srgb.imageUsage = VK_IMAGE_USAGE_STORAGE_BIT;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    CHECK(m.create_swapchain(m.device, &srgb, nullptr, &swapchain) == VK_ERROR_INITIALIZATION_FAILED);

    // A device on another GPU (not the display's) gets none.
    dvl_test::MonadoQueue monado;
    VkDevice other = VK_NULL_HANDLE;
    REQUIRE(m.rig.create_device(1, {monado.info}, {"VK_KHR_swapchain"}, &other) == VK_SUCCESS);
    const VkSwapchainCreateInfoKHR ci = m.swapchain_info();
    CHECK(m.rig.device_fn<PFN_vkCreateSwapchainKHR>(other, "vkCreateSwapchainKHR")(other, &ci, nullptr, &swapchain) ==
          VK_ERROR_INITIALIZATION_FAILED);

    // A second device of the display's GPU, made after the display query: its trial import runs now and passes, but
    // while the first device has a swapchain it gets none.
    VkDevice second = VK_NULL_HANDLE;
    REQUIRE(m.rig.create_device(0, {monado.info}, {"VK_KHR_swapchain"}, &second) == VK_SUCCESS);
    const VkSwapchainKHR first = m.make_swapchain();
    const auto create_on_second = m.rig.device_fn<PFN_vkCreateSwapchainKHR>(second, "vkCreateSwapchainKHR");
    CHECK(create_on_second(second, &ci, nullptr, &swapchain) == VK_ERROR_INITIALIZATION_FAILED);

    // Once the first device's swapchain is gone, the second device can present (its frames, its fences).
    m.frame(first, 1);
    m.destroy_swapchain(m.device, first, nullptr);
    REQUIRE(create_on_second(second, &ci, nullptr, &swapchain) == VK_SUCCESS);
    const VkQueue queue = m.rig.get_queue(second, 2, 0);
    uint32_t index = 0;
    REQUIRE(m.acquire(second, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    uint64_t id = 1;
    VkPresentIdKHR pid{VK_STRUCTURE_TYPE_PRESENT_ID_KHR};
    pid.swapchainCount = 1;
    pid.pPresentIds = &id;
    VkPresentInfoKHR info{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR};
    info.pNext = &pid;
    info.swapchainCount = 1;
    info.pSwapchains = &swapchain;
    info.pImageIndices = &index;
    REQUIRE(m.present(queue, &info) == VK_SUCCESS);
    CHECK(m.wait_for_present(second, swapchain, 1, 500 * kMs) == VK_SUCCESS);
    m.rig.device_fn<PFN_vkDestroySwapchainKHR>(second, "vkDestroySwapchainKHR")(second, swapchain, nullptr);
    m.rig.destroy_device(second);
    m.rig.destroy_device(other);
    m.destroy_device();
    CHECK(g_driver.live_fences() == 0);
    CHECK(g_driver.live_images.empty());
}

TEST_CASE("misc_commands_on_our_handles")
{
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    g_driver.clear_calls();

    // Debug names: accepted for ours, not passed down; others go down.
    const auto name = m.fn<PFN_vkSetDebugUtilsObjectNameEXT>("vkSetDebugUtilsObjectNameEXT");
    VkDebugUtilsObjectNameInfoEXT ni{VK_STRUCTURE_TYPE_DEBUG_UTILS_OBJECT_NAME_INFO_EXT};
    ni.pObjectName = "headset";
    for (const auto& object : {std::make_pair(VK_OBJECT_TYPE_SWAPCHAIN_KHR, handle_value(swapchain)),
                               std::make_pair(VK_OBJECT_TYPE_SURFACE_KHR, handle_value(m.surface)),
                               std::make_pair(VK_OBJECT_TYPE_DISPLAY_KHR, handle_value(m.display))}) {
        ni.objectType = object.first;
        ni.objectHandle = object.second;
        CHECK(name(m.device, &ni) == VK_SUCCESS);
    }
    CHECK(g_driver.count("vkSetDebugUtilsObjectNameEXT") == 0);
    ni.objectType = VK_OBJECT_TYPE_IMAGE;
    ni.objectHandle = handle_value(m.images(swapchain)[0]); // the images are the driver's
    CHECK(name(m.device, &ni) == VK_SUCCESS);
    CHECK(g_driver.count("vkSetDebugUtilsObjectNameEXT") == 1);

    // Private data: kept by the layer for ours, the driver's for the rest.
    const auto set = m.fn<PFN_vkSetPrivateData>("vkSetPrivateData");
    const auto get = m.fn<PFN_vkGetPrivateData>("vkGetPrivateData");
    const VkPrivateDataSlot slot = fake_handle<VkPrivateDataSlot>(0x5107);
    CHECK(set(m.device, VK_OBJECT_TYPE_SWAPCHAIN_KHR, handle_value(swapchain), slot, 42) == VK_SUCCESS);
    uint64_t value = 0;
    get(m.device, VK_OBJECT_TYPE_SWAPCHAIN_KHR, handle_value(swapchain), slot, &value);
    CHECK(value == 42);
    get(m.device, VK_OBJECT_TYPE_SURFACE_KHR, handle_value(m.surface), slot, &value);
    CHECK(value == 0);
    CHECK(g_driver.count("vkSetPrivateData") == 0);
    CHECK(g_driver.count("vkGetPrivateData") == 0);
    get(m.device, VK_OBJECT_TYPE_IMAGE, 0x1234, slot, &value);
    CHECK(value == 77);
    CHECK(g_driver.count("vkGetPrivateData") == 1);

    // An image made for our swapchain is refused; an ordinary image goes down.
    VkImageSwapchainCreateInfoKHR for_swapchain{VK_STRUCTURE_TYPE_IMAGE_SWAPCHAIN_CREATE_INFO_KHR};
    for_swapchain.swapchain = swapchain;
    VkImageCreateInfo ici{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ici.pNext = &for_swapchain;
    ici.imageType = VK_IMAGE_TYPE_2D;
    ici.format = VK_FORMAT_R8G8B8A8_UNORM;
    ici.extent = {64, 64, 1};
    ici.mipLevels = 1;
    ici.arrayLayers = 1;
    ici.samples = VK_SAMPLE_COUNT_1_BIT;
    ici.usage = VK_IMAGE_USAGE_SAMPLED_BIT;
    const auto create_image = m.fn<PFN_vkCreateImage>("vkCreateImage");
    const auto destroy_image = m.fn<PFN_vkDestroyImage>("vkDestroyImage");
    VkImage image = VK_NULL_HANDLE;
    const size_t images = g_driver.images_made.size();
    CHECK(create_image(m.device, &ici, nullptr, &image) == VK_ERROR_UNKNOWN);
    CHECK(g_driver.images_made.size() == images);
    ici.pNext = nullptr;
    CHECK(create_image(m.device, &ici, nullptr, &image) == VK_SUCCESS);
    CHECK(g_driver.images_made.size() == images + 1);
    destroy_image(m.device, image, nullptr);

    // GOOGLE display timing, shared swapchains, the status, HDR metadata, full-screen exclusive.
    VkRefreshCycleDurationGOOGLE refresh{};
    CHECK(m.fn<PFN_vkGetRefreshCycleDurationGOOGLE>("vkGetRefreshCycleDurationGOOGLE")(m.device, swapchain,
                                                                                         &refresh) == VK_SUCCESS);
    CHECK(refresh.refreshDuration == static_cast<uint64_t>(kRefresh));
    uint32_t timings = 5;
    CHECK(m.fn<PFN_vkGetPastPresentationTimingGOOGLE>("vkGetPastPresentationTimingGOOGLE")(m.device, swapchain,
                                                                                             &timings, nullptr) ==
          VK_SUCCESS);
    CHECK(timings == 0);
    const VkSwapchainCreateInfoKHR ci = m.swapchain_info();
    VkSwapchainKHR shared = VK_NULL_HANDLE;
    CHECK(m.fn<PFN_vkCreateSharedSwapchainsKHR>("vkCreateSharedSwapchainsKHR")(m.device, 1, &ci, nullptr, &shared) ==
          VK_ERROR_INITIALIZATION_FAILED);
    CHECK(m.fn<PFN_vkGetSwapchainStatusKHR>("vkGetSwapchainStatusKHR")(m.device, swapchain) == VK_SUCCESS);
    VkHdrMetadataEXT hdr{VK_STRUCTURE_TYPE_HDR_METADATA_EXT};
    m.fn<PFN_vkSetHdrMetadataEXT>("vkSetHdrMetadataEXT")(m.device, 1, &swapchain, &hdr);
    CHECK(m.fn<PFN_vkAcquireFullScreenExclusiveModeEXT>("vkAcquireFullScreenExclusiveModeEXT")(m.device, swapchain) ==
          VK_SUCCESS);

    // vkReleaseSwapchainImagesEXT: an acquired image goes back, so all three can be acquired again.
    uint32_t a = 0;
    uint32_t b = 0;
    uint32_t c = 0;
    REQUIRE(m.acquire(m.device, swapchain, 0, kAcquireSemaphore, VK_NULL_HANDLE, &a) == VK_SUCCESS);
    REQUIRE(m.acquire(m.device, swapchain, 0, kAcquireSemaphore, VK_NULL_HANDLE, &b) == VK_SUCCESS);
    REQUIRE(m.acquire(m.device, swapchain, 0, kAcquireSemaphore, VK_NULL_HANDLE, &c) == VK_SUCCESS);
    VkReleaseSwapchainImagesInfoEXT release{VK_STRUCTURE_TYPE_RELEASE_SWAPCHAIN_IMAGES_INFO_EXT};
    release.swapchain = swapchain;
    release.imageIndexCount = 1;
    release.pImageIndices = &b;
    CHECK(m.fn<PFN_vkReleaseSwapchainImagesEXT>("vkReleaseSwapchainImagesEXT")(m.device, &release) == VK_SUCCESS);
    uint32_t again = UINT32_MAX;
    CHECK(m.acquire(m.device, swapchain, 0, kAcquireSemaphore, VK_NULL_HANDLE, &again) == VK_SUCCESS);
    CHECK(again == b);
    // Its KHR name (VK_KHR_swapchain_maintenance1) does the same.
    VkReleaseSwapchainImagesInfoKHR release_khr{VK_STRUCTURE_TYPE_RELEASE_SWAPCHAIN_IMAGES_INFO_KHR};
    release_khr.swapchain = swapchain;
    release_khr.imageIndexCount = 1;
    release_khr.pImageIndices = &again;
    CHECK(m.fn<PFN_vkReleaseSwapchainImagesKHR>("vkReleaseSwapchainImagesKHR")(m.device, &release_khr) == VK_SUCCESS);
    uint32_t once_more = UINT32_MAX;
    CHECK(m.acquire(m.device, swapchain, 0, kAcquireSemaphore, VK_NULL_HANDLE, &once_more) == VK_SUCCESS);
    CHECK(once_more == again);
    CHECK(g_driver.count("vkReleaseSwapchainImagesKHR") == 0);

    // None of it reached the driver with our handles.
    for (const std::string& n : {std::string("vkCreateSwapchainKHR"), std::string("vkQueuePresentKHR"),
                                 std::string("vkDestroySwapchainKHR")}) {
        CHECK(g_driver.count(n) == 0);
    }
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

// --- Task H: the paths a bug would skew the measurements on, or hang Monado on ----------------------------------

TEST_CASE("a_display_event_wait_leaves_pending_lines_for_later")
{
    // Monado's vblank thread stamps the vblank when vkWaitForFences returns, so the wait must not write the log's
    // pending lines (file, console, debugger) on its way out (Task 11 review). They are written later: by the next
    // intercept that is not a wait, or by the display thread before one of its own waits.
    Monado m;
    LogCapture log(m.rig);
    REQUIRE(log.ready());
    const DWORD self = GetCurrentThreadId();

    // No swapchain, so no display thread: nothing else writes the line, and nothing sets the event.
    VkFence event = m.display_event();
    REQUIRE(m.rig.add_pending_line("pending line 0") == 1);
    CHECK(m.wait_fences(m.device, 1, &event, VK_TRUE, 20 * kMs) == VK_TIMEOUT);
    CHECK(log.count("pending line 0") == 0);
    CHECK(m.fence_status(m.device, event) == VK_NOT_READY); // a look at the event is a wait too
    CHECK(log.count("pending line 0") == 0);
    m.destroy_fence(m.device, event, nullptr); // not a wait: it writes the line
    CHECK(log.count("pending line 0", self) == 1);

    // The display thread runs: each event is set at the next refresh, and its wait returns without writing the line
    // left pending before it. By the time the fence is destroyed the line has been written, once, by whichever came
    // first.
    const VkSwapchainKHR swapchain = m.make_swapchain();
    for (int pass = 1; pass <= 20; ++pass) {
        const std::string line = "pending line " + std::to_string(pass) + ".";
        event = m.display_event();
        REQUIRE(m.rig.add_pending_line(line.c_str()) == 1);
        CHECK(m.wait_fences(m.device, 1, &event, VK_TRUE, 1'000'000'000) == VK_SUCCESS);
        CHECK(log.count(line, self) == 0);
        m.destroy_fence(m.device, event, nullptr);
        CHECK(log.count(line) == 1);
    }

    // The render loop's present-wait is a wait for a refresh too; the acquire and the present are not.
    REQUIRE(m.rig.add_pending_line("pending line before acquire") == 1);
    uint32_t index = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    CHECK(log.count("pending line before acquire") == 1);
    REQUIRE(m.rig.add_pending_line("pending line before present") == 1);
    REQUIRE(m.present_one(swapchain, index, 1) == VK_SUCCESS);
    CHECK(log.count("pending line before present") == 1);
    REQUIRE(m.rig.add_pending_line("pending line before present-wait") == 1);
    CHECK(m.wait_for_present(m.device, swapchain, 1, 500 * kMs) == VK_SUCCESS);
    CHECK(log.count("pending line before present-wait", self) == 0);

    m.destroy_swapchain(m.device, swapchain, nullptr);
    CHECK(log.count("pending line before present-wait") == 1);
    m.destroy_device();
}

TEST_CASE("the_fence_fallback_line_is_written_at_most_once_a_second")
{
    // With every frame fence left over and unsignalled (the fake GPU holds them), a present waits a frame period on
    // the oldest, then falls back to vkQueueWaitIdle (design 6.5 invariant 2). Monado presents at 90 Hz holding its
    // queue lock, so that line comes at most once a second (Task 11 review); the warnings' once-per-app-session line
    // stays.
    Monado m;
    LogCapture log(m.rig);
    REQUIRE(log.ready());
    const VkSwapchainKHR swapchain = m.make_swapchain();
    g_driver.hold_fences = true;
    const auto fallbacks = [&] { return g_driver.count_on("vkQueueWaitIdle", handle_value(m.queue)); };
    // Presents of an image that is not acquired: nothing to show, so each frame fence is left over at once. One in
    // seventeen finds none of the sixteen free.
    uint64_t id = 0;
    const auto present_until = [&](size_t more) {
        const size_t target = fallbacks() + more;
        for (int n = 0; n < 200 && fallbacks() < target; ++n) {
            REQUIRE(m.present_one(swapchain, 0, ++id) == VK_SUCCESS);
        }
        REQUIRE(fallbacks() >= target);
    };
    const std::string line = "vkQueuePresentKHR: no frame fence was free";

    const Clock::time_point start = Clock::now();
    present_until(4);
    REQUIRE(Clock::now() - start < 900ms); // four fallbacks within a second...
    CHECK(log.count(line) == 1);           // ...one line

    std::this_thread::sleep_for(1100ms);
    present_until(1);
    CHECK(log.count(line) == 2); // a second later, the next one is written
    present_until(2);
    CHECK(log.count(line) == 2);
    // The warnings' line, once per app session (the pause above started a new one).
    CHECK(log.count("no frame fence was free for a present") >= 1);

    g_driver.hold_fences = false;
    g_driver.signal_held();
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("a_present_whose_submit_fails_releases_its_image_for_a_later_acquire")
{
    // Final review m2: after a failed vkQueuePresentKHR Monado acquires again and never presents that image. Its frame
    // is released, as a failed acquire's is (design 6.10), so the image comes back to a later acquire instead of
    // staying acquired for good. Here all three are acquired, so only the released one can come back.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    std::vector<uint32_t> held;
    for (int i = 0; i < 3; ++i) {
        uint32_t index = UINT32_MAX;
        REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
        held.push_back(index);
    }
    REQUIRE(std::set<uint32_t>(held.begin(), held.end()).size() == 3);

    g_driver.fail_next_call("vkQueueSubmit", VK_ERROR_OUT_OF_HOST_MEMORY);
    CHECK(m.present_one(swapchain, held[0], 1) == VK_ERROR_OUT_OF_HOST_MEMORY);
    uint32_t again = UINT32_MAX;
    CHECK(m.acquire(m.device, swapchain, 100 * kMs, kAcquireSemaphore, VK_NULL_HANDLE, &again) == VK_SUCCESS);
    CHECK(again == held[0]);

    // Monado's loop goes on with every image.
    REQUIRE(m.present_one(swapchain, held[0], 2) == VK_SUCCESS);
    REQUIRE(m.present_one(swapchain, held[1], 3) == VK_SUCCESS);
    REQUIRE(m.present_one(swapchain, held[2], 4) == VK_SUCCESS);
    CHECK(m.wait_for_present(m.device, swapchain, 4, 500 * kMs) == VK_SUCCESS);
    m.frame(swapchain, 5);

    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("a_present_whose_fallback_queue_wait_fails_releases_its_image_too")
{
    // The other error return of vkQueuePresentKHR: no frame fence was free (the fake GPU holds every one), the present
    // falls back to vkQueueWaitIdle (design 6.5 invariant 2), and that fails.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    g_driver.hold_fences = true;
    uint32_t first = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &first) == VK_SUCCESS);
    // Presents of an image that is not acquired: nothing to show, and each leaves its fence over, unsignalled.
    const uint32_t other = (first + 1) % 3;
    for (uint64_t id = 1; id <= nvdm::kFencePoolCap; ++id) {
        REQUIRE(m.present_one(swapchain, other, id) == VK_SUCCESS);
    }
    REQUIRE(g_driver.count_on("vkQueueWaitIdle", handle_value(m.queue)) == 0); // no fallback yet
    std::vector<uint32_t> held{first};
    for (int i = 0; i < 2; ++i) {
        uint32_t index = UINT32_MAX;
        REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
        held.push_back(index);
    }
    REQUIRE(std::set<uint32_t>(held.begin(), held.end()).size() == 3);

    g_driver.fail_next_call("vkQueueWaitIdle", VK_ERROR_DEVICE_LOST);
    CHECK(m.present_one(swapchain, first, 100) == VK_ERROR_DEVICE_LOST);
    CHECK(g_driver.count_on("vkQueueWaitIdle", handle_value(m.queue)) == 1); // it was the fallback that failed
    uint32_t again = UINT32_MAX;
    CHECK(m.acquire(m.device, swapchain, 100 * kMs, kAcquireSemaphore, VK_NULL_HANDLE, &again) == VK_SUCCESS);
    CHECK(again == first);

    g_driver.hold_fences = false;
    g_driver.signal_held();
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("latency_sleep_signals_its_semaphore_only_through_the_khr_command")
{
    // vkLatencySleepNV on the layer's swapchain does not sleep; it signals the application's semaphore from the host at
    // once, with vkSignalSemaphoreKHR (VK_KHR_timeline_semaphore). Never the core 1.2 vkSignalSemaphore (Monado's
    // instance is API 1.0, constraints): without the KHR command nothing is signalled, and one line says so.
    Monado m;
    LogCapture log(m.rig);
    REQUIRE(log.ready());
    const VkSwapchainKHR swapchain = m.make_swapchain();
    VkLatencySleepInfoNV info{VK_STRUCTURE_TYPE_LATENCY_SLEEP_INFO_NV};
    info.signalSemaphore = fake_handle<VkSemaphore>(0x5E3A0003);
    info.value = 7;
    g_driver.clear_calls();
    CHECK(m.fn<PFN_vkLatencySleepNV>("vkLatencySleepNV")(m.device, swapchain, &info) == VK_SUCCESS);
    CHECK(g_driver.count_on("vkSignalSemaphoreKHR", handle_value(info.signalSemaphore)) == 1);
    CHECK(g_driver.last("vkSignalSemaphoreKHR").index == 7);
    CHECK(g_driver.count("vkSignalSemaphore") == 0);
    CHECK(g_driver.count("vkLatencySleepNV") == 0);

    // A device whose driver has no vkSignalSemaphoreKHR (VK_KHR_timeline_semaphore not enabled).
    g_driver.offer_signal_semaphore_khr = false;
    dvl_test::MonadoQueue queue;
    VkDevice other = VK_NULL_HANDLE;
    REQUIRE(m.rig.create_device(0, {queue.info}, {"VK_KHR_swapchain"}, &other) == VK_SUCCESS);
    const auto sleep_other = m.rig.device_fn<PFN_vkLatencySleepNV>(other, "vkLatencySleepNV");
    g_driver.clear_calls();
    for (int i = 0; i < 3; ++i) {
        CHECK(sleep_other(other, swapchain, &info) == VK_SUCCESS);
    }
    CHECK(g_driver.count("vkSignalSemaphore") == 0);
    CHECK(g_driver.count("vkSignalSemaphoreKHR") == 0);
    CHECK(g_driver.count("vkLatencySleepNV") == 0);
    CHECK(log.count("vkLatencySleepNV:") == 1); // once

    m.rig.destroy_device(other);
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("a_stopped_display_never_hangs_monado")
{
    // The FakeApi's counter stops (the headset unplugged). Monado's frames, as it runs them: the first is Presented and
    // never credited, the next two queue behind it, each present-wait ends at its own timeout. The fourth acquire
    // (UINT64_MAX) finds no free back-buffer and still returns past the bound with the back-buffer of a queued frame,
    // never the presented one. The vblank thread's display-event wait ends at its own timeout with the event not set
    // (design 6.6, 6.7 step 5, 6.8, 6.9).
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    std::this_thread::sleep_for(30ms); // the display thread sees the counter run first
    m.rig.api.stop_counter();
    const Clock::time_point stopped = Clock::now();
    const size_t presents = m.rig.api.count("present");

    std::vector<uint32_t> indices;
    for (uint64_t id = 1; id <= 3; ++id) {
        uint32_t index = UINT32_MAX;
        REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
        REQUIRE(m.present_one(swapchain, index, id) == VK_SUCCESS);
        const Clock::time_point before = Clock::now();
        CHECK(m.wait_for_present(m.device, swapchain, id, 30 * kMs) == VK_TIMEOUT);
        const Clock::duration waited = Clock::now() - before;
        CHECK(waited >= 29ms);
        CHECK(waited < 300ms);
        indices.push_back(index);
    }
    CHECK(std::set<uint32_t>(indices.begin(), indices.end()).size() == 3);
    CHECK(m.rig.api.count("present") == presents + 1); // one outstanding Present, never credited

    uint32_t index = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    const Clock::duration acquired_after = Clock::now() - stopped;
    CHECK(acquired_after >= 550ms); // the bound (or flips seen stopped, which drops the queued frames)
    CHECK(acquired_after < 1500ms);
    CHECK((index == indices[1] || index == indices[2]));

    const VkFence event = m.display_event();
    const Clock::time_point before = Clock::now();
    CHECK(m.wait_fences(m.device, 1, &event, VK_TRUE, 100 * kMs) == VK_TIMEOUT);
    const Clock::duration waited = Clock::now() - before;
    CHECK(waited >= 99ms);
    CHECK(waited < 500ms);
    CHECK(m.fence_status(m.device, event) == VK_NOT_READY);
    m.destroy_fence(m.device, event, nullptr);

    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("a_late_frame_fence_holds_back_its_present_until_it_signals")
{
    // A frame whose GPU work ends late (the fake GPU holds its fence) in the middle of Monado's loop: the display
    // thread makes no NVAPI Present for it while its fence is unsignalled, Presents it once the fence signals, and the
    // present ids still complete in order.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    std::vector<uint64_t> acquired_tags;
    for (uint64_t id = 1; id <= 5; ++id) {
        acquired_tags.push_back(0x100 + m.frame(swapchain, id));
    }

    g_driver.hold_fences = true;
    uint32_t index = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    REQUIRE(m.present_one(swapchain, index, 6) == VK_SUCCESS);
    const size_t presents = m.rig.api.count("present");
    std::this_thread::sleep_for(60ms); // five refreshes
    CHECK(m.rig.api.count("present") == presents);
    CHECK(m.wait_for_present(m.device, swapchain, 5, 0) == VK_SUCCESS);
    CHECK(m.wait_for_present(m.device, swapchain, 6, 0) == VK_TIMEOUT);

    g_driver.hold_fences = false;
    g_driver.signal_held();
    CHECK(m.wait_for_present(m.device, swapchain, 6, 500 * kMs) == VK_SUCCESS);
    acquired_tags.push_back(0x100 + index);
    for (uint64_t id = 7; id <= 10; ++id) {
        acquired_tags.push_back(0x100 + m.frame(swapchain, id));
    }
    CHECK(present_tags(m.rig.api) == acquired_tags); // each frame its own Present, in order

    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("destroy_device_while_a_display_event_wait_is_in_progress")
{
    // Monado joins its vblank thread before teardown; if a wait on a display event were still in progress when its
    // device is destroyed, the wait ends at its own deadline (the event is forgotten, never set) and touches nothing of
    // the destroyed device.
    Monado m;
    const VkDevice device = m.device;
    const VkFence event = m.display_event(); // no swapchain: no display thread, so nothing sets it
    VkResult result = VK_RESULT_MAX_ENUM;
    Clock::duration waited{};
    std::thread vblank([&] {
        const Clock::time_point start = Clock::now();
        result = m.wait_fences(device, 1, &event, VK_TRUE, 300 * kMs);
        waited = Clock::now() - start;
    });
    std::this_thread::sleep_for(50ms);
    g_driver.clear_calls();
    m.destroy_device();
    vblank.join();

    CHECK(result == VK_TIMEOUT);
    CHECK(waited >= 299ms);
    CHECK(waited < 800ms);
    const std::vector<std::string> calls = g_driver.names();
    REQUIRE_FALSE(calls.empty());
    CHECK(calls.back() == "vkDestroyDevice"); // nothing reached the driver after it
    CHECK(g_driver.count_on("vkWaitForFences", handle_value(event)) == 0);
    CHECK(g_driver.count_on("vkGetFenceStatus", handle_value(event)) == 0);
    CHECK(g_driver.bad_fence_uses == 0);
}

TEST_CASE("the_trace_is_appended_each_time_the_display_thread_stops")
{
    // MIRADOR_DIRECT_MODE_TRACE=<folder>: nothing is written while the thread runs; the last swapchain's destruction
    // writes what it recorded, a later stop adds only what came after (one header, the old lines as they were).
    TraceDir dir;
    const dvl_test::ScopedEnv trace_on(L"MIRADOR_DIRECT_MODE_TRACE", dir.folder().c_str());
    Monado m;
    const VkSwapchainKHR a = m.make_swapchain();
    for (uint64_t id = 1; id <= 10; ++id) {
        m.frame(a, id);
    }
    CHECK_FALSE(std::filesystem::exists(dir.file()));
    m.destroy_swapchain(m.device, a, nullptr);

    const std::vector<std::string> first = dir.lines();
    REQUIRE(first.size() > 1);
    CHECK(first[0] == "t_ns,kind,a,b");
    CHECK(records_of_kind(first, nvdm::TraceKind::Acquire) == 10);
    CHECK(records_of_kind(first, nvdm::TraceKind::PresentEnter) == 10);
    CHECK(records_of_kind(first, nvdm::TraceKind::Credit) == 10);

    const VkSwapchainKHR b = m.make_swapchain();
    for (uint64_t id = 1; id <= 5; ++id) {
        m.frame(b, id);
    }
    m.destroy_swapchain(m.device, b, nullptr);
    const std::vector<std::string> second = dir.lines();
    REQUIRE(second.size() > first.size());
    CHECK(std::equal(first.begin(), first.end(), second.begin()));
    CHECK(records_of_kind(second, nvdm::TraceKind::Acquire) == 15);
    CHECK(std::count(second.begin(), second.end(), std::string("t_ns,kind,a,b")) == 1);
    m.destroy_device();
}

TEST_CASE("ctrl_c_writes_the_trace_before_the_default_handler_ends_the_process")
{
    // The console control handler runs the stop hook on a helper thread and gives it 100 ms; the process ends right
    // after. The hook stops the display thread and writes the trace in that time, the swapchain still alive.
    TraceDir dir;
    const dvl_test::ScopedEnv trace_on(L"MIRADOR_DIRECT_MODE_TRACE", dir.folder().c_str());
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    for (uint64_t id = 1; id <= 10; ++id) {
        m.frame(swapchain, id);
    }
    CHECK_FALSE(std::filesystem::exists(dir.file()));

    REQUIRE(m.rig.console(CTRL_C_EVENT) == 0); // FALSE: the default handler would end the process
    // The handler may return before the hook is done (a loaded machine): the helper thread goes on.
    for (int i = 0; i < 300 && records_of_kind(dir.lines(), nvdm::TraceKind::PresentReturn) < 10; ++i) {
        std::this_thread::sleep_for(10ms);
    }
    const std::vector<std::string> lines = dir.lines();
    REQUIRE(lines.size() > 1);
    CHECK(lines[0] == "t_ns,kind,a,b");
    CHECK(records_of_kind(lines, nvdm::TraceKind::Acquire) == 10);
    CHECK(records_of_kind(lines, nvdm::TraceKind::PresentReturn) == 10);
    CHECK(stats_reads_during(m.rig.api, 80) == 0); // and the thread is stopped

    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("ctrl_c_with_a_full_trace_writes_only_the_newest_65536_records_and_says_how_many_it_left_out")
{
    // The hook has 100 ms and a full array (2^20 records) takes ~160 ms to write, so the hook writes the newest 2^16
    // only (kConsoleHookTraceRecords in layer/swapchain.cpp), and the log says how many older ones stay out. That the
    // ones written are the newest is pinned on Trace itself (test_trace.cpp, "a bound writes the newest records").
    constexpr size_t kHeld = size_t{1} << 20;
    constexpr size_t kHookRecords = size_t{1} << 16;
    TraceDir dir;
    const dvl_test::ScopedEnv trace_on(L"MIRADOR_DIRECT_MODE_TRACE", dir.folder().c_str());
    Monado m;
    LogCapture log(m.rig);
    const VkSwapchainKHR swapchain = m.make_swapchain();
    REQUIRE(m.rig.fill_trace() == 1); // the array holds kHeld records; later ones are dropped
    CHECK_FALSE(std::filesystem::exists(dir.file()));

    REQUIRE(m.rig.console(CTRL_C_EVENT) == 0); // FALSE: the default handler would end the process
    // The handler may return before the hook is done (it waits 100 ms for it): the helper thread goes on.
    for (int i = 0; i < 300 && log.count("records written to") == 0; ++i) {
        std::this_thread::sleep_for(10ms);
    }
    REQUIRE(log.count("records written to") == 1);
    const std::vector<std::string> lines = dir.lines();
    REQUIRE_FALSE(lines.empty());
    CHECK(lines[0] == "t_ns,kind,a,b");
    CHECK(lines.size() == kHookRecords + 1); // the header and exactly 65536 records
    CHECK(log.count("trace: " + std::to_string(kHookRecords) + " records written to") == 1);
    CHECK(log.count("trace: " + std::to_string(kHeld - kHookRecords) + " older records not written yet are left out") ==
          1);

    m.destroy_swapchain(m.device, swapchain, nullptr);
    CHECK(dir.lines().size() == kHookRecords + 1); // the left-out records stay out: the stop has nothing more to write
    m.destroy_device();
}

TEST_CASE("a_full_trace_says_so_once_and_the_file_gets_what_it_holds")
{
    // The array of records fills up: one Info line names the file and says later records are dropped, however many are
    // dropped after it; the stop writes what the array holds.
    TraceDir dir;
    const dvl_test::ScopedEnv trace_on(L"MIRADOR_DIRECT_MODE_TRACE", dir.folder().c_str());
    Monado m;
    LogCapture log(m.rig);
    const VkSwapchainKHR swapchain = m.make_swapchain();
    REQUIRE(m.rig.fill_trace() == 1);
    for (uint64_t id = 1; id <= 3; ++id) {
        m.frame(swapchain, id); // the intercepts write the pending lines; their records are dropped
    }
    CHECK(log.count("records is full; later records are dropped") == 1);
    CHECK(log.count(dir.file().filename().string() + " gets the ones held") == 1);
    for (const LogCapture::Line& line : log.lines()) {
        if (line.text.find("records is full") != std::string::npos) {
            CHECK(line.level == dvl::Level::Info);
        }
    }

    m.destroy_swapchain(m.device, swapchain, nullptr);
    CHECK(log.count("records written to") == 1);
    CHECK(std::filesystem::exists(dir.file()));
    m.destroy_device();
}

// --- Present timing (design 6.11) ---------------------------------------------------------------------------------

namespace {

constexpr VkPresentStageFlagsEXT kTimedStages = VK_PRESENT_STAGE_QUEUE_OPERATIONS_END_BIT_EXT |
                                                VK_PRESENT_STAGE_REQUEST_DEQUEUED_BIT_EXT |
                                                VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT;

// The swapchain Monado makes with VK_EXT_present_timing, present id 2 and present wait 2.
VkSwapchainKHR make_timed_swapchain(Monado& m)
{
    VkSwapchainCreateInfoKHR ci = m.swapchain_info();
    ci.flags = VK_SWAPCHAIN_CREATE_PRESENT_TIMING_BIT_EXT | VK_SWAPCHAIN_CREATE_PRESENT_ID_2_BIT_KHR |
               VK_SWAPCHAIN_CREATE_PRESENT_WAIT_2_BIT_KHR;
    VkSwapchainKHR swapchain = VK_NULL_HANDLE;
    REQUIRE(m.create_swapchain(m.device, &ci, nullptr, &swapchain) == VK_SUCCESS);
    return swapchain;
}

// A present with a present-id2 id and one timing info: the swapchain's QPC domain, `stages`, and a target time (0:
// none) with its flags, read against the first pixel out as Monado asks.
VkResult present_timed(Monado& m, VkSwapchainKHR swapchain, uint32_t index, uint64_t id,
                       VkPresentStageFlagsEXT stages = kTimedStages, uint64_t target = 0,
                       VkPresentTimingInfoFlagsEXT flags = 0)
{
    VkPresentId2KHR ids{VK_STRUCTURE_TYPE_PRESENT_ID_2_KHR, nullptr, 1, &id};
    VkPresentTimingInfoEXT timing{VK_STRUCTURE_TYPE_PRESENT_TIMING_INFO_EXT};
    timing.flags = flags;
    timing.targetTime = target;
    timing.timeDomainId = 1; // the QPC domain (vkGetSwapchainTimeDomainPropertiesEXT)
    timing.presentStageQueries = stages;
    timing.targetTimeDomainPresentStage = VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT;
    VkPresentTimingsInfoEXT timings{VK_STRUCTURE_TYPE_PRESENT_TIMINGS_INFO_EXT, &ids, 1, &timing};
    VkPresentInfoKHR info{VK_STRUCTURE_TYPE_PRESENT_INFO_KHR, &timings};
    info.waitSemaphoreCount = 1;
    info.pWaitSemaphores = &kRenderSemaphore;
    info.swapchainCount = 1;
    info.pSwapchains = &swapchain;
    info.pImageIndices = &index;
    VkResult result = VK_RESULT_MAX_ENUM;
    info.pResults = &result;
    const VkResult r = m.present(m.queue, &info);
    CHECK(result == r);
    return r;
}

VkResult wait_for_present_2(Monado& m, VkSwapchainKHR swapchain, uint64_t id)
{
    VkPresentWait2InfoKHR info{VK_STRUCTURE_TYPE_PRESENT_WAIT_2_INFO_KHR, nullptr, id, 500 * kMs};
    return m.fn<PFN_vkWaitForPresent2KHR>("vkWaitForPresent2KHR")(m.device, swapchain, &info);
}

// vkGetPastPresentationTimingEXT with room for `n` results of four stages each.
struct PastTimings {
    explicit PastTimings(size_t n)
        : results(n, VkPastPresentationTimingEXT{VK_STRUCTURE_TYPE_PAST_PRESENTATION_TIMING_EXT}), stages(n)
    {
    }
    VkResult read(Monado& m, VkSwapchainKHR swapchain)
    {
        for (size_t i = 0; i < results.size(); ++i) {
            results[i].presentStageCount = 4;
            results[i].pPresentStages = stages[i].data();
        }
        VkPastPresentationTimingInfoEXT info{VK_STRUCTURE_TYPE_PAST_PRESENTATION_TIMING_INFO_EXT};
        info.swapchain = swapchain;
        props.presentationTimingCount = static_cast<uint32_t>(results.size());
        props.pPresentationTimings = results.data();
        return m.fn<PFN_vkGetPastPresentationTimingEXT>("vkGetPastPresentationTimingEXT")(m.device, &info, &props);
    }

    std::vector<VkPastPresentationTimingEXT> results;
    std::vector<std::array<VkPresentStageTimeEXT, 4>> stages;
    VkPastPresentationTimingPropertiesEXT props{VK_STRUCTURE_TYPE_PAST_PRESENTATION_TIMING_PROPERTIES_EXT};
};

} // namespace

TEST_CASE("present_timing_gives_each_frame_its_flip")
{
    // Monado's frame loop with present timing, as the feedback pacer runs it: present with an id2 and the three stages,
    // wait for it with present-wait 2, read its results. Each frame's first pixel out is the FakeApi flip that put it on
    // screen, after its queue operations ended and its Present started, and none of it reaches the driver.
    Monado m;
    const VkSwapchainKHR swapchain = make_timed_swapchain(m);
    REQUIRE(m.fn<PFN_vkSetSwapchainPresentTimingQueueSizeEXT>("vkSetSwapchainPresentTimingQueueSizeEXT")(
                m.device, swapchain, 4) == VK_SUCCESS);

    VkSwapchainTimingPropertiesEXT properties{VK_STRUCTURE_TYPE_SWAPCHAIN_TIMING_PROPERTIES_EXT};
    uint64_t counter = 0;
    REQUIRE(m.fn<PFN_vkGetSwapchainTimingPropertiesEXT>("vkGetSwapchainTimingPropertiesEXT")(
                m.device, swapchain, &properties, &counter) == VK_SUCCESS);
    CHECK(properties.refreshDuration == static_cast<uint64_t>(kRefresh));
    CHECK(properties.refreshInterval == static_cast<uint64_t>(kRefresh)); // a fixed refresh rate
    CHECK(counter == 1);
    VkSwapchainTimeDomainPropertiesEXT domains{VK_STRUCTURE_TYPE_SWAPCHAIN_TIME_DOMAIN_PROPERTIES_EXT};
    REQUIRE(m.fn<PFN_vkGetSwapchainTimeDomainPropertiesEXT>("vkGetSwapchainTimeDomainPropertiesEXT")(
                m.device, swapchain, &domains, &counter) == VK_SUCCESS);
    CHECK(domains.timeDomainCount == 2);

    constexpr uint64_t kFrames = 40;
    std::vector<uint64_t> flips;
    for (uint64_t id = 1; id <= kFrames; ++id) {
        uint32_t index = UINT32_MAX;
        REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
        const int64_t presented = m.rig.clock.now_ns();
        REQUIRE(present_timed(m, swapchain, index, id) == VK_SUCCESS);
        REQUIRE(wait_for_present_2(m, swapchain, id) == VK_SUCCESS);
        const int64_t waited = m.rig.clock.now_ns();

        // Its id completed when it went on screen, so its results are complete.
        PastTimings past(2);
        REQUIRE(past.read(m, swapchain) == VK_SUCCESS);
        REQUIRE(past.props.presentationTimingCount == 1);
        const VkPastPresentationTimingEXT& r = past.results[0];
        INFO("frame " << id);
        CHECK(r.presentId == id);
        CHECK(r.reportComplete == VK_TRUE);
        CHECK(r.timeDomain == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
        CHECK(r.timeDomainId == 1);
        REQUIRE(r.presentStageCount == 3);
        CHECK(r.pPresentStages[0].stage == VK_PRESENT_STAGE_QUEUE_OPERATIONS_END_BIT_EXT);
        CHECK(r.pPresentStages[1].stage == VK_PRESENT_STAGE_REQUEST_DEQUEUED_BIT_EXT);
        CHECK(r.pPresentStages[2].stage == VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT);
        const uint64_t queue_end = r.pPresentStages[0].time;
        const uint64_t dequeued = r.pPresentStages[1].time;
        const uint64_t pixel_out = r.pPresentStages[2].time;
        CHECK(queue_end >= static_cast<uint64_t>(presented));
        CHECK(queue_end <= dequeued);
        CHECK(dequeued < pixel_out);                      // a refresh after its Present returned
        CHECK(pixel_out <= static_cast<uint64_t>(waited)); // the flip came before the wait returned
        flips.push_back(pixel_out);
    }

    // The first pixel out of every frame is a flip of the FakeApi's, which come every refresh period from its start.
    for (size_t i = 1; i < flips.size(); ++i) {
        INFO("frames " << i << " and " << i + 1);
        CHECK(flips[i] > flips[i - 1]);
        CHECK((flips[i] - flips[i - 1]) % static_cast<uint64_t>(kRefresh) == 0);
    }
    nvdm::RefreshStats now{};
    REQUIRE(m.rig.api.present_stats(now) == 0);
    CHECK(static_cast<uint64_t>(now.qpc_ns) >= flips.back());
    CHECK((static_cast<uint64_t>(now.qpc_ns) - flips.back()) % static_cast<uint64_t>(kRefresh) == 0);

    // None of it reached the driver.
    for (const char* name : {"vkSetSwapchainPresentTimingQueueSizeEXT", "vkGetSwapchainTimingPropertiesEXT",
                             "vkGetSwapchainTimeDomainPropertiesEXT", "vkGetPastPresentationTimingEXT",
                             "vkWaitForPresent2KHR", "vkQueuePresentKHR", "vkCreateSwapchainKHR"}) {
        INFO(name);
        CHECK(g_driver.count(name) == 0);
    }
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

namespace {

constexpr VkPresentTimingInfoFlagsEXT kNearest = VK_PRESENT_TIMING_INFO_PRESENT_AT_NEAREST_REFRESH_CYCLE_BIT_EXT;

// The FakeApi's flips exactly: every kRefresh from its start, which is a little after clock 0 (flip_at_or_before()
// is near enough for the tests above, not for naming a flip to the nanosecond).
struct Flips {
    explicit Flips(Monado& m)
    {
        nvdm::RefreshStats now{};
        REQUIRE(m.rig.api.present_stats(now) == 0);
        phase = now.qpc_ns % kRefresh;
    }
    int64_t at_or_before(int64_t t) const
    {
        const int64_t since = (t - phase) % kRefresh;
        return t - (since < 0 ? since + kRefresh : since);
    }
    int64_t at_or_after(int64_t t) const
    {
        const int64_t before = at_or_before(t);
        return before == t ? t : before + kRefresh;
    }
    // Waits until 1-4 ms after a flip, so that a present made now makes the next flip with time to spare and a test
    // can name the flip it goes out at.
    void early_in_a_period(Monado& m) const
    {
        for (;;) {
            const int64_t now = m.rig.clock.now_ns();
            const int64_t since = now - at_or_before(now);
            if (since >= 1'000'000 && since <= 4'000'000) {
                return;
            }
            std::this_thread::yield();
        }
    }

    int64_t phase = 0;
};

// One frame of Monado's present-at-time pacer: acquire, present with `target` (0: none), wait for it; its first pixel
// out (0 if it was never on screen). `presented`: when it was presented.
uint64_t present_at(Monado& m, VkSwapchainKHR swapchain, uint64_t id, uint64_t target, VkPresentTimingInfoFlagsEXT flags,
                    int64_t* presented = nullptr)
{
    uint32_t index = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &index) == VK_SUCCESS);
    if (presented != nullptr) {
        *presented = m.rig.clock.now_ns();
    }
    REQUIRE(present_timed(m, swapchain, index, id, kTimedStages, target, flags) == VK_SUCCESS);
    REQUIRE(wait_for_present_2(m, swapchain, id) == VK_SUCCESS);
    PastTimings past(2);
    REQUIRE(past.read(m, swapchain) == VK_SUCCESS);
    REQUIRE(past.props.presentationTimingCount == 1);
    const VkPastPresentationTimingEXT& r = past.results[0];
    CHECK(r.presentId == id);
    CHECK(r.targetTime == target); // echoed
    CHECK(r.reportComplete == VK_TRUE);
    REQUIRE(r.presentStageCount == 3);
    return r.pPresentStages[2].time;
}

} // namespace

TEST_CASE("present_at_a_target_time_shows_each_frame_at_the_first_flip_at_or_after_it")
{
    // Design 6.11, present at a time, as Monado's pacer asks for it: an absolute QPC target, read against the first
    // pixel out. An early frame is held for the flip at or after its target (within 1 ms; with NEAREST, the flip
    // nearest it), never one flip later, whatever the target's phase; a frame whose target has passed, or that has
    // none, goes at the first flip it can make. The first pixel out stays the real flip.
    dvl_test::ScopedEnv present_at_on(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", nullptr);
    Monado m;
    LogCapture log(m.rig);
    const VkSwapchainKHR swapchain = make_timed_swapchain(m);
    REQUIRE(m.fn<PFN_vkSetSwapchainPresentTimingQueueSizeEXT>("vkSetSwapchainPresentTimingQueueSizeEXT")(
                m.device, swapchain, 4) == VK_SUCCESS);
    CHECK(log.count("present at a time on") == 1);
    const Flips flips(m);

    struct Case {
        const char* what;
        int64_t ahead; // the target: this long after the present (0: no target)...
        bool on_flip;  // ...or after the flip before the present
        VkPresentTimingInfoFlagsEXT flags;
    };
    const Case cases[] = {
        {"untargeted", 0, false, 0},
        {"three periods ahead", 3 * kRefresh, false, 0},
        {"exactly on a flip three ahead", 3 * kRefresh, true, 0},
        {"0.3 ms before a flip", 3 * kRefresh - 300'000, true, 0},
        {"0.5 ms after a flip: within the 1 ms tolerance", 3 * kRefresh + 500'000, true, 0},
        {"2 ms after a flip: the next one", 3 * kRefresh + 2'000'000, true, 0},
        {"NEAREST, 0.4 period past a flip: that flip", 3 * kRefresh + 4 * kRefresh / 10, true, kNearest},
        {"NEAREST, 0.6 period past a flip: the next", 3 * kRefresh + 6 * kRefresh / 10, true, kNearest},
        {"late: two periods ago", -2 * kRefresh, false, 0},
        {"untargeted after the targets", 0, false, 0},
        {"NEAREST, five periods ahead", 5 * kRefresh, false, kNearest},
        {"the next flip", kRefresh, true, 0},
    };
    uint64_t id = 0;
    for (const Case& c : cases) {
        INFO(c.what);
        ++id;
        // The target is set just before the present, on the clock the flips run on.
        flips.early_in_a_period(m);
        const int64_t now = m.rig.clock.now_ns();
        const int64_t base = c.on_flip ? flips.at_or_before(now) : now;
        const uint64_t target = c.ahead == 0 ? 0 : static_cast<uint64_t>(base + c.ahead);
        int64_t presented = 0;
        const uint64_t pixel_out = present_at(m, swapchain, id, target, c.flags, &presented);
        INFO("target " << target << ", presented " << presented << ", first pixel out " << pixel_out);
        // The first flip it can make, and with a target not before it: never a flip early, never a flip late.
        int64_t expected = flips.at_or_before(presented) + kRefresh;
        if (c.ahead > 0) {
            const int64_t early = (c.flags & kNearest) != 0 ? kRefresh / 2 : 1'000'000;
            expected = std::max(expected, flips.at_or_after(static_cast<int64_t>(target) - early));
        }
        CHECK(static_cast<int64_t>(pixel_out) == expected);
    }
    CHECK(log.count("each frame is held for the first refresh at or after its target") == 1);
    CHECK(log.count("present at a time is off") == 0);
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("a_frame_behind_a_held_one_waits_for_it")
{
    // Presents are taken in order: an untargeted frame queued behind a held one is not shown before the held one's
    // flip. At that flip the newest queued frame goes, as always (design 6.7), and the held one is retired unshown.
    dvl_test::ScopedEnv present_at_on(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", nullptr);
    Monado m;
    const VkSwapchainKHR swapchain = make_timed_swapchain(m);
    REQUIRE(m.fn<PFN_vkSetSwapchainPresentTimingQueueSizeEXT>("vkSetSwapchainPresentTimingQueueSizeEXT")(
                m.device, swapchain, 4) == VK_SUCCESS);
    present_at(m, swapchain, 1, 0, 0); // one on screen
    const Flips flips(m);

    uint32_t first = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &first) == VK_SUCCESS);
    flips.early_in_a_period(m);
    const int64_t target = flips.at_or_before(m.rig.clock.now_ns()) + 4 * kRefresh;
    REQUIRE(present_timed(m, swapchain, first, 2, kTimedStages, static_cast<uint64_t>(target)) == VK_SUCCESS);
    uint32_t second = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &second) == VK_SUCCESS);
    REQUIRE(present_timed(m, swapchain, second, 3) == VK_SUCCESS);
    REQUIRE(wait_for_present_2(m, swapchain, 3) == VK_SUCCESS);

    PastTimings past(4);
    REQUIRE(past.read(m, swapchain) == VK_SUCCESS);
    REQUIRE(past.props.presentationTimingCount == 2);
    CHECK(past.results[0].presentId == 2);
    CHECK(past.results[0].reportComplete == VK_TRUE);
    CHECK(past.results[0].pPresentStages[2].time == 0); // retired at the flip it was held for
    CHECK(past.results[1].presentId == 3);
    CHECK(static_cast<int64_t>(past.results[1].pPresentStages[2].time) == target); // not before it
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("present_at_a_time_switched_off_shows_targeted_frames_at_the_first_flip")
{
    // MIRADOR_DIRECT_MODE_NO_PRESENT_AT=1, read when the swapchain is made, for an A/B of the same build: a target is
    // ignored (said once) and the timings go on as before. (The surface's caps: test_surface.cpp.)
    dvl_test::ScopedEnv present_at_off(L"MIRADOR_DIRECT_MODE_NO_PRESENT_AT", L"1");
    Monado m;
    LogCapture log(m.rig);
    const VkSwapchainKHR swapchain = make_timed_swapchain(m);
    CHECK(log.count("present at a time off (MIRADOR_DIRECT_MODE_NO_PRESENT_AT)") == 1);
    REQUIRE(m.fn<PFN_vkSetSwapchainPresentTimingQueueSizeEXT>("vkSetSwapchainPresentTimingQueueSizeEXT")(
                m.device, swapchain, 4) == VK_SUCCESS);
    const Flips flips(m);
    for (uint64_t id = 1; id <= 4; ++id) {
        INFO("frame " << id);
        flips.early_in_a_period(m);
        const uint64_t target = static_cast<uint64_t>(m.rig.clock.now_ns() + 5 * kRefresh);
        int64_t presented = 0;
        const uint64_t pixel_out = present_at(m, swapchain, id, target, kNearest, &presented);
        CHECK(static_cast<int64_t>(pixel_out) == flips.at_or_before(presented) + kRefresh); // not held
    }
    CHECK(log.count("present at a time is off") == 1); // said once
    CHECK(log.count("each frame is held") == 0);
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("a_full_present_timing_queue_fails_the_present_before_anything_is_submitted")
{
    Monado m;
    const VkSwapchainKHR swapchain = make_timed_swapchain(m);
    const auto set_size =
        m.fn<PFN_vkSetSwapchainPresentTimingQueueSizeEXT>("vkSetSwapchainPresentTimingQueueSizeEXT");
    uint32_t first = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &first) == VK_SUCCESS);

    // No size set yet: no slot. Nothing is submitted, and the image stays acquired for a present that works.
    const size_t submits = g_driver.submits_on(m.queue).size();
    CHECK(present_timed(m, swapchain, first, 1) == VK_ERROR_PRESENT_TIMING_QUEUE_FULL_EXT);
    CHECK(g_driver.submits_on(m.queue).size() == submits);
    REQUIRE(set_size(m.device, swapchain, 1) == VK_SUCCESS);
    REQUIRE(present_timed(m, swapchain, first, 1) == VK_SUCCESS);
    REQUIRE(wait_for_present_2(m, swapchain, 1) == VK_SUCCESS);

    // Its results unread hold the one slot: the next timed present fails, the size cannot go below it, and a present
    // that asks for no stages still goes.
    uint32_t second = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &second) == VK_SUCCESS);
    CHECK(present_timed(m, swapchain, second, 2) == VK_ERROR_PRESENT_TIMING_QUEUE_FULL_EXT);
    CHECK(set_size(m.device, swapchain, 0) == VK_NOT_READY);
    REQUIRE(present_timed(m, swapchain, second, 2, 0) == VK_SUCCESS);
    REQUIRE(wait_for_present_2(m, swapchain, 2) == VK_SUCCESS);

    // Read: frame 1's results (frame 2 asked for none), and its slot is free again.
    PastTimings past(4);
    REQUIRE(past.read(m, swapchain) == VK_SUCCESS);
    REQUIRE(past.props.presentationTimingCount == 1);
    CHECK(past.results[0].presentId == 1);
    uint32_t third = UINT32_MAX;
    REQUIRE(m.acquire(m.device, swapchain, kForever, kAcquireSemaphore, VK_NULL_HANDLE, &third) == VK_SUCCESS);
    CHECK(present_timed(m, swapchain, third, 3) == VK_SUCCESS);
    REQUIRE(wait_for_present_2(m, swapchain, 3) == VK_SUCCESS);

    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("calibrated_timestamps_answer_our_swapchains_local_domains_from_the_qpc_reading")
{
    // A VkSwapchainCalibratedTimestampInfoEXT may name the layer's swapchain: the driver gets a QPC entry in place of
    // its local domain and never the handle; the layer gives that reading in nanoseconds, the timeline of its results.
    Monado m;
    const VkSwapchainKHR swapchain = m.make_swapchain();
    const auto get = m.fn<PFN_vkGetCalibratedTimestampsKHR>("vkGetCalibratedTimestampsKHR");
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    const uint64_t qpc_ticks = dvl_test::fake::fake_timestamp(VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
    const uint64_t qpc_ns =
        static_cast<uint64_t>(nvdm::qpc_ticks_to_ns(static_cast<int64_t>(qpc_ticks), frequency.QuadPart));
    VkSwapchainCalibratedTimestampInfoEXT ours{VK_STRUCTURE_TYPE_SWAPCHAIN_CALIBRATED_TIMESTAMP_INFO_EXT};
    ours.swapchain = swapchain;
    ours.presentStage = VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT;
    ours.timeDomainId = 2;
    const auto info = [](VkTimeDomainKHR domain, const void* next = nullptr) {
        VkCalibratedTimestampInfoKHR i{VK_STRUCTURE_TYPE_CALIBRATED_TIMESTAMP_INFO_KHR};
        i.pNext = next;
        i.timeDomain = domain;
        return i;
    };
    uint64_t ts[2] = {};
    uint64_t deviation = 0;

    // The device clock and a present stage of ours: the driver reads the device clock and QPC.
    VkCalibratedTimestampInfoKHR device_and_stage[2] = {info(VK_TIME_DOMAIN_DEVICE_KHR),
                                                        info(VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT, &ours)};
    REQUIRE(get(m.device, 2, device_and_stage, ts, &deviation) == VK_SUCCESS);
    REQUIRE(g_driver.calibrations.size() == 1);
    REQUIRE(g_driver.calibrations[0].size() == 2);
    CHECK(g_driver.calibrations[0][0].domain == VK_TIME_DOMAIN_DEVICE_KHR);
    CHECK(g_driver.calibrations[0][1].domain == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
    CHECK_FALSE(g_driver.calibrations[0][1].chained);
    CHECK(ts[0] == dvl_test::fake::fake_timestamp(VK_TIME_DOMAIN_DEVICE_KHR));
    CHECK(ts[1] == qpc_ns);
    CHECK(deviation == dvl_test::fake::kFakeMaxDeviation);

    // QPC asked for too, and our swapchain-local domain: one QPC reading serves both, in ticks and in nanoseconds.
    VkCalibratedTimestampInfoKHR qpc_and_local[2] = {info(VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR),
                                                     info(VK_TIME_DOMAIN_SWAPCHAIN_LOCAL_EXT, &ours)};
    REQUIRE(get(m.device, 2, qpc_and_local, ts, &deviation) == VK_SUCCESS);
    REQUIRE(g_driver.calibrations.size() == 2);
    REQUIRE(g_driver.calibrations[1].size() == 1);
    CHECK(g_driver.calibrations[1][0].domain == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
    CHECK(ts[0] == qpc_ticks);
    CHECK(ts[1] == qpc_ns);

    // Our swapchain named with a domain that is not local: down without the struct.
    VkCalibratedTimestampInfoKHR qpc_naming_ours = info(VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR, &ours);
    REQUIRE(get(m.device, 1, &qpc_naming_ours, ts, &deviation) == VK_SUCCESS);
    REQUIRE(g_driver.calibrations.size() == 3);
    REQUIRE(g_driver.calibrations[2].size() == 1);
    CHECK_FALSE(g_driver.calibrations[2][0].chained);
    CHECK(ts[0] == qpc_ticks);

    // The driver's own swapchain: unchanged.
    VkSwapchainCalibratedTimestampInfoEXT theirs = ours;
    theirs.swapchain = dvl_test::fake::driver_swapchain(0);
    VkCalibratedTimestampInfoKHR their_stage = info(VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT, &theirs);
    REQUIRE(get(m.device, 1, &their_stage, ts, &deviation) == VK_SUCCESS);
    REQUIRE(g_driver.calibrations.size() == 4);
    CHECK(g_driver.calibrations[3][0].domain == VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT);
    CHECK(g_driver.calibrations[3][0].swapchain == handle_value(theirs.swapchain));
    CHECK(ts[0] == dvl_test::fake::fake_timestamp(VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT));

    // The EXT name does the same.
    REQUIRE(m.fn<PFN_vkGetCalibratedTimestampsEXT>("vkGetCalibratedTimestampsEXT")(m.device, 2, device_and_stage, ts,
                                                                                   &deviation) == VK_SUCCESS);
    CHECK(g_driver.count("vkGetCalibratedTimestampsEXT") == 1);
    REQUIRE(g_driver.calibrations.size() == 5);
    CHECK(g_driver.calibrations[4][1].domain == VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR);
    CHECK(ts[1] == qpc_ns);

    for (const auto& call : g_driver.calibrations) {
        for (const dvl_test::FakeDriver::Calibration& c : call) {
            CHECK(c.swapchain != handle_value(swapchain));
        }
    }
    m.destroy_swapchain(m.device, swapchain, nullptr);
    m.destroy_device();
}

TEST_CASE("present_timing_commands_on_a_driver_swapchain_go_down")
{
    // Monado's peek window has a swapchain of the driver's: its present timing goes down unchanged.
    Monado m;
    const VkSwapchainKHR theirs = dvl_test::fake::driver_swapchain(0);
    const uint64_t handle = handle_value(theirs);
    CHECK(m.fn<PFN_vkSetSwapchainPresentTimingQueueSizeEXT>("vkSetSwapchainPresentTimingQueueSizeEXT")(
              m.device, theirs, 4) == VK_SUCCESS);
    VkSwapchainTimingPropertiesEXT properties{VK_STRUCTURE_TYPE_SWAPCHAIN_TIMING_PROPERTIES_EXT};
    uint64_t counter = 0;
    CHECK(m.fn<PFN_vkGetSwapchainTimingPropertiesEXT>("vkGetSwapchainTimingPropertiesEXT")(m.device, theirs,
                                                                                             &properties, &counter) ==
          VK_SUCCESS);
    CHECK(properties.refreshDuration == 16'666'667); // the driver's answer
    CHECK(counter == 7);
    VkSwapchainTimeDomainPropertiesEXT domains{VK_STRUCTURE_TYPE_SWAPCHAIN_TIME_DOMAIN_PROPERTIES_EXT};
    domains.timeDomainCount = 9;
    CHECK(m.fn<PFN_vkGetSwapchainTimeDomainPropertiesEXT>("vkGetSwapchainTimeDomainPropertiesEXT")(
              m.device, theirs, &domains, nullptr) == VK_SUCCESS);
    CHECK(domains.timeDomainCount == 0);
    PastTimings past(1);
    CHECK(past.read(m, theirs) == VK_SUCCESS);
    CHECK(past.props.presentationTimingCount == 0);
    CHECK(wait_for_present_2(m, theirs, 1) == VK_SUCCESS);
    uint32_t index = 0;
    VkReleaseSwapchainImagesInfoKHR release{VK_STRUCTURE_TYPE_RELEASE_SWAPCHAIN_IMAGES_INFO_KHR};
    release.swapchain = theirs;
    release.imageIndexCount = 1;
    release.pImageIndices = &index;
    CHECK(m.fn<PFN_vkReleaseSwapchainImagesKHR>("vkReleaseSwapchainImagesKHR")(m.device, &release) == VK_SUCCESS);

    for (const char* name : {"vkSetSwapchainPresentTimingQueueSizeEXT", "vkGetSwapchainTimingPropertiesEXT",
                             "vkGetSwapchainTimeDomainPropertiesEXT", "vkGetPastPresentationTimingEXT",
                             "vkWaitForPresent2KHR", "vkReleaseSwapchainImagesKHR"}) {
        INFO(name);
        CHECK(g_driver.count_on(name, handle) == 1);
    }
    m.destroy_device();
}
