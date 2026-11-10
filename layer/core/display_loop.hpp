// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "api.hpp"
#include "clock.hpp"
#include "fence_pool.hpp"
#include "frame_model.hpp"
#include "stats.hpp"
#include "trace.hpp"

namespace nvdm {

// The display thread's wait (design 6.7 step 1): the "frame queued" event, or a time on the injected clock.
class Waiter {
public:
    virtual ~Waiter() = default;
    // True as soon as signal_frame_queued() has been called since the last wait that returned true (the event is
    // auto-reset: one signal ends one wait), false once the clock reaches deadline_ns. A deadline at or before now only
    // looks at the event. Never waits much past the deadline (design invariant 6).
    virtual bool wait_frame_or_until(int64_t deadline_ns) = 0;
    // The same wait, for the idle poll (design 6.7, Idle), which need not end on time: it never arms the
    // high-resolution timer, and may end up to about two timer ticks of the process after deadline_ns: a Win32
    // timeout can end a little early, and the wait then goes on for at least 1 ms more (a tick is 1 ms while Monado
    // sleeps for its next frame, 15.6 ms by default). Never before the deadline, unless the event ends it.
    virtual bool wait_frame_or_until_coarse(int64_t deadline_ns) = 0;
    // Sets the event. Any thread, no lock needed: vkQueuePresentKHR after it queued a frame, and request_stop().
    virtual void signal_frame_queued() = 0;
};

// The real Waiter: an auto-reset event and a CREATE_WAITABLE_TIMER_HIGH_RESOLUTION waitable timer, waited on together
// (WaitForMultipleObjects). Waits shorter than about a millisecond, which the timer cannot time, poll the event and
// yield until the deadline instead: step 3's pauses between two reads, inside its bounded window.
//
// The coarse wait is the event alone with a Win32 timeout (an ordinary timer). While a high-resolution timer is armed,
// Windows expires the ordinary timers of other threads late, in 0.5 ms steps up to ~1.5 ms: Monado's compositor
// sleeps on one (os_precise_sleeper: timeBeginPeriod(1) and an ordinary waitable timer) between a refresh and its
// next frame, which is exactly when the display thread is idle. A frame that starts 1.5 ms late misses its refresh
// (M3 debugging, 2026-10-31: 0.5-0.7 % of refreshes missed with gaze_world through the layer).
class Win32Waiter : public Waiter {
public:
    explicit Win32Waiter(Clock& clock);
    ~Win32Waiter() override;
    Win32Waiter(const Win32Waiter&) = delete;
    Win32Waiter& operator=(const Win32Waiter&) = delete;

    bool wait_frame_or_until(int64_t deadline_ns) override;
    bool wait_frame_or_until_coarse(int64_t deadline_ns) override;
    void signal_frame_queued() override;

    // False if this Windows could not make a high-resolution timer and an ordinary one stands in (for the log).
    bool high_resolution() const { return high_resolution_; }

private:
    Clock& clock_;
    HANDLE event_ = nullptr;
    HANDLE timer_ = nullptr;
    bool high_resolution_ = false;
};

// The frame fences, by slot (vkWaitForFences and vkGetFenceStatus on the device's VkFence for the slot). The display
// loop calls both without holding any lock (design invariant 7).
class FenceOps {
public:
    virtual ~FenceOps() = default;
    // True if the fence signalled by deadline_ns (on the loop's clock), false if not. A deadline at or before now only
    // looks. False only once the deadline has passed, or on an error (a lost device); after an early false the loop
    // waits out the rest of the time itself, so it never spins on a failing wait.
    virtual bool wait(FenceSlot slot, int64_t deadline_ns) = 0;
    // True if the fence has signalled.
    virtual bool status(FenceSlot slot) = 0;
};

struct DisplayLoopConfig {
    std::vector<uint64_t> tags;        // the NVAPI tag of each back-buffer, by index (DisplaySession::back_buffer)
    int64_t refresh_ns = 11'111'111;   // the mode's refresh period (1/90 s)
    int64_t lead_ns = 1'000'000;       // step 1's timer and step 2's fence wait end this long before the predicted refresh
    int64_t poll_after_ns = 2'000'000; // step 3 polls until the predicted refresh plus this
    int64_t poll_step_ns = 100'000;    // between two reads of step 3
    int64_t idle_poll_ns = 3'000'000;  // idle: a read this often (a coarse wait: up to about two timer ticks late)
    int64_t stopped_poll_ns = 4'000'000; // flips stopped: a read this often (design 6.7 step 5)

    // The loop's own lines (stats lines, failing reads), one call per line, outside the lock. Unset: dropped. The
    // frame model logs its events itself, through FrameModel::set_log().
    std::function<void(const std::string&)> log;
    // Each counter increment the loop sees: the stats' QPC time of the read's last refresh, oldest first. The layer
    // sets the CPU event of every display event registered before that time (design 6.7 step 4, 6.9). Called under
    // the lock.
    std::function<void(int64_t qpc_ns)> refresh_seen;
    // True while a display event is registered and not yet set: the loop then polls near each refresh instead of
    // coarsely (design 6.7, Idle). Called under the lock. Unset: none.
    std::function<bool()> events_pending;
    // Per-frame timing records (TraceKind), or none. Not owned; it must outlive the loop.
    Trace* trace = nullptr;
};

// One pass of the display thread (design 6.7): the thread that makes every per-frame NVAPI call. It owns no thread;
// DisplayThread runs it, and tests call pass() with fakes.
//
// Each pass checks the stop request first, then:
//  1. waits for the "frame queued" event or a timer: ~1 ms before the predicted refresh; a few times per refresh period
//     while idle (nothing queued, no Present outstanding, no display event pending), with a coarse wait that arms no
//     high-resolution timer (Win32Waiter says why); every few milliseconds while flips are stopped. No wait when a
//     Present can be made at once.
//  2. Presents when frames are queued and none is outstanding: waits on the newest queued frame's fence until ~1 ms
//     before the predicted refresh (flips stopped: until the next stopped-mode read, and no sooner if no Present was
//     made); once it has signalled, re-checks under the lock (FrameModel::present_started) and calls NVAPI Present
//     outside it, writing its start and return times under the lock before and after. A fence wait that fails early
//     is waited out.
//  3. near the predicted refresh, polls GetPresentStats until the counter moves or the predicted refresh + 2 ms,
//     checking the newest queued fence without waiting between reads, so a frame that becomes ready is Presented at
//     once. Idle or stopped, it reads once instead.
//  4. hands every read to the frame model (credit, unconfirmed, link drops), each increment to refresh_seen, and the
//     model's counters and every read to the stats; logs each stats line. A read whose refresh time is more than a
//     period after now is bogus and taken as one period from now (logged at most once a second).
//  5. ticks the model, which stops and resumes flips (FrameModel::tick).
// The first pass also starts the epoch: FrameModel::thread_started(now) and the first read as the counter's baseline.
// The prediction is the next refresh period after now, counted from the model's last refresh; but a predicted refresh
// that has passed unread while its poll window (+ 2 ms) is still open (a wait that ended late) is polled at once.
//
// Locking (design invariants 5-8): every model and stats call is made under `lock` (the display-session lock); no
// NVAPI call, fence wait or timer wait is. The loop's lines go to `log` outside the lock, and never between a frame's
// fence seen signalled and its Present: the stats line a Present closes is written after it (design 6.7). A fence is
// marked waited under the lock before the loop lets go of it to wait on it or read its status, and is seen and unmarked
// in one lock hold after (the fence protocol, frame_model.hpp).
// Only the display thread calls pass(); only one epoch's loop calls NVAPI at a time (DisplayThread's baton).
//
// Everything passed in must outlive the loop, and, for a loop left behind in an NVAPI call (abandon()), the thread
// running it.
class DisplayLoop {
public:
    DisplayLoop(Api& api, FrameModel& model, std::mutex& lock, Clock& clock, Waiter& waiter, FenceOps& fences,
                StatsWindow& stats, DisplayLoopConfig config = {});
    DisplayLoop(const DisplayLoop&) = delete;
    DisplayLoop& operator=(const DisplayLoop&) = delete;

    // One pass, as above. Returns at once once a stop has been requested. Never waits longer than a refresh period at a
    // time (an idle wait: up to about two system timer ticks, 31 ms by default), so a pass ends within about that, plus
    // an NVAPI call.
    void pass();

    // Any thread. The current pass ends at its next check (after each wait and NVAPI call) and every later one at once.
    // It also wakes the pass's wait (signal_frame_queued()).
    void request_stop();
    bool stop_requested() const;

    // The last pass found nothing to do but watch the refreshes: the coarse polling of design 6.7, Idle.
    bool idle() const;

    // The thread running this loop was left behind by a join that timed out (design 6.7, thread epochs). Requests a stop
    // and, under the lock, marks the loop so that it touches nothing more: whatever NVAPI call it is in, it returns from
    // it to make no model or stats call, not even its Present's return time (that Present then becomes reserved after
    // three increments, FrameModel::on_counter). Any thread but the loop's own, not holding the lock.
    void abandon();

private:
    enum class Mode {
        Active,  // something to present, credit or report: wait for the poll window
        Idle,    // nothing: coarse polling
        Stopped, // flips stopped: a read every few milliseconds
    };
    struct Plan {
        int64_t now = 0;
        int64_t target = 0; // the predicted refresh
        Mode mode = Mode::Idle;
        bool ready = false; // a Present may be made now (FrameModel::candidate)
    };

    void begin();
    Plan plan();
    int64_t predict(int64_t last_refresh_ns, int64_t now_ns) const;
    bool present_newest(int64_t deadline_ns, bool wait);
    void poll(int64_t target_ns);
    bool read_counter();
    void take_reads(int code, const RefreshStats& stats, int64_t now_ns);
    void say(std::string line);
    void flush_log();
    // A trace record stamped now; no clock read when there is no trace.
    void note(TraceKind kind, uint64_t a = 0, int64_t b = 0)
    {
        if (config_.trace != nullptr) {
            config_.trace->add(kind, clock_.now_ns(), a, b);
        }
    }

    Api& api_;
    FrameModel& model_;
    std::mutex& lock_;
    Clock& clock_;
    Waiter& waiter_;
    FenceOps& fences_;
    StatsWindow& stats_;
    DisplayLoopConfig config_;

    std::atomic<bool> stop_{false};
    std::atomic<bool> abandoned_{false}; // set under lock_
    std::atomic<bool> idle_{false};

    // The loop thread's own.
    bool started_ = false;
    bool read_failing_ = false;
    int64_t read_failed_logged_ns_ = 0;
    bool future_logged_ = false;      // a refresh time in the future has been logged...
    int64_t future_logged_ns_ = 0;    // ...last at this time
    std::vector<std::string> pending_log_; // lines made under the lock, logged after it
    FrameSeq traced_present_ = 0;          // the frame of the outstanding Present, for TraceKind::Credit
};

} // namespace nvdm
