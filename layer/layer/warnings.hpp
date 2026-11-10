// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "core/frame_model.hpp"

namespace dvl {

// Design 8's degradation warnings (anti-goal "silent degradation"). Each fires at most once per app session; a session
// starts at the first present after more than kAppSessionGapNs without one, as the stats' windows do (design 6.8).
//
// Two kinds:
// - standing conditions of the presenting device and swapchain (set_*): the device was made without
//   VK_EXT_display_control, a present mode other than FIFO, the import's memory-type fallback. Each is warned at the
//   first present of every app session while it holds;
// - things that happen (on_*): no vkRegisterDisplayEventEXT, or no vkWaitForPresentKHR, in the first 90 presents of
//   the session; a display event with no wait on it within 2 s; flips stopped; frames dropped, failed or unconfirmed;
//   a back-buffer reserved; the fence fallback; a display thread left behind.
// Pure: no clock and no log of its own. Callers hold the display-session lock and write take_lines() to the log as
// warnings once they have let go of it.
class Warnings {
public:
    static constexpr int64_t kAppSessionGapNs = 1'000'000'000;
    static constexpr uint64_t kCheckAfterPresents = 90;
    static constexpr int64_t kEventWaitNs = 2'000'000'000;

    void set_display_control(bool enabled);
    void set_present_mode(VkPresentModeKHR mode);
    void set_memory_fallback(bool used);

    // vkQueuePresentKHR on our swapchain, at `now_ns`.
    void on_present(int64_t now_ns);
    // vkWaitForPresentKHR on our swapchain.
    void on_present_wait();
    // The 2 s rule: an event registered at `now_ns` (`key` names its fence), a wait or status read on it reaching the
    // layer, its fence destroyed; check() warns if one registered at least 2 s before `now_ns` had no wait or status
    // read, destroyed or not (Monado always waits before it destroys, so a destroy alone means the wait went past the
    // layer). Each event counts once.
    void on_event_registered(uint64_t key, int64_t now_ns);
    void on_event_waited(uint64_t key);
    void on_event_gone(uint64_t key);
    void check(int64_t now_ns);
    // The frame model's counters (they only grow) and whether flips are stopped: an increase of dropped, failed,
    // unconfirmed or reserved warns, as do stopped flips.
    void on_counters(const nvdm::FrameModel::Counters& counters, bool flips_stopped);
    // A present that found no fence and waited for its queue to go idle (design 6.5 invariant 2).
    void on_fence_fallback();
    // A teardown's join timed out and left the display thread inside NVAPI (design 6.10).
    void on_thread_left_behind();

    // The warnings since the last call, oldest first.
    std::vector<std::string> take_lines();

private:
    enum class Kind {
        NoDisplayControl,
        PresentMode,
        MemoryFallback,
        NoDisplayEvent,
        NoPresentWait,
        EventNotWaited,
        FlipsStopped,
        Dropped,
        Failed,
        Unconfirmed,
        Reserved,
        FenceFallback,
        ThreadLeftBehind,
    };
    void fire(Kind kind, std::string line);
    void begin_session();

    bool display_control_ = true;
    VkPresentModeKHR present_mode_ = VK_PRESENT_MODE_FIFO_KHR;
    bool memory_fallback_ = false;

    bool in_session_ = false;
    int64_t last_present_ns_ = 0;
    uint64_t presents_ = 0;
    bool saw_event_ = false;
    bool saw_present_wait_ = false;
    std::set<Kind> fired_;
    std::map<uint64_t, int64_t> unwaited_; // events with no wait yet, by key: when they were registered
    nvdm::FrameModel::Counters counters_;
    std::vector<std::string> lines_;
};

} // namespace dvl
