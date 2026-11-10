// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The display thread (design 6.7). DisplayLoop::pass() runs against fakes in fake time: the clock moves only inside the
// fake Waiter and FenceOps, FakeApi's counter goes up every 11.111 ms of it, and scripted actions (Monado queueing a
// frame, the display stopping) run at their fake times inside those waits, taking the session lock as Monado would.
// The last tests run the real DisplayThread and Win32Waiter, briefly.

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include "core/clock.hpp"
#include "core/display_loop.hpp"
#include "core/display_thread.hpp"
#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "core/stats.hpp"
#include "core/trace.hpp"
#include "fakes/fake_api.hpp"
#include "fakes/fake_clock.hpp"

namespace {

using nvdm::Acquired;
using nvdm::DisplayLoop;
using nvdm::DisplayLoopConfig;
using nvdm::DisplayThread;
using nvdm::FakeApi;
using nvdm::FakeClock;
using nvdm::FenceOps;
using nvdm::FencePool;
using nvdm::FenceSlot;
using nvdm::FenceState;
using nvdm::FrameModel;
using nvdm::FrameModelConfig;
using nvdm::FrameSeq;
using nvdm::StatsWindow;
using nvdm::SwapchainGen;
using nvdm::Waiter;

constexpr int64_t kUs = 1'000;
constexpr int64_t kMs = 1'000'000;
constexpr int64_t kP = FakeApi::kDefaultRefreshNs;
constexpr int64_t kT0 = 1'000'000'000; // the fake clock's start; FakeApi flips at kT0 + k * kP

// The first flip strictly after t, and the last one at or before it.
int64_t flip_after(int64_t t)
{
    return kT0 + ((t - kT0) / kP + 1) * kP;
}
int64_t flip_at_or_before(int64_t t)
{
    return kT0 + ((t - kT0) / kP) * kP;
}

bool contains(const std::vector<std::string>& lines, const std::string& part)
{
    return std::any_of(lines.begin(), lines.end(),
                       [&](const std::string& line) { return line.find(part) != std::string::npos; });
}

struct Rig;

// The "frame queued" event and the timer, in fake time. Auto-reset like the real one. A coarse wait behaves as a precise
// one here; it is only recorded as such.
struct FakeWaiter : Waiter {
    struct Wait {
        int64_t at;
        int64_t deadline;
        bool coarse = false;
        int64_t end = 0; // when it returned
    };
    Rig& rig;
    bool pending = false;
    std::vector<Wait> waits;
    std::function<void()> on_wait;
    // How much later than its deadline a wait that times out returns (a descheduled thread); none by default.
    std::function<int64_t(const Wait&)> late;
    explicit FakeWaiter(Rig& r) : rig(r) {}
    bool wait_frame_or_until(int64_t deadline_ns) override { return wait(deadline_ns, false); }
    bool wait_frame_or_until_coarse(int64_t deadline_ns) override { return wait(deadline_ns, true); }
    void signal_frame_queued() override { pending = true; }
    bool wait(int64_t deadline_ns, bool coarse);
};

// vkWaitForFences and vkGetFenceStatus on the frame fences, in fake time.
struct FakeFences : FenceOps {
    struct Call {
        int64_t at;
        FenceSlot slot;
        int64_t deadline; // 0 for a status
        bool wait;
    };
    Rig& rig;
    std::vector<Call> calls;
    std::function<void(FenceSlot)> during_wait;
    std::function<void(FenceSlot)> on_status; // at the entry of every status read
    bool lost = false; // a lost device: every wait fails at once, every status says not signalled
    explicit FakeFences(Rig& r) : rig(r) {}
    bool wait(FenceSlot slot, int64_t deadline_ns) override;
    bool status(FenceSlot slot) override;
};

struct Rig {
    FakeClock clock{kT0};
    FakeApi api;
    std::mutex lock; // the display-session lock
    std::map<FenceSlot, int64_t> ready_at; // when the GPU signals each frame fence
    FencePool pool{16, [this](FenceSlot slot) { return gpu_done(slot); }};
    FrameModel model{FrameModelConfig{}, &pool};
    StatsWindow stats;
    FakeWaiter waiter{*this};
    FakeFences fences{*this};
    std::unique_ptr<DisplayLoop> loop;

    std::vector<std::string> log;
    size_t loop_lines = 0; // the loop's own lines (config.log), as they are written
    std::multimap<int64_t, std::function<void()>> actions; // what the app and the display do, by fake time
    SwapchainGen gen = 0;
    uint64_t next_id = 0;
    struct Queued {
        FrameSeq seq;
        uint32_t buffer;
        FenceSlot fence;
        int64_t at;
        int64_t ready;
    };
    std::vector<Queued> queued;
    uint64_t acquire_misses = 0;
    struct Seen {
        int64_t at;
        int64_t qpc;
    };
    std::vector<Seen> refreshes_seen; // the display-event hook
    bool event_pending = false;
    uint64_t locked_calls = 0; // NVAPI calls, fence waits and timer waits made with the session lock held

    // The fake display flips every display_ns; the loop predicts with 1/90 s.
    explicit Rig(int64_t display_ns = kP, nvdm::Trace* trace = nullptr) : api(clock, display_ns)
    {
        model.set_log([this](const std::string& line) { log.push_back(line); });
        gen = model.add_swapchain();
        DisplayLoopConfig config;
        config.tags = {0x100, 0x101, 0x102};
        config.log = [this](const std::string& line) {
            log.push_back(line);
            ++loop_lines;
        };
        config.refresh_seen = [this](int64_t qpc) { refreshes_seen.push_back({now(), qpc}); };
        config.events_pending = [this] { return event_pending; };
        config.trace = trace;
        loop = std::make_unique<DisplayLoop>(api, model, lock, clock, waiter, fences, stats, config);
        api.on_call = [this](const char*) { note_lock(); };
    }

    int64_t now() { return clock.now_ns(); }
    bool gpu_done(FenceSlot slot)
    {
        const auto it = ready_at.find(slot);
        return it != ready_at.end() && it->second <= now();
    }
    // Counts a call made while the session lock is held. try_lock fails on a held std::mutex, also on the thread that
    // holds it (MSVC's try_lock does not recurse).
    void note_lock()
    {
        if (lock.try_lock()) {
            lock.unlock();
        } else {
            ++locked_calls;
        }
    }
    void set_clock(int64_t t)
    {
        if (t > now()) {
            clock.advance(t - now());
        }
    }
    void run_next_action()
    {
        const auto it = actions.begin();
        const int64_t at = it->first;
        std::function<void()> action = std::move(it->second);
        actions.erase(it);
        set_clock(at);
        action();
    }
    // Moves the clock to t, running the actions due on the way.
    void advance_to(int64_t t)
    {
        while (!actions.empty() && actions.begin()->first <= t) {
            run_next_action();
        }
        set_clock(t);
    }
    void at(int64_t t, std::function<void()> action) { actions.emplace(t, std::move(action)); }

    // Monado at `t`: acquire, then vkQueuePresentKHR with a fence the GPU signals gpu_ns later, then the event.
    void frame_at(int64_t t, int64_t gpu_ns)
    {
        at(t, [this, gpu_ns] { queue_frame(gpu_ns); });
    }
    void queue_frame(int64_t gpu_ns)
    {
        std::unique_lock<std::mutex> hold(lock, std::try_to_lock);
        REQUIRE(hold.owns_lock()); // the display thread never waits with the lock held
        const int64_t t = now();
        const std::optional<Acquired> a = model.acquire(gen, t);
        if (!a) {
            ++acquire_misses;
            return;
        }
        const std::optional<FenceSlot> fence = pool.take();
        REQUIRE(fence.has_value());
        ready_at[*fence] = t + gpu_ns;
        model.queue(a->seq, ++next_id, *fence, t);
        queued.push_back({a->seq, a->buffer, *fence, t, t + gpu_ns});
        hold.unlock();
        waiter.signal_frame_queued();
    }

    // Passes until the clock reaches t. A loop that spins (passes that do not move the clock) fails the guard.
    void run_until(int64_t t, int max_passes = 1'000'000)
    {
        int passes = 0;
        while (now() < t) {
            loop->pass();
            REQUIRE(++passes < max_passes);
        }
    }
    std::vector<FakeApi::Call> calls(const std::string& name) const
    {
        std::vector<FakeApi::Call> out;
        for (const FakeApi::Call& call : api.calls()) {
            if (call.name == name) {
                out.push_back(call);
            }
        }
        return out;
    }
    bool logged(const std::string& part) const { return contains(log, part); }
};

bool FakeWaiter::wait(int64_t deadline_ns, bool coarse)
{
    rig.note_lock();
    const size_t i = waits.size();
    waits.push_back({rig.now(), deadline_ns, coarse});
    if (on_wait) {
        on_wait();
    }
    while (!pending && !rig.actions.empty() && rig.actions.begin()->first <= deadline_ns) {
        rig.run_next_action();
    }
    if (pending) {
        pending = false; // auto-reset
        waits[i].end = rig.now();
        return true;
    }
    rig.set_clock(deadline_ns);
    if (late) {
        rig.advance_to(rig.now() + late(waits[i])); // what falls due meanwhile still happens
    }
    waits[i].end = rig.now();
    return false;
}

bool FakeFences::wait(FenceSlot slot, int64_t deadline_ns)
{
    rig.note_lock();
    calls.push_back({rig.now(), slot, deadline_ns, true});
    if (during_wait) {
        during_wait(slot);
    }
    if (lost) {
        return false; // VK_ERROR_DEVICE_LOST, at once
    }
    const auto it = rig.ready_at.find(slot);
    if (it != rig.ready_at.end() && it->second <= deadline_ns) {
        rig.advance_to(std::max(rig.now(), it->second));
        return true;
    }
    rig.advance_to(deadline_ns);
    return false;
}

bool FakeFences::status(FenceSlot slot)
{
    rig.note_lock();
    calls.push_back({rig.now(), slot, 0, false});
    if (on_status) {
        on_status(slot);
    }
    return !lost && rig.gpu_done(slot);
}

// A run with everything in it: frames at every phase of the refresh, short and long GPU work, a failed Present and a
// link drop.
void mixed_run(Rig& r)
{
    r.api.queue(FakeApi::Fn::Present, -5); // the first Present fails: its back-buffer is reserved
    for (int i = 0; i < 40; ++i) {
        r.frame_at(kT0 + (3 + i) * kP + (i % 7) * kMs + 300 * kUs, (i % 5) * kMs + 500 * kUs);
    }
    r.at(kT0 + 20 * kP + 5 * kMs, [&r] { r.api.reset_counter(); }); // a link drop
    r.run_until(kT0 + 50 * kP);
}

// FenceOps for the thread tests: every fence has signalled.
struct Signalled : FenceOps {
    bool wait(FenceSlot, int64_t) override { return true; }
    bool status(FenceSlot) override { return true; }
};

// A log sink several threads write to.
struct SharedLog {
    std::mutex mutex;
    std::vector<std::string> lines;
    std::function<void(const std::string&)> sink()
    {
        return [this](const std::string& line) {
            std::lock_guard<std::mutex> hold(mutex);
            lines.push_back(line);
        };
    }
    bool has(const std::string& part)
    {
        std::lock_guard<std::mutex> hold(mutex);
        return contains(lines, part);
    }
};

} // namespace

TEST_CASE("presents_within_fence_wake")
{
    Rig r;
    r.run_until(kT0 + 5 * kP); // started; the phase is known
    for (int i = 0; i < 5; ++i) {
        r.frame_at(kT0 + (6 + i) * kP + 3 * kMs, 2 * kMs); // GPU done 2 ms after the queue, 5 ms after a flip
    }
    r.run_until(kT0 + 13 * kP);

    REQUIRE(r.queued.size() == 5);
    const std::vector<FakeApi::Call> presents = r.calls("present");
    REQUIRE(presents.size() == 5);
    for (size_t i = 0; i < 5; ++i) {
        CHECK(presents[i].at_ns == r.queued[i].ready); // at the fake time its fence signalled
        CHECK(presents[i].arg == 0x100 + r.queued[i].buffer);
    }
    CHECK(r.model.completed_present_id(r.gen) == 5); // each went on screen at the flip after it
    CHECK(r.model.counters().missed == 0);
}

TEST_CASE("fence_wait_ends_before_predicted_refresh")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    for (int i = 0; i < 4; ++i) {
        r.frame_at(kT0 + (6 + 3 * i) * kP + 3 * kMs, 15 * kMs); // GPU work longer than a refresh period
    }
    r.run_until(kT0 + 20 * kP);

    REQUIRE(r.queued.size() == 4);
    int waits = 0;
    for (const FakeFences::Call& call : r.fences.calls) {
        if (!call.wait) {
            continue;
        }
        ++waits;
        const int64_t predicted = flip_after(call.at);
        CHECK(call.deadline <= predicted - 900 * kUs);
        CHECK(call.deadline >= predicted - 1100 * kUs); // the next refresh after now, never a stale one
    }
    CHECK(waits >= 8); // each frame's fence waited on in two refresh periods, with the poll between
    const std::vector<FakeApi::Call> presents = r.calls("present");
    REQUIRE(presents.size() == 4);
    for (size_t i = 0; i < 4; ++i) {
        CHECK(presents[i].at_ns == r.queued[i].ready); // still Presented as soon as its GPU work ended
    }
    CHECK(r.model.counters().refreshes >= 19); // and no refresh went unseen meanwhile
    CHECK(r.model.completed_present_id(r.gen) == 4);
}

TEST_CASE("poll_bounded_to_refresh_plus_2ms")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.frame_at(kT0 + 5 * kP + 3 * kMs, 1 * kMs);
    const int64_t stop_at = kT0 + 5 * kP + 6 * kMs;
    r.at(stop_at, [&r] { r.api.stop_counter(); }); // the display stops flipping: the Present stays outstanding
    r.run_until(kT0 + 5 * kP + 500 * kMs);         // short of "flips stopped" (600 ms)

    REQUIRE(r.calls("present").size() == 1);
    REQUIRE(r.model.present_outstanding());
    REQUIRE_FALSE(r.model.flips_stopped());
    std::set<int64_t> windows;
    for (const FakeApi::Call& call : r.calls("present_stats")) {
        if (call.at_ns <= stop_at) {
            continue;
        }
        const int64_t next = flip_after(call.at_ns);
        const int64_t last = flip_at_or_before(call.at_ns);
        const bool before_next = call.at_ns >= next - 1 * kMs; // the timer's ~1 ms before the predicted refresh
        const bool after_last = call.at_ns <= last + 2 * kMs;  // the poll ends at the predicted refresh + 2 ms
        CHECK((before_next || after_last));
        windows.insert(before_next ? next : last);
    }
    CHECK(windows.size() >= 44); // a poll at every predicted refresh (45 in 500 ms), and only there
    for (const FakeWaiter::Wait& wait : r.waiter.waits) {
        CHECK(wait.deadline - wait.at <= kP); // then back to step 1's timer
    }
}

TEST_CASE("idle_polls_coarsely")
{
    Rig r;
    r.run_until(kT0 + 1000 * kMs); // nothing queued, no Present outstanding, no display event

    CHECK(r.loop->idle());
    const size_t polls = r.calls("present_stats").size();
    CHECK(polls < 10 * 90); // under 10 polls per refresh (90 in a second)
    CHECK(polls >= 90);
    REQUIRE(r.refreshes_seen.size() >= 89); // every refresh still seen
    for (const Rig::Seen& seen : r.refreshes_seen) {
        CHECK((seen.qpc - kT0) % kP == 0); // at its exact time, from the stats: the phase does not drift
    }
    CHECK((r.model.last_refresh_ns() - kT0) % kP == 0);

    SECTION("a display event pending: not idle")
    {
        r.event_pending = true;
        const size_t before = r.calls("present_stats").size();
        r.run_until(kT0 + 1500 * kMs);
        CHECK_FALSE(r.loop->idle());
        CHECK(r.calls("present_stats").size() - before > 6 * 45); // it polls near each refresh instead
    }
}

TEST_CASE("display loop: an idle wait leaves the high-resolution timer alone, and only an idle one does")
{
    // Monado at 90 Hz, as through the layer: after each refresh it sleeps until 6 ms before the next one, then renders
    // (4 ms of GPU work) and presents. Between the credit and its present the loop is idle. While the loop has a
    // high-resolution timer armed, Windows wakes Monado's ordinary 1 ms timer up to ~1.5 ms late (M3 debugging,
    // 2026-10-31): so every idle wait is coarse (no high-resolution timer), and every other wait stays precise.
    Rig r;
    r.run_until(kT0 + 5 * kP);
    for (int i = 0; i < 20; ++i) {
        r.frame_at(kT0 + (6 + i) * kP + 5100 * kUs, 4 * kMs);
    }
    std::vector<bool> idle_at_wait;
    r.waiter.on_wait = [&r, &idle_at_wait] { idle_at_wait.push_back(r.loop->idle()); };
    const size_t first = r.waiter.waits.size();
    r.run_until(kT0 + 27 * kP);

    REQUIRE(r.model.completed_present_id(r.gen) == 20);
    CHECK(r.model.counters().missed == 0);
    REQUIRE(idle_at_wait.size() == r.waiter.waits.size() - first);
    size_t coarse = 0;
    for (size_t i = first; i < r.waiter.waits.size(); ++i) {
        const FakeWaiter::Wait& w = r.waiter.waits[i];
        CHECK(w.coarse == idle_at_wait[i - first]); // coarse exactly when idle
        coarse += w.coarse ? 1 : 0;
        if (w.coarse) {
            continue;
        }
        // Monado's sleep: from 0.3 ms after each refresh to its present 5.1 ms after it. No precise wait in it.
        for (int k = 7; k <= 26; ++k) {
            const int64_t flip = kT0 + k * kP;
            CHECK((w.end <= flip + 300 * kUs || w.at >= flip + 5100 * kUs));
        }
    }
    CHECK(coarse >= 20); // at least one in each frame's sleep
}

TEST_CASE("display loop: while flips are stopped, the waits stay precise")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.frame_at(kT0 + 5 * kP + 3 * kMs, 1 * kMs);
    r.at(kT0 + 5 * kP + 6 * kMs, [&r] { r.api.stop_counter(); });
    r.run_until(kT0 + 5 * kP + 700 * kMs);
    REQUIRE(r.model.flips_stopped());
    const size_t first = r.waiter.waits.size();
    r.run_until(kT0 + 5 * kP + 1000 * kMs);
    REQUIRE(r.waiter.waits.size() - first >= 30);
    for (size_t i = first; i < r.waiter.waits.size(); ++i) {
        CHECK_FALSE(r.waiter.waits[i].coarse); // flips stopped is not idle: its few-millisecond reads stay precise
    }
}

TEST_CASE("display loop: a wait that ends after the predicted refresh still polls that refresh")
{
    // The display thread is not run for a while (another thread, an interrupt storm): its step 1 wait, due 1 ms before
    // the refresh that shows a Present, returns 0.3 ms after that refresh. The poll for it is still due (until the
    // refresh + 2 ms): it reads at once, credits the Present and completes its id, rather than waiting for the next
    // refresh's poll a period later (M3 debugging, 2026-10-31: Monado's present-wait then returned 10.3 ms late).
    Rig r;
    r.run_until(kT0 + 5 * kP);
    const int64_t flip = kT0 + 6 * kP;
    r.frame_at(kT0 + 5 * kP + 3 * kMs, 2 * kMs); // Presented 5 ms after a refresh; shown at `flip`
    r.waiter.late = [flip](const FakeWaiter::Wait& w) {
        return w.deadline == flip - 1 * kMs ? int64_t{1'300'000} : int64_t{0};
    };
    r.run_until(flip + 400 * kUs);
    r.waiter.late = nullptr;
    CHECK(r.model.completed_present_id(r.gen) == 1);
    const auto seen = std::find_if(r.refreshes_seen.begin(), r.refreshes_seen.end(),
                                   [flip](const Rig::Seen& s) { return s.qpc == flip; });
    REQUIRE(seen != r.refreshes_seen.end());
    CHECK(seen->at <= flip + 400 * kUs); // read within 0.1 ms of the late wake, not at the next refresh's poll

    // A display that stops: the poll of an overdue refresh still ends at its predicted time + 2 ms, then the loop waits
    // for the next one (no spinning).
    r.frame_at(kT0 + 6 * kP + 3 * kMs, 2 * kMs);
    r.at(kT0 + 6 * kP + 6 * kMs, [&r] { r.api.stop_counter(); });
    const size_t reads = r.calls("present_stats").size();
    r.run_until(kT0 + 6 * kP + 300 * kMs);
    CHECK(r.calls("present_stats").size() - reads < 40 * 28); // about 31 reads per 3 ms poll window, 27 windows
    for (const FakeWaiter::Wait& w : r.waiter.waits) {
        CHECK(w.deadline - w.at <= kP);
    }
}

TEST_CASE("display loop: the trace records each frame's fence, Present and credit")
{
    nvdm::Trace trace(4096);
    Rig r(kP, &trace);
    r.run_until(kT0 + 5 * kP);
    r.frame_at(kT0 + 5 * kP + 5 * kMs, 2 * kMs);
    r.run_until(kT0 + 7 * kP);
    REQUIRE(r.model.completed_present_id(r.gen) == 1);
    REQUIRE(r.queued.size() == 1);
    const FrameSeq seq = r.queued[0].seq;
    const int64_t flip = kT0 + 6 * kP;

    std::map<nvdm::TraceKind, std::vector<std::pair<int64_t, int64_t>>> seen; // kind -> (t, b) of this frame's records
    for (size_t i = 0; i < trace.size(); ++i) {
        const nvdm::Trace::Record& rec = trace.at(i);
        const auto kind = static_cast<nvdm::TraceKind>(rec.kind.load());
        if (rec.a == seq || kind == nvdm::TraceKind::Counter) {
            seen[kind].push_back({rec.t_ns, rec.b});
        }
    }
    using K = nvdm::TraceKind;
    REQUIRE(seen[K::FenceWait].size() == 1);
    CHECK(seen[K::FenceWait][0].second == flip - 1 * kMs); // its deadline: ~1 ms before the refresh
    REQUIRE(seen[K::FenceDone].size() == 1);
    CHECK(seen[K::FenceDone][0] == std::make_pair(r.queued[0].ready, nvdm::kFenceSignalled)); // when the GPU was done
    REQUIRE(seen[K::NvPresentStart].size() == 1);
    CHECK(seen[K::NvPresentStart][0].first == r.queued[0].ready);
    REQUIRE(seen[K::NvPresentReturn].size() == 1);
    CHECK(seen[K::NvPresentReturn][0].second == 0); // its code
    REQUIRE(seen[K::Credit].size() == 1);
    CHECK(seen[K::Credit][0].second == flip); // the stats' QPC time of the refresh that showed it
    CHECK(seen[K::Counter].size() >= 6);      // and every counter increment the loop saw
    CHECK(trace.dropped() == 0);
}

TEST_CASE("display loop: with a display event pending, each refresh is seen within 0.3 ms")
{
    // A display a little slower than the prediction, as a real one may be: each flip comes 0.15 ms after the
    // predicted refresh, so only reads close together see it in time.
    constexpr int64_t kDisplay = kP + 150 * kUs;
    Rig r(kDisplay);
    r.event_pending = true;
    r.run_until(kT0 + 1000 * kMs);

    REQUIRE(r.refreshes_seen.size() >= 85);
    for (size_t i = 1; i < r.refreshes_seen.size(); ++i) { // the first comes before the phase is known
        CHECK(r.refreshes_seen[i].qpc == r.refreshes_seen[i - 1].qpc + kDisplay); // none missed
        CHECK(r.refreshes_seen[i].at - r.refreshes_seen[i].qpc <= 300 * kUs);     // design 6.9's requirement
    }
}

TEST_CASE("display loop: flips stopped, it reads every few milliseconds, then goes back")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.frame_at(kT0 + 5 * kP + 3 * kMs, 1 * kMs);
    r.at(kT0 + 5 * kP + 6 * kMs, [&r] { r.api.stop_counter(); }); // the display stops; the Present stays outstanding
    r.run_until(kT0 + 5 * kP + 700 * kMs);
    CHECK(r.model.flips_stopped());
    CHECK(r.logged("flips stopped")); // the model's line, once

    const size_t before = r.calls("present_stats").size();
    const size_t waits = r.waiter.waits.size();
    r.run_until(kT0 + 5 * kP + 1000 * kMs);
    const size_t reads = r.calls("present_stats").size() - before;
    CHECK(reads >= 30);  // every few milliseconds...
    CHECK(reads <= 150); // ...not a spin
    for (size_t i = waits; i < r.waiter.waits.size(); ++i) {
        CHECK(r.waiter.waits[i].deadline - r.waiter.waits[i].at >= 2 * kMs);
    }
    CHECK(r.refreshes_seen.size() == 5); // no refresh, so no display event set (refreshes 1-5 came before the stop)

    r.api.start_counter(); // the display comes back
    r.run_until(kT0 + 5 * kP + 1100 * kMs);
    CHECK_FALSE(r.model.flips_stopped());
    CHECK(r.logged("flips resumed"));
    CHECK(r.model.completed_present_id(r.gen) == 1); // the outstanding Present is credited at the first flip
}

TEST_CASE("stop_checked_every_pass")
{
    Rig r;
    r.run_until(kT0 + 3 * kP);
    const size_t calls = r.api.calls().size();

    SECTION("asked before a pass: no call and no wait")
    {
        r.loop->request_stop();
        CHECK(r.loop->stop_requested());
        const size_t waits = r.waiter.waits.size();
        r.loop->pass();
        r.loop->pass();
        CHECK(r.api.calls().size() == calls);
        CHECK(r.waiter.waits.size() == waits);
    }
    SECTION("asked during the pass's wait: no NVAPI call after it")
    {
        r.waiter.on_wait = [&r] { r.loop->request_stop(); };
        r.loop->pass();
        CHECK(r.api.calls().size() == calls);
    }
    SECTION("asked during a fence wait: no Present, and the fence is no longer marked waited")
    {
        r.frame_at(kT0 + 3 * kP + 3 * kMs, 2 * kMs);
        r.fences.during_wait = [&r](FenceSlot) { r.loop->request_stop(); };
        int passes = 0;
        while (!r.loop->stop_requested()) {
            r.loop->pass();
            REQUIRE(++passes < 100);
        }
        CHECK(r.calls("present").empty());
        REQUIRE(r.queued.size() == 1);
        r.model.remove_swapchain(r.gen);
        CHECK(r.pool.state(r.queued[0].fence) == FenceState::InPool); // seen signalled by the wait
        CHECK(r.pool.take() == r.queued[0].fence);                     // and free to hand out again
    }
    SECTION("a thread's loop ends at the pass after the request")
    {
        r.at(kT0 + 10 * kP, [&r] { r.loop->request_stop(); });
        int passes = 0;
        while (!r.loop->stop_requested()) {
            r.loop->pass();
            REQUIRE(++passes < 100'000);
        }
        CHECK(r.now() <= kT0 + 10 * kP + 3 * kMs);
        const size_t after = r.api.calls().size();
        r.loop->pass();
        CHECK(r.api.calls().size() == after);
    }
}

TEST_CASE("nvapi_called_without_lock")
{
    Rig r;
    mixed_run(r);
    CHECK(r.calls("present").size() >= 20);
    CHECK(r.calls("present_stats").size() >= 50);
    CHECK(r.fences.calls.size() >= 20);
    CHECK(r.waiter.waits.size() >= 50);
    CHECK(r.locked_calls == 0); // no NVAPI call, fence wait or timer wait with the session lock held
    CHECK(r.logged("returned -5"));
    CHECK(r.logged("link drop"));
    r.model.check_invariants();
}

TEST_CASE("display loop: every wait is bounded (invariant 6)")
{
    Rig r;
    mixed_run(r);
    for (const FakeWaiter::Wait& wait : r.waiter.waits) {
        CHECK(wait.deadline - wait.at <= kP);
    }
    for (const FakeFences::Call& call : r.fences.calls) {
        if (call.wait) {
            CHECK(call.deadline - call.at <= kP);
        }
    }
}

TEST_CASE("display loop: a fence wait that fails at once does not make it spin")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.fences.lost = true; // vkWaitForFences returns VK_ERROR_DEVICE_LOST at once from now on
    r.frame_at(kT0 + 5 * kP + 3 * kMs, 2 * kMs);
    r.run_until(kT0 + 25 * kP, 10'000); // a spin does not move the clock: the pass guard fails

    REQUIRE(r.queued.size() == 1);
    CHECK(r.calls("present").empty()); // never seen signalled
    const auto waits = std::count_if(r.fences.calls.begin(), r.fences.calls.end(),
                                     [](const FakeFences::Call& call) { return call.wait; });
    CHECK(waits >= 15);          // one fence wait per refresh period (20 of them)...
    CHECK(waits <= 2 * 20 + 2);  // ...not one per spin
    for (const FakeWaiter::Wait& wait : r.waiter.waits) {
        CHECK(wait.deadline - wait.at <= kP);
    }
}

TEST_CASE("display loop: flips stopped, a frame whose fence has not signalled does not make it spin")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.at(kT0 + 5 * kP + 6 * kMs, [&r] { r.api.stop_counter(); }); // the display stops, no Present outstanding
    r.run_until(kT0 + 5 * kP + 700 * kMs);
    REQUIRE(r.model.flips_stopped());

    // The one frame still allowed a Present, queued 0.5 ms before a refresh of the stale phase, so ~1 ms before that
    // refresh has passed already. Its GPU work ends 50 ms later.
    const int64_t queue_at = flip_after(kT0 + 5 * kP + 710 * kMs) - 500 * kUs;
    r.frame_at(queue_at, 50 * kMs);
    const size_t reads_before = r.calls("present_stats").size();
    const size_t fence_calls_before = r.fences.calls.size();
    r.run_until(queue_at + 40 * kMs, 10'000);

    REQUIRE(r.queued.size() == 1);
    CHECK(r.calls("present").empty());
    const size_t reads = r.calls("present_stats").size() - reads_before;
    CHECK(reads >= 5);  // a read every few milliseconds...
    CHECK(reads <= 20); // ...not a spin
    for (size_t i = fence_calls_before; i < r.fences.calls.size(); ++i) {
        if (r.fences.calls[i].wait) {
            CHECK(r.fences.calls[i].deadline - r.fences.calls[i].at >= 2 * kMs); // the stopped mode's wait
        }
    }

    r.run_until(queue_at + 100 * kMs, 10'000);
    const std::vector<FakeApi::Call> presents = r.calls("present");
    REQUIRE(presents.size() == 1);                   // Presented once (design 6.7 step 5)...
    CHECK(presents[0].at_ns == r.queued[0].ready);   // ...as soon as its fence signalled
}

TEST_CASE("display loop: a last refresh in the future does not hold its wait (invariant 6)")
{
    Rig r;
    r.event_pending = true; // active: step 1 waits for the predicted refresh
    r.run_until(kT0 + 5 * kP + 3 * kMs);
    nvdm::RefreshStats stats{};
    REQUIRE(r.api.present_stats(stats) == 0);
    {
        std::lock_guard<std::mutex> hold(r.lock);
        r.model.on_counter(stats.counter + 1, r.now() + 10'000 * kMs); // a bogus read: its refresh 10 s ahead
        REQUIRE(r.model.last_refresh_ns() > r.now() + 9'000 * kMs);
    }
    const size_t waits = r.waiter.waits.size();
    const int64_t until = r.now() + 100 * kMs;
    r.run_until(until, 10'000);

    REQUIRE(r.waiter.waits.size() > waits);
    for (size_t i = waits; i < r.waiter.waits.size(); ++i) {
        CHECK(r.waiter.waits[i].deadline - r.waiter.waits[i].at <= kP); // at most one period ahead
    }
    CHECK(r.now() < until + kP);
    CHECK(r.model.counters().refreshes > stats.counter + 1); // it went on polling the refreshes...
    CHECK(r.model.last_refresh_ns() <= r.now());           // ...and a real increment took over again
}

TEST_CASE("display loop: start sets both clocks to now and takes the first read as the baseline")
{
    Rig r;
    {
        // An earlier epoch saw its last refresh long ago, at another counter value.
        std::lock_guard<std::mutex> hold(r.lock);
        r.model.thread_started(0);
        r.model.on_counter(77, 0);
    }
    r.clock.advance(5 * kMs);
    const int64_t start = r.now();
    r.loop->pass();

    CHECK(r.model.last_refresh_ns() == start);
    CHECK(r.model.last_credited_ns() == start);
    CHECK_FALSE(r.model.flips_stopped()); // a restart is not mistaken for stopped flips
    CHECK_FALSE(r.logged("flips stopped"));
    CHECK(r.model.counters().refreshes == 0); // the jump from 77 is a baseline, not refreshes or a link drop
    CHECK(r.model.counters().link_drops == 0);

    r.run_until(flip_after(start) + 1 * kMs);
    CHECK(r.model.counters().refreshes == 1);
    CHECK(r.model.last_refresh_ns() == flip_after(start)); // the first increment fixes the phase
}

TEST_CASE("display loop: a frame whose GPU work ends in the poll window is Presented at once")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.frame_at(kT0 + 6 * kP - 3 * kMs, 2500 * kUs); // ready 0.5 ms before the flip, inside step 3's window
    r.run_until(kT0 + 8 * kP);

    const std::vector<FakeApi::Call> presents = r.calls("present");
    REQUIRE(presents.size() == 1);
    REQUIRE(r.queued.size() == 1);
    CHECK(presents[0].at_ns >= r.queued[0].ready);
    CHECK(presents[0].at_ns - r.queued[0].ready <= 100 * kUs); // within one poll step
    CHECK(std::any_of(r.fences.calls.begin(), r.fences.calls.end(),
                      [](const FakeFences::Call& call) { return !call.wait; })); // checked without waiting
    CHECK(r.model.completed_present_id(r.gen) == 1); // and credited at that flip
}

TEST_CASE("display loop: a frame gone during its fence wait is not Presented (invariant 5)")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.frame_at(kT0 + 5 * kP + 3 * kMs, 2 * kMs);
    int waits = 0;
    r.fences.during_wait = [&r, &waits](FenceSlot slot) {
        ++waits;
        std::unique_lock<std::mutex> hold(r.lock, std::try_to_lock);
        REQUIRE(hold.owns_lock());
        r.model.remove_swapchain(r.gen); // Monado destroys its swapchain: the queued frame is dropped
        CHECK(r.pool.state(slot) == FenceState::LeftOver);
        CHECK_FALSE(r.pool.oldest_left_over().has_value()); // marked waited: not offered to anyone else
    };
    r.run_until(kT0 + 8 * kP);

    CHECK(waits == 1);
    CHECK(r.calls("present").empty()); // the re-check after the wait found the frame gone
    REQUIRE(r.queued.size() == 1);
    CHECK(r.pool.state(r.queued[0].fence) == FenceState::InPool); // seen signalled in the wait's lock hold
    CHECK(r.pool.take() == r.queued[0].fence);                     // and no longer marked waited
}

TEST_CASE("display loop: a fence wait that times out does not mark the fence seen (invariant 2)")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    r.frame_at(kT0 + 5 * kP + 3 * kMs, 30 * kMs); // GPU work outlasts two fence waits
    r.at(kT0 + 6 * kP + 4 * kMs, [&r] {
        std::unique_lock<std::mutex> hold(r.lock, std::try_to_lock);
        REQUIRE(hold.owns_lock());
        r.model.remove_swapchain(r.gen); // dropped before its fence signalled
    });
    r.run_until(kT0 + 7 * kP);

    REQUIRE(r.queued.size() == 1);
    const FenceSlot fence = r.queued[0].fence;
    CHECK(std::count_if(r.fences.calls.begin(), r.fences.calls.end(),
                        [](const FakeFences::Call& call) { return call.wait; }) >= 2);
    CHECK(r.calls("present").empty());
    CHECK(r.pool.state(fence) == FenceState::LeftOver); // never seen signalled
    CHECK(r.pool.oldest_left_over() == fence);          // and no longer marked waited
    CHECK(r.pool.take() != fence);                      // unsignalled: not handed out
}

TEST_CASE("display loop: stats lines go to the log")
{
    Rig r;
    r.run_until(kT0 + 5 * kP);
    for (int i = 0; i < 92; ++i) {
        r.frame_at(kT0 + (6 + i) * kP + 3 * kMs, 2 * kMs);
    }
    r.run_until(kT0 + 100 * kP);
    CHECK(r.acquire_misses == 0);
    CHECK(r.logged("90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz), 0 missed (0.00 %), 0 link drops; dropped 0, "
                   "failed 0, unconfirmed 0, reserved 0, held 0, target skips 0;"));
}

TEST_CASE("display loop: no line is written between a frame's fence being seen and its Present")
{
    // Final review m1: the stats line made by the Present that closes a window (and any line pending then) is written
    // after that Present and its return time, so no file, console or debugger write delays a Present whose fence has
    // signalled.
    Rig r;
    size_t lines_at_fence = 0;
    size_t written_in_between = 0;
    size_t presents = 0;
    r.fences.during_wait = [&](FenceSlot) { lines_at_fence = r.loop_lines; };
    r.fences.on_status = [&](FenceSlot) { lines_at_fence = r.loop_lines; };
    r.api.on_call = [&](const char* name) {
        r.note_lock();
        if (std::string(name) == "present") {
            ++presents;
            written_in_between += r.loop_lines - lines_at_fence;
        }
    };
    r.run_until(kT0 + 5 * kP);
    for (int i = 0; i < 100; ++i) {
        r.frame_at(kT0 + (6 + i) * kP + 2 * kMs, 3 * kMs);
    }
    r.run_until(kT0 + 110 * kP);

    REQUIRE(presents >= 91);
    REQUIRE(r.logged("90 presents in")); // the first-90 line, made by the 91st Present
    CHECK(written_in_between == 0);
}

TEST_CASE("display loop: a refresh time in the future is taken as at most a period from now, said once a second")
{
    // A bogus GetPresentStats time: the model, the display events and the stats never see
    // a refresh more than a period ahead, and a good read is taken as it is again.
    Rig r;
    r.at(kT0 + 10 * kP, [&r] { r.api.qpc_offset_ns = 10'000 * kMs; }); // the reads' refresh time 10 s ahead
    r.at(kT0 + 200 * kP, [&r] { r.api.qpc_offset_ns = 0; });
    r.run_until(kT0 + 260 * kP);

    REQUIRE(r.refreshes_seen.size() > 200);
    for (const Rig::Seen& seen : r.refreshes_seen) {
        CHECK(seen.qpc <= seen.at + kP);
    }
    const size_t lines = static_cast<size_t>(std::count_if(r.log.begin(), r.log.end(), [](const std::string& line) {
        return line.find("in the future") != std::string::npos;
    }));
    CHECK(lines >= 2); // 190 refreshes are 2.1 s: once a second
    CHECK(lines <= 3);
    const Rig::Seen last = r.refreshes_seen.back();
    CHECK(last.qpc == flip_at_or_before(last.at)); // a good read again
    CHECK(r.model.last_refresh_ns() == last.qpc);
}

TEST_CASE("thread_epoch_waits_for_old")
{
    STATIC_REQUIRE(nvdm::kBatonWaitNs == 1'000'000'000); // the layer's wait; this test uses a shorter one
    constexpr int64_t kBaton = 200 * kMs;

    FakeClock display_clock; // never moves: the fake display's counter stays put
    FakeApi api(display_clock);
    nvdm::QpcClock clock;
    std::mutex lock;
    FencePool pool(16, [](FenceSlot) { return true; });
    FrameModel model(FrameModelConfig{}, &pool);
    StatsWindow stats;
    nvdm::Win32Waiter waiter(clock);
    Signalled fences;
    SharedLog log;

    // A Present that does not return until the test says so.
    HANDLE entered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE release = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    REQUIRE(entered != nullptr);
    REQUIRE(release != nullptr);
    api.on_call = [&](const char* name) {
        if (std::string(name) == "present") {
            SetEvent(entered);
            WaitForSingleObject(release, 10'000);
        }
    };
    api.queue(FakeApi::Fn::Present, -5); // if its return were written, its frame would fail

    DisplayThread thread(log.sink(), kBaton);
    auto make_loop = [&] {
        DisplayLoopConfig config;
        config.tags = {0x100, 0x101, 0x102};
        config.log = log.sink();
        return std::make_unique<DisplayLoop>(api, model, lock, clock, waiter, fences, stats, config);
    };
    SwapchainGen gen = 0;
    {
        std::lock_guard<std::mutex> hold(lock);
        model.set_log(log.sink());
        gen = model.add_swapchain();
    }
    thread.start(make_loop);
    CHECK(thread.epoch() == 1);
    {
        std::lock_guard<std::mutex> hold(lock);
        const std::optional<Acquired> a = model.acquire(gen, clock.now_ns());
        REQUIRE(a.has_value());
        const std::optional<FenceSlot> fence = pool.take();
        REQUIRE(fence.has_value());
        model.queue(a->seq, 1, *fence, clock.now_ns());
    }
    waiter.signal_frame_queued();
    REQUIRE(WaitForSingleObject(entered, 5'000) == WAIT_OBJECT_0); // epoch 1 is inside its Present

    CHECK_FALSE(thread.stop_and_join(50 * kMs)); // the teardown's join times out: epoch 1 is left behind
    CHECK(log.has("display thread epoch 1 did not stop within 50 ms"));
    const size_t calls = api.calls().size();
    thread.start(make_loop);
    CHECK(thread.epoch() == 2);
    Sleep(500); // two and a half baton waits
    CHECK(api.calls().size() == calls); // epoch 2 made no NVAPI call meanwhile
    CHECK(log.has("display thread epoch 2: epoch 1 is still inside an NVAPI call after 200 ms"));

    const size_t reads = api.count("present_stats");
    SetEvent(release); // epoch 1's Present returns: it exits, touching nothing
    bool started = false;
    for (int i = 0; i < 300 && !started; ++i) {
        Sleep(10);
        started = api.count("present_stats") > reads;
    }
    CHECK(started); // epoch 2 took over within its next check
    CHECK(log.has("display thread epoch 2: epoch 1 has left NVAPI"));
    CHECK(thread.stop_and_join(nvdm::kJoinTimeoutNs));
    {
        std::lock_guard<std::mutex> hold(lock);
        CHECK(model.counters().failed == 0); // epoch 1 never wrote its Present's return (-5)
        CHECK(model.present_outstanding());  // so its back-buffer stays presented, uncredited
    }
    CHECK(api.count("present") == 1);
    CloseHandle(entered);
    CloseHandle(release);
}

TEST_CASE("display thread: a new epoch after a clean stop starts at once")
{
    FakeClock display_clock;
    FakeApi api(display_clock);
    nvdm::QpcClock clock;
    std::mutex lock;
    FencePool pool;
    FrameModel model(FrameModelConfig{}, &pool);
    StatsWindow stats;
    nvdm::Win32Waiter waiter(clock);
    Signalled fences;
    SharedLog log;
    DisplayThread thread(log.sink());
    auto make_loop = [&] {
        DisplayLoopConfig config;
        config.tags = {0x100, 0x101, 0x102};
        return std::make_unique<DisplayLoop>(api, model, lock, clock, waiter, fences, stats, config);
    };
    for (uint64_t epoch = 1; epoch <= 3; ++epoch) {
        const size_t reads = api.count("present_stats");
        thread.start(make_loop);
        CHECK(thread.epoch() == epoch);
        bool running = false;
        for (int i = 0; i < 300 && !running; ++i) {
            Sleep(1);
            running = api.count("present_stats") > reads;
        }
        CHECK(running);
        const int64_t t0 = clock.now_ns();
        CHECK(thread.stop_and_join(nvdm::kJoinTimeoutNs));
        CHECK(clock.now_ns() - t0 < 100 * kMs); // a pass is short, and the stop wakes its wait
    }
    CHECK_FALSE(log.has("still inside"));
    CHECK(log.has("display thread epoch 3 started"));
    CHECK(thread.stop_and_join(nvdm::kJoinTimeoutNs)); // nothing running: nothing to stop
}

TEST_CASE("display thread: an error inside a pass ends the epoch, logged, and gives the baton back")
{
    FakeClock display_clock;
    FakeApi api(display_clock);
    nvdm::QpcClock clock;
    std::mutex lock;
    FencePool pool;
    FrameModel model(FrameModelConfig{}, &pool);
    StatsWindow stats;
    nvdm::Win32Waiter waiter(clock);
    Signalled fences;
    SharedLog log;
    std::atomic<int> fail{1};
    api.on_call = [&fail](const char*) {
        if (fail.exchange(0) != 0) {
            throw std::runtime_error("a broken invariant");
        }
    };
    DisplayThread thread(log.sink(), 200 * kMs);
    auto make_loop = [&] {
        DisplayLoopConfig config;
        config.tags = {0x100, 0x101, 0x102};
        return std::make_unique<DisplayLoop>(api, model, lock, clock, waiter, fences, stats, config);
    };
    thread.start(make_loop);
    bool told = false;
    for (int i = 0; i < 300 && !told; ++i) {
        Sleep(1);
        told = log.has("display thread epoch 1 stopped by an error, a bug: a broken invariant");
    }
    CHECK(told);
    CHECK(thread.stop_and_join(nvdm::kJoinTimeoutNs)); // it has exited

    const size_t reads = api.count("present_stats");
    thread.start(make_loop);
    bool running = false;
    for (int i = 0; i < 100 && !running; ++i) {
        Sleep(1);
        running = api.count("present_stats") > reads;
    }
    CHECK(running); // within 100 ms: the baton was free, no 200 ms wait
    CHECK(thread.stop_and_join(nvdm::kJoinTimeoutNs));
    CHECK_FALSE(log.has("still inside"));
}

TEST_CASE("Win32Waiter: the coarse wait, without the high-resolution timer")
{
    nvdm::QpcClock clock;
    nvdm::Win32Waiter waiter(clock);

    waiter.signal_frame_queued();
    int64_t t0 = clock.now_ns();
    CHECK(waiter.wait_frame_or_until_coarse(t0 + 500 * kMs));
    CHECK(clock.now_ns() - t0 < 50 * kMs); // at once

    t0 = clock.now_ns();
    CHECK_FALSE(waiter.wait_frame_or_until_coarse(t0 + 5 * kMs)); // auto-reset: one signal woke one wait
    const int64_t waited = clock.now_ns() - t0;
    CHECK(waited >= 5 * kMs); // never before its deadline...
    CHECK(waited < 60 * kMs); // ...and bounded: the system timer's tick (15.6 ms without timeBeginPeriod) at most, a few times over

    t0 = clock.now_ns();
    CHECK_FALSE(waiter.wait_frame_or_until_coarse(t0 - 1)); // a deadline passed: it only looks at the event
    CHECK(clock.now_ns() - t0 < 5 * kMs);

    std::thread other([&waiter] {
        Sleep(5);
        waiter.signal_frame_queued();
    });
    t0 = clock.now_ns();
    CHECK(waiter.wait_frame_or_until_coarse(t0 + 2000 * kMs)); // another thread's signal ends a long wait early
    CHECK(clock.now_ns() - t0 < 500 * kMs);
    other.join();
}

TEST_CASE("Win32Waiter: an auto-reset event and a high-resolution timer")
{
    nvdm::QpcClock clock;
    nvdm::Win32Waiter waiter(clock);
    CHECK(waiter.high_resolution());

    waiter.signal_frame_queued();
    int64_t t0 = clock.now_ns();
    CHECK(waiter.wait_frame_or_until(t0 + 500 * kMs));
    CHECK(clock.now_ns() - t0 < 50 * kMs); // at once

    t0 = clock.now_ns();
    CHECK_FALSE(waiter.wait_frame_or_until(t0 + 5 * kMs)); // auto-reset: one signal woke one wait
    int64_t waited = clock.now_ns() - t0;
    CHECK(waited >= 5 * kMs);
    CHECK(waited < 50 * kMs);

    t0 = clock.now_ns();
    CHECK_FALSE(waiter.wait_frame_or_until(t0 + 300 * kUs)); // a short wait ends at its deadline too
    waited = clock.now_ns() - t0;
    CHECK(waited >= 300 * kUs);
    CHECK(waited < 20 * kMs);

    t0 = clock.now_ns();
    CHECK_FALSE(waiter.wait_frame_or_until(t0 - 1)); // a deadline passed: it only looks at the event
    CHECK(clock.now_ns() - t0 < 5 * kMs);

    std::thread other([&waiter] {
        Sleep(5);
        waiter.signal_frame_queued();
    });
    t0 = clock.now_ns();
    CHECK(waiter.wait_frame_or_until(t0 + 2000 * kMs)); // another thread's signal ends a long wait early
    CHECK(clock.now_ns() - t0 < 500 * kMs);
    other.join();
}
