// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

// The swapchain on the layer's surface and the presentation state behind it (design 6.4-6.10), shared by the
// intercepts of swapchain.cpp, present.cpp, events.cpp and misc_intercepts.cpp, and by vkDestroyDevice (device.cpp).
//
// Lock order (design 6.5 invariant 7):
//   Presenter::lifecycle -> DeviceData::queue_mutex -> the display-session lock -> leaf locks
// The leaf locks are the swapchain registry's, PendingLines', LayerFenceOps', the log's and the surface registry's:
// nothing else is taken while one is held. The display-session lock is never held while taking the private-queue
// mutex, during a GPU wait (vkWaitForFences, vkQueueWaitIdle) or an NVAPI call, or while starting or joining the
// display thread. Lines made under it are written once it is let go (PendingLines), so a blocked console never holds
// it.

#include <vulkan/vulkan.h>

#include <array>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

#include "core/display_loop.hpp"
#include "core/display_session.hpp"
#include "core/display_thread.hpp"
#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "core/stats.hpp"
#include "core/trace.hpp"
#include "layer/dispatch.hpp"
#include "layer/event_table.hpp"
#include "layer/import.hpp"
#include "layer/log.hpp"
#include "layer/present_timing.hpp"
#include "layer/warnings.hpp"

namespace dvl {

inline constexpr uint32_t kImageCount = nvdm::kBackBufferCount;

// A swapchain of the layer's on its surface (design 6.4). Its handle is the record's address. Image i is back-buffer
// i of the display session: the map image index -> back-buffer is the identity, and back-buffer state lives in the
// frame model, not here, so every swapchain wraps the same three. Completed present ids are kept per generation in the
// frame model.
struct SwapchainRecord {
    nvdm::SwapchainGen gen = 0;
    std::array<ImportedImage, kImageCount> images{};
    VkFormat format = VK_FORMAT_UNDEFINED;
    VkImageUsageFlags usage = 0;

    DeviceData* device = nullptr;
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkPresentModeKHR present_mode = VK_PRESENT_MODE_FIFO_KHR;
    bool present_at = true; // present at a time (design 6.11), read once at creation: MIRADOR_DIRECT_MODE_NO_PRESENT_AT

    // Under the display-session lock.
    bool alive = true;         // until vkDestroySwapchainKHR
    bool images_moved = false; // a newer swapchain made with this one as oldSwapchain owns the images now
    std::array<nvdm::FrameSeq, kImageCount> acquired{}; // the frame acquired on each image, 0 for none
};

// The registry of the layer's swapchains, by handle. Lookups never dereference the handle, so any handle (a driver's,
// VK_NULL_HANDLE) can be asked about. Any thread.
VkSwapchainKHR add_swapchain(std::shared_ptr<SwapchainRecord> record);
std::shared_ptr<SwapchainRecord> find_swapchain(VkSwapchainKHR swapchain); // null if not ours
std::shared_ptr<SwapchainRecord> remove_swapchain(VkSwapchainKHR swapchain);
std::vector<std::pair<VkSwapchainKHR, std::shared_ptr<SwapchainRecord>>> swapchains_of(const DeviceData& device);

// A fence handle as a number: the key of the warnings' 2 s rule.
inline uint64_t handle_key(VkFence fence)
{
    return static_cast<uint64_t>(reinterpret_cast<uintptr_t>(fence));
}

// Whether a handle given as an object type and a number (debug names, private data) is one of the layer's own
// display, mode, surface or swapchain.
bool is_our_object(VkObjectType type, uint64_t handle);
bool is_our_debug_report_object(VkDebugReportObjectTypeEXT type, uint64_t handle);

// Lines made under the display-session lock (the frame model's, the warnings), written once it is let go: by the
// intercept that held it (Locked), or, after a wait whose return is a measurement (Locked::Flush::Later), by the next
// intercept that writes them or by the display thread before one of its waits (LayerWaiter).
class PendingLines {
public:
    void add(Level level, std::string line);
    // Writes them. Never call it holding the display-session lock.
    void flush();

private:
    std::mutex mutex_;
    std::vector<std::pair<Level, std::string>> lines_;
};

// The display loop's wait (design 6.7 step 1) as the layer runs it: Win32Waiter, writing pending lines before a wait
// long enough not to matter (never inside step 3's polling window).
class LayerWaiter : public nvdm::Waiter {
public:
    LayerWaiter(nvdm::Clock& clock, PendingLines& pending);
    bool wait_frame_or_until(int64_t deadline_ns) override;
    bool wait_frame_or_until_coarse(int64_t deadline_ns) override;
    void signal_frame_queued() override;
    bool high_resolution() const { return inner_.high_resolution(); }

private:
    nvdm::Clock& clock_;
    nvdm::Win32Waiter inner_;
    PendingLines& pending_;
};

// The display loop's frame fences (vkWaitForFences and vkGetFenceStatus on a slot's VkFence), on the one device of an
// epoch. With vkDestroyDevice's handshake (design 6.10, the Task 6 review): leave() makes every later call fail at
// once without touching the device, and waits for a call in progress, which ends by its deadline (~1 ms before the
// predicted refresh, so within a refresh period).
class LayerFenceOps : public nvdm::FenceOps {
public:
    LayerFenceOps(DeviceData& device, nvdm::Clock& clock);
    bool wait(nvdm::FenceSlot slot, int64_t deadline_ns) override;
    bool status(nvdm::FenceSlot slot) override;

    DeviceData* device();
    // No call on the device after this; true if none is in progress when it returns (waited up to timeout_ns).
    bool leave(int64_t timeout_ns);

private:
    // The device and the slot's fence for a call about to start, or null (left, or a slot with no fence).
    DeviceData* enter(nvdm::FenceSlot slot, VkFence& fence);
    void exit();

    std::mutex mutex_;
    std::condition_variable idle_;
    DeviceData* device_;
    bool in_call_ = false;
    nvdm::Clock& clock_;
};

// The presentation state of the process (design 6.5-6.9, 6.11), one per display session: the frame model (back-buffers
// and the frames of the presenting device), the stats, the display events, the present timings, the warnings and the
// display thread. Made at
// the first swapchain or display event and never destroyed (design 6.10); a test that puts another display session in
// place gets a new one (reset_presenter_for_tests).
class Presenter {
public:
    explicit Presenter(nvdm::DisplaySession& session);
    Presenter(const Presenter&) = delete;
    Presenter& operator=(const Presenter&) = delete;

    nvdm::DisplaySession& session;
    nvdm::Clock& clock;          // the session's: QPC nanoseconds, the timeline of NVAPI's refresh times
    std::mutex& lock;            // the display-session lock
    const int64_t refresh_ns;    // the mode's refresh period: the frame model's and the display loop's alike
    std::condition_variable changed; // notified under `lock` at every refresh and every change a wait waits for

    // Under `lock`.
    nvdm::FrameModel model;
    nvdm::StatsWindow stats;
    EventTable events;
    PresentTimings timings; // fed by the model's reports (design 6.11)
    Warnings warnings;
    DeviceData* device = nullptr; // the device whose fence pool the model has (one presents at a time, design 6.4)
    uint32_t swapchains = 0;      // the layer's live swapchains

    std::atomic<size_t> event_count{0}; // events.size(), for the fence intercepts' look without the lock
    PendingLines pending;

    // The fence fallback (present.cpp): how many presents took it, and when (on `clock`) its line was last written, at
    // most once a second; kNever before the first.
    static constexpr int64_t kNever = INT64_MIN;
    std::atomic<uint64_t> fence_fallbacks{0};
    std::atomic<int64_t> fence_fallback_line_ns{kNever};

    // Swapchain creation and destruction, and vkDestroyDevice, one at a time; never under `lock`.
    std::mutex lifecycle;
    nvdm::DisplayThread* thread;              // never destroyed (design 6.10)
    LayerWaiter waiter;                       // every epoch's; signal_frame_queued() from any thread
    std::shared_ptr<LayerFenceOps> fence_ops; // the running epoch's
    std::vector<std::shared_ptr<LayerFenceOps>> past_ops; // kept: a loop left behind still refers to its own

    // Waits on `changed` (holding `hold`, a lock of `lock`) until notified or until_ns on `clock`, whichever comes
    // first; returns at once if until_ns has passed. May return early, so callers loop on their condition.
    void wait_changed(std::unique_lock<std::mutex>& hold, int64_t until_ns);

    // MIRADOR_DIRECT_MODE_TRACE (a folder, or 1 for the log's folder): per-frame timing records, added to
    // mirador-trace-<pid>.csv each time the display thread stops. Null when the variable is off (unset, empty or 0).
    std::unique_ptr<nvdm::Trace> trace;
    std::wstring trace_path;
    // A record stamped now on `clock`, if tracing. Any thread, no lock needed.
    void note(nvdm::TraceKind kind, uint64_t a = 0, int64_t b = 0)
    {
        if (trace) {
            trace->add(kind, clock.now_ns(), a, b);
        }
    }
    // Adds the records not written yet to trace_path (the newest `newest_at_most` of them only) and logs how many; does
    // nothing without a trace. Any thread, never under `lock`: writes are one at a time, and one may take a while.
    void write_trace(size_t newest_at_most = SIZE_MAX);
};

// The presenter of the current display session, made if there is none. Call it only once the display session is open
// (the layer's surface, display or swapchain exists).
Presenter& presenter();
// The presenter if one was made, without making one: for paths that only look (the fence intercepts).
Presenter* presenter_if_any();
// Stops the display thread and forgets the presenter and every swapchain record, before a test puts another display
// session in place (test_hooks.cpp). Never called in the shipped layer.
void reset_presenter_for_tests();

// The display-session lock, held for an intercept. The warnings' lines are collected while it is held and, with the
// frame model's, written once it is let go (Flush::Now). The waits whose return is a measurement (Monado's vblank
// thread stamps the refresh when its display-event wait returns; its pacer reads the present-wait's return) take
// Flush::Later: they leave every pending line to the next intercept that writes them, or to the display thread before
// one of its waits, so no file, console or debugger write delays their return.
class Locked {
public:
    enum class Flush { Now, Later };
    explicit Locked(Presenter& p, Flush flush = Flush::Now);
    ~Locked();
    Locked(const Locked&) = delete;
    Locked& operator=(const Locked&) = delete;

    std::unique_lock<std::mutex> hold;

private:
    Presenter& p_;
    Flush flush_;
};

// A Vulkan timeout from now, as a deadline on `clock`: INT64_MAX for UINT64_MAX (or anything that does not fit).
int64_t deadline_after(nvdm::Clock& clock, uint64_t timeout_ns);

// vkDestroyDevice (design 6.10), before the call down and in this order:
//  - before_device_destroyed(): the device's swapchains still alive (an application error) are destroyed, as
//    vkDestroySwapchainKHR would; the display thread leaves the device (the handshake, at most one refresh); frame
//    fences still outstanding are waited for, up to 100 ms. Takes Presenter::lifecycle.
//  - the caller then idles the private queue holding its mutex, and keeps holding it;
//  - device_destroyed(): the device's frames end (destroyed, no ids completed) and its display events are forgotten,
//    then its frame fences are destroyed.
void before_device_destroyed(DeviceData& device);
void device_destroyed(DeviceData& device);

} // namespace dvl
