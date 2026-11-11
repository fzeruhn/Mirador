// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The frame model (design 6.5-6.8), one rule at a time. Every model call goes through M, which runs
// check_invariants() after it.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"

namespace {

using nvdm::Acquired;
using nvdm::BufferState;
using nvdm::Candidate;
using nvdm::FenceSlot;
using nvdm::FenceState;
using nvdm::FencePool;
using nvdm::FrameEnd;
using nvdm::FrameModel;
using nvdm::FrameModelConfig;
using nvdm::FrameSeq;
using nvdm::FrameState;
using nvdm::SwapchainGen;
using Via = nvdm::Acquired::Via;

constexpr int64_t kMs = 1'000'000;
constexpr int64_t kRefresh = 11'111'111;

// The model, a pool whose fences signal when a test says so, and the log. Every call checks the invariants after
// itself; the helpers below the wrappers script the usual steps.
struct M {
    std::set<FenceSlot> signalled;
    FencePool pool{16, [this](FenceSlot slot) { return signalled.count(slot) > 0; }};
    FrameModel model;
    std::vector<std::string> log;
    std::vector<nvdm::FrameReport> reports;
    std::map<FrameSeq, FenceSlot> fence_of;
    uint32_t counter = 1000;

    explicit M(FrameModelConfig config = {}) : model(config, &pool)
    {
        model.set_log([this](const std::string& line) { log.push_back(line); });
        model.set_report([this](const nvdm::FrameReport& report) { reports.push_back(report); });
        check();
    }

    void check() const { model.check_invariants(); }

    // Checked wrappers, one per model call.
    SwapchainGen add_swapchain()
    {
        const SwapchainGen gen = model.add_swapchain();
        check();
        return gen;
    }
    void remove_swapchain(SwapchainGen gen)
    {
        model.remove_swapchain(gen);
        check();
    }
    std::optional<Acquired> acquire(SwapchainGen gen, int64_t now)
    {
        const std::optional<Acquired> got = model.acquire(gen, now);
        check();
        return got;
    }
    void queue(FrameSeq seq, uint64_t id, FenceSlot fence, int64_t qpc = 0)
    {
        model.queue(seq, id, fence, qpc);
        check();
    }
    void hold(FrameSeq seq, int64_t not_before)
    {
        model.hold(seq, not_before);
        check();
    }
    void fence_seen(FenceSlot fence)
    {
        model.fence_seen(fence);
        check();
    }
    // A wait outside the lock that saw the fence signalled, as the fence protocol (frame_model.hpp) has it: marked
    // waited before the wait, then seen and unmarked in one lock hold.
    void wait_seen(FenceSlot fence)
    {
        model.fence_waiting(fence, true);
        check();
        model.fence_seen(fence);
        model.fence_waiting(fence, false);
        check();
    }
    std::optional<Candidate> candidate(int64_t now)
    {
        const std::optional<Candidate> c = model.candidate(now);
        check();
        return c;
    }
    bool present_started(FrameSeq seq, int64_t qpc)
    {
        const bool ok = model.present_started(seq, qpc);
        check();
        return ok;
    }
    void present_returned(FrameSeq seq, int code, int64_t qpc)
    {
        model.present_returned(seq, code, qpc);
        check();
    }
    void on_counter(uint32_t value, int64_t qpc)
    {
        model.on_counter(value, qpc);
        check();
    }
    void tick(int64_t now)
    {
        model.tick(now);
        check();
    }
    void thread_started(int64_t now)
    {
        model.thread_started(now);
        check();
    }
    void device_destroyed()
    {
        model.device_destroyed();
        check();
    }
    void set_pool(FencePool* next)
    {
        model.set_pool(next);
        check();
    }
    BufferState state(uint32_t buffer) const
    {
        const BufferState s = model.buffer_state(buffer);
        check();
        return s;
    }
    uint64_t completed(SwapchainGen gen) const
    {
        const uint64_t id = model.completed_present_id(gen);
        check();
        return id;
    }
    FrameModel::Counters counters() const
    {
        const FrameModel::Counters c = model.counters();
        check();
        return c;
    }
    std::optional<FrameEnd> end_of(FrameSeq seq) const
    {
        const std::optional<FrameEnd> e = model.frame_end(seq);
        check();
        return e;
    }
    std::optional<FrameState> state_of(FrameSeq seq) const
    {
        const std::optional<FrameState> s = model.frame_state(seq);
        check();
        return s;
    }
    int64_t bound() const
    {
        const int64_t b = model.acquire_bound_ns();
        check();
        return b;
    }

    // Script helpers.
    void start(int64_t now)
    {
        thread_started(now);
        on_counter(counter, now); // the first read is the baseline
    }
    void vblank(int64_t qpc) { on_counter(++counter, qpc); }
    Acquired acq(SwapchainGen gen, int64_t now)
    {
        const std::optional<Acquired> got = acquire(gen, now);
        REQUIRE(got.has_value());
        return *got;
    }
    FenceSlot take_fence()
    {
        const std::optional<FenceSlot> slot = pool.take();
        REQUIRE(slot.has_value());
        signalled.erase(*slot); // reset for its new submit
        return *slot;
    }
    // acquire, then present (queue) with a fresh fence.
    Acquired frame(SwapchainGen gen, int64_t now, uint64_t id)
    {
        const Acquired a = acq(gen, now);
        const FenceSlot fence = take_fence();
        fence_of[a.seq] = fence;
        queue(a.seq, id, fence);
        return a;
    }
    // The display thread: the fence seen signalled, the Present, its return.
    void present(FrameSeq seq, int64_t start_qpc, int64_t return_qpc, int code = 0)
    {
        signalled.insert(fence_of.at(seq));
        wait_seen(fence_of.at(seq));
        REQUIRE(present_started(seq, start_qpc));
        present_returned(seq, code, return_qpc);
    }
    bool logged(const std::string& part) const
    {
        return std::any_of(log.begin(), log.end(),
                           [&](const std::string& line) { return line.find(part) != std::string::npos; });
    }
};

} // namespace

TEST_CASE("acquire_never_hands_out_presented_on_screen_or_reserved")
{
    SECTION("a back-buffer on screen, one reserved, one presented: nothing to hand out before the bound")
    {
        M m;
        m.start(0);
        const SwapchainGen g = m.add_swapchain();
        const Acquired a = m.frame(g, 1 * kMs, 1);
        m.present(a.seq, 4 * kMs, 4 * kMs + 50'000);
        m.vblank(kRefresh); // a on screen
        const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
        m.present(b.seq, kRefresh + 4 * kMs, kRefresh + 4 * kMs + 50'000, 5); // reserved
        m.vblank(2 * kRefresh);
        const Acquired c = m.frame(g, 2 * kRefresh + 1 * kMs, 3);
        m.present(c.seq, 2 * kRefresh + 4 * kMs, 2 * kRefresh + 4 * kMs + 50'000); // presented

        CHECK(m.state(a.buffer) == BufferState::OnScreen);
        CHECK(m.state(b.buffer) == BufferState::Reserved);
        CHECK(m.state(c.buffer) == BufferState::Presented);
        CHECK_FALSE(m.acquire(g, 2 * kRefresh + 5 * kMs).has_value());

        m.vblank(3 * kRefresh); // c credited: a and b become idle
        const Acquired d = m.acq(g, 3 * kRefresh + 1 * kMs);
        CHECK(d.via == Via::Free);
        CHECK(d.buffer != c.buffer);
    }

    SECTION("a long walk through acquire, present, failed Presents, dead displays and recreates")
    {
        M m;
        uint64_t rng = 0x9e3779b97f4a7c15ull;
        auto next = [&](uint64_t n) {
            rng = rng * 6364136223846793005ull + 1442695040888963407ull;
            return (rng >> 33) % n;
        };
        int64_t t = 0;
        int64_t next_vblank = kRefresh;
        bool display_alive = true;
        int64_t died_at = 0;
        m.start(0);
        SwapchainGen gen = m.add_swapchain();
        std::optional<SwapchainGen> old_gen;
        std::optional<FrameSeq> held;     // the current swapchain's acquired frame
        std::optional<FrameSeq> old_held; // one left acquired on the retired swapchain
        uint64_t id = 0;
        int by_via[4] = {};
        std::vector<FenceSlot> gpu_busy; // fences whose GPU work ends at the next step

        for (int step = 0; step < 20000; ++step) {
            t += static_cast<int64_t>(next(3) + 1) * kMs / 2;
            m.signalled.insert(gpu_busy.begin(), gpu_busy.end());
            gpu_busy.clear();
            while (t >= next_vblank) {
                if (display_alive) {
                    m.vblank(next_vblank);
                }
                next_vblank += kRefresh;
            }
            m.tick(t);
            switch (next(10)) {
            case 0:
            case 1: // acquire
                if (!held) {
                    BufferState before[3] = {m.state(0), m.state(1), m.state(2)};
                    const bool stopped = m.model.flips_stopped() || t - m.model.last_refresh_ns() >= 600 * kMs;
                    const std::optional<Acquired> got = m.acquire(gen, t);
                    if (got) {
                        REQUIRE(before[got->buffer] != BufferState::OnScreen);
                        if (got->via == Via::Free || got->via == Via::DroppedQueued) {
                            REQUIRE(before[got->buffer] == BufferState::Idle);
                        } else if (got->via == Via::Reserved) {
                            REQUIRE(before[got->buffer] == BufferState::Reserved);
                        } else {
                            REQUIRE(before[got->buffer] == BufferState::Presented);
                            REQUIRE(stopped);
                        }
                        ++by_via[static_cast<int>(got->via)];
                        held = got->seq;
                    }
                }
                break;
            case 2:
            case 3: // present (queue)
                if (held) {
                    if (m.state_of(*held) == FrameState::Acquired) {
                        const FenceSlot fence = m.take_fence();
                        m.fence_of[*held] = fence;
                        if (next(2) == 0) {
                            m.signalled.insert(fence); // GPU done at once
                        } else {
                            gpu_busy.push_back(fence);
                        }
                        m.queue(*held, ++id, fence);
                    }
                    held.reset();
                }
                break;
            case 4:
            case 5: { // the display thread
                const std::optional<Candidate> c = m.candidate(t);
                if (c) {
                    m.signalled.insert(c->fence);
                    m.wait_seen(c->fence);
                    REQUIRE(m.present_started(c->seq, t));
                    m.present_returned(c->seq, next(8) == 0 ? 5 : 0, t + 50'000);
                }
                break;
            }
            case 6: // the display dies, or comes back after a second and a half or more
                if (display_alive ? next(20) == 0 : t - died_at > 1500 * kMs && next(4) == 0) {
                    display_alive = !display_alive;
                    died_at = t;
                }
                break;
            case 7: // recreate with oldSwapchain, sometimes leaving an image acquired on the old one
                if (next(10) == 0) {
                    if (old_gen) {
                        m.remove_swapchain(*old_gen);
                        old_held.reset();
                    }
                    old_gen = gen;
                    old_held = held;
                    held.reset();
                    gen = m.add_swapchain();
                }
                break;
            default:
                break;
            }
        }
        // The walk reached every way of acquiring.
        CHECK(by_via[static_cast<int>(Via::Free)] > 0);
        CHECK(by_via[static_cast<int>(Via::DroppedQueued)] > 0);
        CHECK(by_via[static_cast<int>(Via::Reserved)] > 0);
        CHECK(by_via[static_cast<int>(Via::Presented)] > 0);
        CHECK(m.counters().failed > 0);
    }
}

TEST_CASE("credit_requires_qpc_after_return")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 7);
    m.present(a.seq, 10 * kMs, 12 * kMs);

    m.vblank(11 * kMs); // read after the return, but the refresh came during the call: it showed the old frame
    CHECK(m.state(a.buffer) == BufferState::Presented);
    CHECK(m.state_of(a.seq) == FrameState::Presented);
    CHECK(m.completed(g) == 0);
    CHECK(m.model.last_refresh_ns() == 11 * kMs);
    CHECK(m.model.last_credited_ns() == 0);
    m.vblank(12 * kMs); // at the return time itself: not later, not credited
    CHECK(m.state(a.buffer) == BufferState::Presented);

    m.vblank(22 * kMs);
    CHECK(m.state(a.buffer) == BufferState::OnScreen);
    CHECK(m.state_of(a.seq) == FrameState::OnScreen);
    CHECK(m.completed(g) == 7);
    CHECK(m.model.last_credited_ns() == 22 * kMs);
}

TEST_CASE("backwards_counter_is_link_drop_not_refresh")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.present(a.seq, 4 * kMs, 5 * kMs);
    m.model.drain_refreshes();
    const FrameModel::Counters before = m.counters();

    m.on_counter(3, 20 * kMs); // was 1000
    FrameModel::Counters c = m.counters();
    CHECK(c.link_drops == before.link_drops + 1);
    CHECK(c.refreshes == before.refreshes);
    CHECK(m.model.last_refresh_ns() == 0);
    CHECK(m.state(a.buffer) == BufferState::Presented);
    CHECK(m.model.drain_refreshes().empty());
    CHECK(m.logged("link drop"));

    m.on_counter(4, 31 * kMs); // the next increment from the new value is a refresh
    c = m.counters();
    CHECK(c.refreshes == before.refreshes + 1);
    CHECK(m.model.last_refresh_ns() == 31 * kMs);
    CHECK(m.state(a.buffer) == BufferState::OnScreen);
    CHECK(m.model.drain_refreshes() == std::vector<int64_t>{31 * kMs});
}

TEST_CASE("nonzero_present_reserves_and_waits_one_increment")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.present(a.seq, 4 * kMs, 5 * kMs);
    m.vblank(kRefresh); // a on screen
    const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
    m.present(b.seq, kRefresh + 4 * kMs, kRefresh + 5 * kMs, 5);

    CHECK(m.state(b.buffer) == BufferState::Reserved);
    CHECK(m.state(a.buffer) == BufferState::OnScreen); // what is on screen stays on screen
    CHECK(m.end_of(b.seq) == FrameEnd::Failed);
    CHECK(m.completed(g) == 2);
    CHECK(m.counters().failed == 1);
    CHECK(m.counters().reserved == 1);

    const Acquired c = m.frame(g, kRefresh + 6 * kMs, 3);
    CHECK_FALSE(m.candidate(kRefresh + 7 * kMs).has_value());
    CHECK_FALSE(m.present_started(c.seq, kRefresh + 7 * kMs));
    m.tick(kRefresh + 8 * kMs);
    CHECK_FALSE(m.candidate(kRefresh + 9 * kMs).has_value());
    m.vblank(kRefresh + 4 * kMs + 500'000); // read now, but it came during b's call: same refresh interval
    CHECK_FALSE(m.candidate(kRefresh + 9 * kMs).has_value());

    m.vblank(2 * kRefresh);
    const std::optional<Candidate> next = m.candidate(2 * kRefresh + 1 * kMs);
    REQUIRE(next.has_value());
    CHECK(next->seq == c.seq);
    CHECK(next->buffer == c.buffer);
    CHECK(next->fence == m.fence_of.at(c.seq));

    m.present(c.seq, 2 * kRefresh + 1 * kMs, 2 * kRefresh + 2 * kMs);
    m.vblank(3 * kRefresh); // the next credit frees the reserved one
    CHECK(m.state(b.buffer) == BufferState::Idle);
    CHECK(m.state(a.buffer) == BufferState::Idle);
    CHECK(m.state(c.buffer) == BufferState::OnScreen);
}

TEST_CASE("second_reserved_frees_older_after_a_refresh")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.present(a.seq, 4 * kMs, 5 * kMs, 5);
    m.vblank(kRefresh);
    const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
    const int64_t b_returned = kRefresh + 5 * kMs;
    m.present(b.seq, kRefresh + 4 * kMs, b_returned, 5);

    REQUIRE(a.buffer != b.buffer);
    CHECK(m.state(a.buffer) == BufferState::Reserved);
    CHECK(m.state(b.buffer) == BufferState::Reserved);
    CHECK(m.counters().reserved == 2);
    CHECK(m.logged("two non-zero Presents in a row"));

    m.vblank(b_returned - 1 * kMs); // came during b's call: not yet
    CHECK(m.state(a.buffer) == BufferState::Reserved);
    CHECK(m.state(b.buffer) == BufferState::Reserved);

    m.vblank(b_returned + 6 * kMs);
    CHECK(m.state(a.buffer) == BufferState::Idle);
    CHECK(m.state(b.buffer) == BufferState::Reserved);
}

TEST_CASE("three_uncredited_increments_make_unconfirmed")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 4);
    m.signalled.insert(m.fence_of.at(a.seq));
    m.wait_seen(m.fence_of.at(a.seq));
    REQUIRE(m.present_started(a.seq, 4 * kMs)); // its return time is never written

    m.vblank(kRefresh);
    m.vblank(2 * kRefresh);
    CHECK(m.state(a.buffer) == BufferState::Presented);
    CHECK(m.state_of(a.seq) == FrameState::Presented);
    CHECK(m.completed(g) == 0);
    const Acquired b = m.frame(g, 2 * kRefresh + 1 * kMs, 5);
    CHECK_FALSE(m.candidate(2 * kRefresh + 2 * kMs).has_value());

    m.vblank(3 * kRefresh);
    CHECK(m.state(a.buffer) == BufferState::Reserved);
    CHECK(m.end_of(a.seq) == FrameEnd::Unconfirmed);
    CHECK(m.completed(g) == 4);
    CHECK(m.counters().unconfirmed == 1);
    CHECK(m.counters().reserved == 1);
    CHECK(m.logged("not credited"));

    const std::optional<Candidate> next = m.candidate(3 * kRefresh + 1 * kMs); // the next Present may go ahead
    REQUIRE(next.has_value());
    CHECK(next->seq == b.seq);
}

TEST_CASE("frame model: three increments seen in one read count as three")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 4);
    m.signalled.insert(m.fence_of.at(a.seq));
    m.wait_seen(m.fence_of.at(a.seq));
    REQUIRE(m.present_started(a.seq, 4 * kMs));
    m.counter += 2;
    m.vblank(3 * kRefresh);
    CHECK(m.state(a.buffer) == BufferState::Reserved);
    CHECK(m.end_of(a.seq) == FrameEnd::Unconfirmed);
}

TEST_CASE("frame model: acquire takes the oldest free back-buffer")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.present(a.seq, 2 * kMs, 3 * kMs);
    m.vblank(kRefresh);
    const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
    m.present(b.seq, kRefresh + 2 * kMs, kRefresh + 3 * kMs);
    m.vblank(2 * kRefresh); // a's back-buffer is free again, the third one has never been used
    const Acquired c = m.acq(g, 2 * kRefresh + 1 * kMs);
    CHECK(c.buffer != a.buffer);
    CHECK(c.buffer != b.buffer);
    const FenceSlot fc = m.take_fence();
    m.fence_of[c.seq] = fc;
    m.queue(c.seq, 3, fc);
    m.present(c.seq, 2 * kRefresh + 2 * kMs, 2 * kRefresh + 3 * kMs);
    m.vblank(3 * kRefresh);
    CHECK(m.acq(g, 3 * kRefresh + 1 * kMs).buffer == a.buffer); // free longer than b's
}

TEST_CASE("unconfirmed_without_live_frame_completes_no_id")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 4);
    m.signalled.insert(m.fence_of.at(a.seq));
    m.wait_seen(m.fence_of.at(a.seq));
    REQUIRE(m.present_started(a.seq, 4 * kMs)); // a left-behind thread: no return time

    m.device_destroyed();
    CHECK(m.end_of(a.seq) == FrameEnd::Destroyed);
    CHECK(m.state(a.buffer) == BufferState::Presented);

    m.vblank(kRefresh);
    m.vblank(2 * kRefresh);
    m.vblank(3 * kRefresh);
    CHECK(m.state(a.buffer) == BufferState::Reserved);
    CHECK(m.completed(g) == 0);
    CHECK(m.model.ids_completed() == 0);
    CHECK(m.counters().unconfirmed == 0);
    CHECK(m.counters().reserved == 1);
}

TEST_CASE("ids_complete_in_order_and_once")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired f1 = m.frame(g, 1 * kMs, 10);
    const Acquired f2 = m.frame(g, 2 * kMs, 11);
    m.present(f2.seq, 4 * kMs, 5 * kMs); // f1 retired: its id completes now
    CHECK(m.end_of(f1.seq) == FrameEnd::Retired);
    CHECK(m.completed(g) == 10);
    CHECK(m.model.ids_completed() == 1);

    m.vblank(kRefresh);
    CHECK(m.completed(g) == 11);
    CHECK(m.model.ids_completed() == 2);

    const Acquired f3 = m.frame(g, kRefresh + 1 * kMs, 12);
    const int64_t returned = kRefresh + 5 * kMs;
    m.present(f3.seq, kRefresh + 4 * kMs, returned);
    const Acquired f4 = m.frame(g, kRefresh + 6 * kMs, 13);
    CHECK(m.completed(g) == 11);

    // No refresh for a while: after the bound, acquire drops f4, completing 13 and so everything before it.
    CHECK_FALSE(m.acquire(g, returned + 599 * kMs).has_value());
    const Acquired f5 = m.acq(g, returned + 600 * kMs);
    CHECK(f5.via == Via::DroppedQueued);
    CHECK(f5.buffer == f4.buffer);
    CHECK(m.end_of(f4.seq) == FrameEnd::Dropped);
    CHECK(m.completed(g) == 13);
    CHECK(m.model.ids_completed() == 3);

    m.vblank(returned + 605 * kMs); // f3 goes on screen; 12 < 13, so the record stays at 13
    CHECK(m.state_of(f3.seq) == FrameState::OnScreen);
    CHECK(m.completed(g) == 13);
    CHECK(m.model.ids_completed() == 4);
    CHECK(m.end_of(f2.seq) == FrameEnd::Replaced);
    CHECK(m.model.ids_completed() == 4); // replaced: completed once already, when it went on screen
}

TEST_CASE("ids_of_destroyed_swapchain_not_written")
{
    M m;
    m.start(0);
    const SwapchainGen g1 = m.add_swapchain();
    const Acquired f1 = m.frame(g1, 1 * kMs, 1);
    m.present(f1.seq, 4 * kMs, 5 * kMs);
    const Acquired f2 = m.frame(g1, 6 * kMs, 2);

    const SwapchainGen g2 = m.add_swapchain(); // recreate with oldSwapchain
    REQUIRE(g2 != g1);
    m.remove_swapchain(g1);
    CHECK(m.end_of(f2.seq) == FrameEnd::Dropped); // queued: dropped
    CHECK(m.state(f2.buffer) == BufferState::Idle);
    CHECK(m.state_of(f1.seq) == FrameState::Presented); // presented: unlinked, its state stays
    CHECK(m.completed(g1) == 0);
    CHECK(m.completed(g2) == 0);
    CHECK(m.pool.state(m.fence_of.at(f2.seq)) == FenceState::LeftOver);

    m.vblank(kRefresh); // f1 goes on screen, but its swapchain is gone
    CHECK(m.state(f1.buffer) == BufferState::OnScreen);
    CHECK(m.completed(g1) == 0);
    CHECK(m.completed(g2) == 0);

    const Acquired f3 = m.frame(g2, kRefresh + 1 * kMs, 1);
    CHECK(f3.buffer != f1.buffer);
    m.present(f3.seq, kRefresh + 4 * kMs, kRefresh + 5 * kMs);
    m.vblank(2 * kRefresh);
    CHECK(m.completed(g2) == 1);
    CHECK(m.completed(g1) == 0);
    CHECK(m.end_of(f1.seq) == FrameEnd::Replaced);
}

TEST_CASE("acquire_bound_uses_later_of_credit_and_return_time")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.present(a.seq, 4 * kMs, 5 * kMs);
    m.vblank(kRefresh); // a on screen, the last credited refresh
    REQUIRE(m.model.last_credited_ns() == kRefresh);

    SECTION("after a gap with no app session: the outstanding Present's return time")
    {
        int64_t t = kRefresh;
        for (int i = 0; i < 450; ++i) { // 5 s of refreshes with no frames
            t += kRefresh;
            m.vblank(t);
        }
        CHECK(m.model.last_credited_ns() == kRefresh);
        const Acquired b = m.frame(g, t + 1 * kMs, 2);
        const int64_t returned = t + 5 * kMs;
        m.present(b.seq, t + 4 * kMs, returned);
        const Acquired c = m.frame(g, t + 6 * kMs, 3);
        (void)c;
        CHECK(m.bound() == returned + 600 * kMs);
        CHECK_FALSE(m.acquire(g, returned + 1 * kMs).has_value()); // not expired at once
        CHECK_FALSE(m.acquire(g, returned + 599 * kMs).has_value());
        CHECK(m.acq(g, returned + 600 * kMs).via == Via::DroppedQueued);
    }

    SECTION("the last credited refresh when it is later: a thread restart sets it to now")
    {
        const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
        const int64_t returned = kRefresh + 5 * kMs;
        m.present(b.seq, kRefresh + 4 * kMs, returned);
        const Acquired c = m.frame(g, kRefresh + 6 * kMs, 3);
        (void)c;
        CHECK(m.bound() == returned + 600 * kMs);
        const int64_t restart = returned + 100 * kMs;
        m.thread_started(restart);
        CHECK(m.model.last_credited_ns() == restart);
        CHECK(m.bound() == restart + 600 * kMs);
        CHECK_FALSE(m.acquire(g, returned + 650 * kMs).has_value());
        CHECK(m.acq(g, restart + 600 * kMs).via == Via::DroppedQueued);
    }

    SECTION("with no Present outstanding, refreshes do not move it")
    {
        const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
        m.present(b.seq, kRefresh + 4 * kMs, kRefresh + 5 * kMs, 5); // reserved
        const Acquired c = m.frame(g, kRefresh + 6 * kMs, 3);
        (void)c;
        int64_t t = kRefresh;
        for (int i = 0; i < 50; ++i) { // refreshes go on; nothing is Presented
            t += kRefresh;
            m.vblank(t);
        }
        CHECK(m.bound() == kRefresh + 600 * kMs);
        CHECK_FALSE(m.acquire(g, kRefresh + 599 * kMs).has_value());
        CHECK(m.acq(g, kRefresh + 600 * kMs).via == Via::DroppedQueued);
    }

    SECTION("a Present still in its call: its start time, or a refresh seen since")
    {
        const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
        const int64_t start = kRefresh + 4 * kMs;
        m.signalled.insert(m.fence_of.at(b.seq));
        m.wait_seen(m.fence_of.at(b.seq));
        REQUIRE(m.present_started(b.seq, start));
        CHECK(m.bound() == start + 600 * kMs);
        m.vblank(2 * kRefresh); // no return time: not credited, but the display is alive
        CHECK(m.bound() == 2 * kRefresh + 600 * kMs);
        m.present_returned(b.seq, 0, 2 * kRefresh + 1 * kMs);
        CHECK(m.bound() == 2 * kRefresh + 601 * kMs);
    }
}

TEST_CASE("acquire_after_bound_order")
{
    SECTION("queued, then reserved, then presented only when flips have stopped")
    {
        FrameModelConfig config;
        config.stopped_ns = 1000 * kMs; // longer than the bound, so the two can be told apart
        M m(config);
        m.start(0);
        const SwapchainGen g1 = m.add_swapchain();
        const Acquired a = m.frame(g1, 1 * kMs, 1);
        m.present(a.seq, 4 * kMs, 5 * kMs, 5); // reserved
        const int64_t v = kRefresh;
        m.vblank(v);
        const Acquired b = m.frame(g1, v + 1 * kMs, 2);
        const int64_t returned = v + 5 * kMs;
        m.present(b.seq, v + 4 * kMs, returned); // presented; the display dies after this
        const Acquired c = m.frame(g1, v + 6 * kMs, 3);
        // Two more swapchains, as if recreated, each holding the image it acquires.
        const SwapchainGen g2 = m.add_swapchain();
        const SwapchainGen g3 = m.add_swapchain();
        REQUIRE(m.bound() == returned + 600 * kMs);
        CHECK_FALSE(m.acquire(g1, returned + 599 * kMs).has_value());

        const int64_t t1 = returned + 600 * kMs;
        const Acquired first = m.acq(g1, t1);
        CHECK(first.via == Via::DroppedQueued);
        CHECK(first.buffer == c.buffer);
        CHECK(m.end_of(c.seq) == FrameEnd::Dropped);

        const Acquired second = m.acq(g2, t1);
        CHECK(second.via == Via::Reserved);
        CHECK(second.buffer == a.buffer);
        CHECK(m.logged("reserved"));

        m.tick(t1);
        CHECK_FALSE(m.model.flips_stopped());
        CHECK_FALSE(m.acquire(g3, t1).has_value()); // the presented one only while flips are stopped
        CHECK(m.state(b.buffer) == BufferState::Presented);

        const int64_t t2 = v + 1000 * kMs;
        m.tick(t2);
        CHECK(m.model.flips_stopped());
        const Acquired third = m.acq(g3, t2);
        CHECK(third.via == Via::Presented);
        CHECK(third.buffer == b.buffer);
        CHECK(m.end_of(b.seq) == FrameEnd::Unconfirmed);
        CHECK(m.completed(g1) == 3); // dropped 3, then unconfirmed 2: the record never goes back
        CHECK(m.counters().unconfirmed == 1);
    }

    SECTION("the on-screen back-buffer never")
    {
        M m;
        m.start(0);
        const SwapchainGen g1 = m.add_swapchain();
        const Acquired a = m.frame(g1, 1 * kMs, 1);
        m.present(a.seq, 4 * kMs, 5 * kMs);
        m.vblank(kRefresh); // a on screen
        const Acquired b = m.frame(g1, kRefresh + 1 * kMs, 2);
        const int64_t returned = kRefresh + 5 * kMs;
        m.present(b.seq, kRefresh + 4 * kMs, returned);
        const Acquired c = m.frame(g1, kRefresh + 6 * kMs, 3);
        const SwapchainGen g2 = m.add_swapchain();
        const SwapchainGen g3 = m.add_swapchain();

        const int64_t t = returned + 600 * kMs; // the display has been dead since before the return
        CHECK(m.acq(g1, t).buffer == c.buffer);
        CHECK_FALSE(m.model.flips_stopped()); // the display thread has not ticked since
        const Acquired p = m.acq(g2, t);
        CHECK(p.via == Via::Presented);
        CHECK(p.buffer == b.buffer);
        CHECK_FALSE(m.acquire(g3, t + 10'000 * kMs).has_value()); // only the on-screen one is left
        CHECK(m.state(a.buffer) == BufferState::OnScreen);

        // Flips have stopped now, and the Present acquire took was the one of the stopped period.
        CHECK(m.model.flips_stopped());
        const FenceSlot fp = m.take_fence();
        m.fence_of[p.seq] = fp;
        m.queue(p.seq, 4, fp);
        CHECK(m.end_of(p.seq) == FrameEnd::Dropped);
        CHECK_FALSE(m.candidate(t + 1 * kMs).has_value());
        CHECK(m.counters().presents == 2);
        m.vblank(t + 2 * kMs); // the display is back
        CHECK_FALSE(m.model.flips_stopped());
    }
}

TEST_CASE("flips_stopped_drops_queued_keeps_one_present")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.present(a.seq, 4 * kMs, 5 * kMs);
    const int64_t last = kRefresh;
    m.vblank(last); // a on screen; the display dies after this refresh

    SECTION("with a Present outstanding, it stays the only one and queued frames are dropped")
    {
        const Acquired b = m.frame(g, last + 1 * kMs, 2);
        m.present(b.seq, last + 4 * kMs, last + 5 * kMs);
        const Acquired c = m.frame(g, last + 6 * kMs, 3);
        m.tick(last + 599 * kMs);
        CHECK_FALSE(m.model.flips_stopped());
        CHECK(m.state_of(c.seq) == FrameState::Queued);

        m.tick(last + 600 * kMs);
        CHECK(m.model.flips_stopped());
        CHECK(m.logged("flips stopped"));
        CHECK(m.end_of(c.seq) == FrameEnd::Dropped);
        CHECK(m.state(c.buffer) == BufferState::Idle);
        CHECK(m.completed(g) == 3);

        const Acquired d = m.frame(g, last + 601 * kMs, 4); // dropped as it arrives
        CHECK(d.via == Via::Free);
        CHECK(m.end_of(d.seq) == FrameEnd::Dropped);
        CHECK(m.completed(g) == 4);
        CHECK_FALSE(m.candidate(last + 602 * kMs).has_value());
        CHECK(m.state(b.buffer) == BufferState::Presented); // states kept
        CHECK(m.state(a.buffer) == BufferState::OnScreen);
        CHECK(m.counters().presents == 2);
        CHECK(m.counters().dropped == 2);

        m.vblank(last + 2000 * kMs); // the display is back
        CHECK_FALSE(m.model.flips_stopped());
        CHECK(m.logged("flips resumed"));
        CHECK(m.state(b.buffer) == BufferState::OnScreen);
        CHECK(m.state(a.buffer) == BufferState::Idle);
    }

    SECTION("with none outstanding, the newest frame is Presented once")
    {
        const Acquired b = m.frame(g, last + 1 * kMs, 2);
        const Acquired c = m.frame(g, last + 2 * kMs, 3);
        m.tick(last + 600 * kMs);
        CHECK(m.model.flips_stopped());
        CHECK(m.end_of(b.seq) == FrameEnd::Dropped);
        const std::optional<Candidate> once = m.candidate(last + 601 * kMs);
        REQUIRE(once.has_value());
        CHECK(once->seq == c.seq);
        m.present(c.seq, last + 601 * kMs, last + 602 * kMs);
        CHECK(m.counters().presents == 2);

        const Acquired d = m.frame(g, last + 603 * kMs, 4);
        CHECK(m.end_of(d.seq) == FrameEnd::Dropped);
        CHECK_FALSE(m.candidate(last + 604 * kMs).has_value());
    }

    SECTION("a frame queued later replaces the one waiting for the single Present")
    {
        const Acquired b = m.frame(g, last + 1 * kMs, 2);
        m.tick(last + 600 * kMs);
        CHECK(m.state_of(b.seq) == FrameState::Queued);
        const Acquired c = m.frame(g, last + 601 * kMs, 3);
        CHECK(m.end_of(b.seq) == FrameEnd::Dropped);
        const std::optional<Candidate> once = m.candidate(last + 602 * kMs);
        REQUIRE(once.has_value());
        CHECK(once->seq == c.seq);
    }
}

TEST_CASE("recreate_shares_buffer_state")
{
    M m;
    m.start(0);
    const SwapchainGen g1 = m.add_swapchain();
    const Acquired a = m.frame(g1, 1 * kMs, 1);
    m.present(a.seq, 4 * kMs, 5 * kMs);
    m.vblank(kRefresh); // a on screen
    const SwapchainGen g2 = m.add_swapchain();

    const Acquired b = m.frame(g2, kRefresh + 1 * kMs, 1);
    CHECK(b.buffer != a.buffer);
    m.present(b.seq, kRefresh + 4 * kMs, kRefresh + 5 * kMs);
    const Acquired c = m.frame(g2, kRefresh + 6 * kMs, 2);
    CHECK(c.buffer != a.buffer);
    CHECK(c.buffer != b.buffer);
    CHECK_FALSE(m.acquire(g2, kRefresh + 7 * kMs).has_value()); // the old swapchain's on-screen image is not free

    m.remove_swapchain(g1);
    CHECK(m.state(a.buffer) == BufferState::OnScreen);
    CHECK_FALSE(m.acquire(g2, kRefresh + 8 * kMs).has_value());

    m.vblank(2 * kRefresh); // b credited: a's back-buffer is free now
    CHECK(m.end_of(a.seq) == FrameEnd::Replaced);
    CHECK(m.acq(g2, 2 * kRefresh + 1 * kMs).buffer == a.buffer);
    CHECK(m.completed(g2) == 1);
}

TEST_CASE("device_destroyed_frees_frame_held_keeps_nvapi_states")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.present(a.seq, 4 * kMs, 5 * kMs);
    m.vblank(kRefresh); // a on screen
    const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
    m.present(b.seq, kRefresh + 4 * kMs, kRefresh + 5 * kMs); // presented
    const Acquired c = m.acq(g, kRefresh + 6 * kMs);          // acquired, held

    m.remove_swapchain(g);
    m.device_destroyed();
    CHECK(m.end_of(a.seq) == FrameEnd::Destroyed);
    CHECK(m.end_of(b.seq) == FrameEnd::Destroyed);
    CHECK(m.end_of(c.seq).has_value()); // ended with its swapchain
    CHECK(m.state(a.buffer) == BufferState::OnScreen);
    CHECK(m.state(b.buffer) == BufferState::Presented);
    CHECK(m.state(c.buffer) == BufferState::Idle);
    CHECK(m.model.ids_completed() == 1); // only a's, when it went on screen

    // The retry: a new device with its own pool.
    FencePool next_pool;
    m.set_pool(&next_pool);
    const SwapchainGen g2 = m.add_swapchain();
    const Acquired d = m.acq(g2, kRefresh + 7 * kMs);
    CHECK(d.via == Via::Free);
    CHECK(d.buffer == c.buffer);

    m.vblank(2 * kRefresh); // b's Present is credited, with no frame left to complete
    CHECK(m.state(b.buffer) == BufferState::OnScreen);
    CHECK(m.state(a.buffer) == BufferState::Idle);
    CHECK(m.model.ids_completed() == 1);
    CHECK(m.completed(g2) == 0);
}

TEST_CASE("frame model: thread start sets both clocks, takes the first read as the baseline")
{
    M m;
    m.thread_started(5000 * kMs);
    CHECK(m.model.last_refresh_ns() == 5000 * kMs);
    CHECK(m.model.last_credited_ns() == 5000 * kMs);
    m.on_counter(77, 5001 * kMs); // baseline: no refresh
    CHECK(m.counters().refreshes == 0);
    CHECK(m.model.last_refresh_ns() == 5000 * kMs);
    m.tick(5599 * kMs);
    CHECK_FALSE(m.model.flips_stopped());
    m.on_counter(78, 5010 * kMs);
    CHECK(m.counters().refreshes == 1);
    CHECK(m.model.last_refresh_ns() == 5010 * kMs);
    m.tick(5609 * kMs);
    CHECK_FALSE(m.model.flips_stopped());
    m.tick(5610 * kMs);
    CHECK(m.model.flips_stopped());

    m.thread_started(9000 * kMs); // a restart is not mistaken for stopped flips
    CHECK_FALSE(m.model.flips_stopped());
    m.on_counter(500, 9001 * kMs); // a jump across the restart is a new baseline
    CHECK(m.counters().refreshes == 1);
}

TEST_CASE("frame model: missed is the flip gap minus one, gaps over 1 s are idle")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    uint64_t id = 0;
    int64_t t = 0;
    auto show = [&](int refreshes_later) { // one frame, credited `refreshes_later` refreshes on
        const Acquired f = m.frame(g, t + 1 * kMs, ++id);
        m.present(f.seq, t + 2 * kMs, t + 3 * kMs);
        for (int i = 0; i < refreshes_later; ++i) {
            t += kRefresh;
            m.vblank(t);
        }
        REQUIRE(m.state_of(f.seq) == FrameState::OnScreen);
    };
    show(1);
    show(1);
    CHECK(m.counters().missed == 0);
    // A frame whose Present comes late: the refresh in between shows the old frame again.
    t += kRefresh;
    m.vblank(t);
    show(1);
    CHECK(m.counters().missed == 1);
    // Over a second with no frame (no app session) is idle, not missed.
    for (int i = 0; i < 100; ++i) {
        t += kRefresh;
        m.vblank(t);
    }
    show(1);
    CHECK(m.counters().missed == 1);
    // Refreshes a poll did not see on their own (the counter jumps by 3) count each.
    const Acquired f = m.frame(g, t + 1 * kMs, ++id);
    m.present(f.seq, t + 2 * kMs, t + 3 * kMs);
    m.counter += 2;
    t += 3 * kRefresh;
    m.vblank(t);
    CHECK(m.counters().missed == 3);
    CHECK(m.counters().refreshes == 1 + 1 + 1 + 1 + 100 + 1 + 3);
}

TEST_CASE("frame model: drain_refreshes gives every refresh's QPC time once")
{
    M m;
    m.start(0);
    m.vblank(10 * kMs);
    m.vblank(21 * kMs);
    m.on_counter(5, 25 * kMs); // a link drop is not a refresh
    m.on_counter(6, 32 * kMs);
    CHECK(m.model.drain_refreshes() == std::vector<int64_t>{10 * kMs, 21 * kMs, 32 * kMs});
    CHECK(m.model.drain_refreshes().empty());
}

TEST_CASE("frame model: fences go back to the pool when seen signalled, else are left over")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    const FenceSlot fa = m.fence_of.at(a.seq);
    CHECK(m.pool.state(fa) == FenceState::Attached);
    const Acquired b = m.frame(g, 2 * kMs, 2);
    m.present(b.seq, 3 * kMs, 4 * kMs); // a retired before its fence was seen
    CHECK(m.pool.state(fa) == FenceState::LeftOver);
    m.vblank(kRefresh);
    const Acquired c = m.frame(g, kRefresh + 1 * kMs, 3);
    m.present(c.seq, kRefresh + 2 * kMs, kRefresh + 3 * kMs);
    m.vblank(2 * kRefresh); // b replaced; its fence was seen signalled
    CHECK(m.pool.state(m.fence_of.at(b.seq)) == FenceState::InPool);

    m.wait_seen(fa); // a wait on it (the present path's, on the oldest left-over fence) saw it signalled
    CHECK(m.pool.state(fa) == FenceState::InPool);

    SECTION("the display thread's wait marks the fence")
    {
        const Acquired d = m.frame(g, 2 * kRefresh + 1 * kMs, 4);
        const FenceSlot fd = m.fence_of.at(d.seq);
        m.model.fence_waiting(fd, true);
        m.check();
        const Acquired e = m.frame(g, 2 * kRefresh + 2 * kMs, 5);
        m.present(e.seq, 2 * kRefresh + 3 * kMs, 2 * kRefresh + 4 * kMs); // d retired while waited on
        m.signalled.insert(fd);
        CHECK(m.pool.state(fd) == FenceState::LeftOver);
        CHECK(m.pool.oldest_left_over() != fd);
        m.model.fence_waiting(fd, false);
        m.check();
        CHECK(m.pool.oldest_left_over() == fd);
    }
}

TEST_CASE("frame model: a stale fence_seen does not mark a re-attached fence seen")
{
    M m;
    m.start(0);
    SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    const FenceSlot f = m.fence_of.at(a.seq);
    m.signalled.insert(f); // a's GPU work is done; a status check outside the lock, not marked waited, sees it...
    m.remove_swapchain(g); // ...while a is dropped: its fence is left over, never seen
    REQUIRE(m.pool.state(f) == FenceState::LeftOver);

    g = m.add_swapchain();
    const Acquired b = m.frame(g, 2 * kMs, 2); // take() reclaims the signalled left-over fence; it is reset for b
    REQUIRE(m.fence_of.at(b.seq) == f);
    m.fence_seen(f); // the stale call arrives now: f is b's, unsignalled, and not marked waited
    m.remove_swapchain(g);
    CHECK(m.pool.state(f) == FenceState::LeftOver); // b's fence was never seen signalled
    const std::optional<FenceSlot> next = m.pool.take();
    REQUIRE(next.has_value());
    CHECK(*next != f); // so it is not handed out again unsignalled (invariant 2)

    SECTION("a stale call on the left-over fence does not put it back in the pool either")
    {
        m.fence_seen(f);
        CHECK(m.pool.state(f) == FenceState::LeftOver);
    }
}

TEST_CASE("frame model: present_started refuses what candidate() would not give")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.acq(g, 1 * kMs);
    CHECK_FALSE(m.present_started(a.seq, 2 * kMs)); // acquired, not queued
    CHECK_FALSE(m.present_started(999, 2 * kMs));   // no such frame
    const FenceSlot fa = m.take_fence();
    m.fence_of[a.seq] = fa;
    m.queue(a.seq, 1, fa);
    const Acquired b = m.frame(g, 3 * kMs, 2);
    REQUIRE(m.present_started(a.seq, 4 * kMs)); // the older one may still go (invariant 5's re-check)
    CHECK_FALSE(m.present_started(b.seq, 5 * kMs)); // one Present outstanding at most
    CHECK(m.state_of(b.seq) == FrameState::Queued);  // newer frames are not retired
    CHECK_FALSE(m.candidate(5 * kMs).has_value());
    CHECK(m.counters().presents == 1);
}

TEST_CASE("frame model: check_invariants names the invariant it finds broken")
{
    // A model can only be broken from outside through its pool: a fence attached to a frame and then taken back.
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.pool.seen_signalled(m.fence_of.at(a.seq));
    m.pool.frame_ended(m.fence_of.at(a.seq)); // the pool now thinks the fence is free while a still holds it
    try {
        m.model.check_invariants();
        FAIL("no invariant reported");
    } catch (const std::logic_error& e) {
        CHECK(std::string(e.what()).rfind("invariant 2: ", 0) == 0);
    }
}

TEST_CASE("frame model: release ends an acquired frame and frees its back-buffer")
{
    // vkReleaseSwapchainImagesEXT: an image acquired and never presented goes back. Its frame ends dropped (it has no
    // present id yet, so none completes) and its back-buffer is free for the next acquire.
    M m;
    const SwapchainGen gen = m.add_swapchain();
    const Acquired a = *m.acquire(gen, 0);
    const Acquired b = *m.acquire(gen, 0);
    const Acquired c = *m.acquire(gen, 0);
    CHECK_FALSE(m.acquire(gen, 0).has_value());
    const uint64_t ids = m.model.ids_completed();

    m.model.release(b.seq);
    m.check();
    CHECK_FALSE(m.model.frame_state(b.seq).has_value());
    CHECK(m.model.frame_end(b.seq) == FrameEnd::Dropped);
    CHECK(m.model.ids_completed() == ids);
    const std::optional<Acquired> again = m.acquire(gen, 0);
    REQUIRE(again.has_value());
    CHECK(again->buffer == b.buffer);

    // A frame that is not acquired is left as it is.
    m.queue(a.seq, 1, nvdm::kNoFence);
    m.model.release(a.seq);
    m.check();
    CHECK(m.model.frame_state(a.seq) == FrameState::Queued);
    m.model.release(9999);
    m.check();
    CHECK(m.model.frame_state(c.seq) == FrameState::Acquired);
}

// --- Present timing (design 6.11) ---------------------------------------------------------------------------------

TEST_CASE("frame model: a shown frame is reported once, with its fence, Present and flip times")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.frame(g, 1 * kMs, 1);
    REQUIRE(m.model.frame_times(a.seq).has_value());
    CHECK(m.model.frame_times(a.seq)->queue_done_ns == 0); // its fence has not been seen yet

    // The display thread sees its fence signalled and starts its Present in one lock hold: both stages at that time.
    m.present(a.seq, 4 * kMs, 4 * kMs + 50'000);
    nvdm::FrameTimes so_far = *m.model.frame_times(a.seq);
    CHECK(so_far.queue_done_ns == 4 * kMs);
    CHECK(so_far.dequeued_ns == 4 * kMs);
    CHECK(so_far.on_screen_ns == 0);
    CHECK(m.reports.empty());

    // The refresh that credits it: its flip time, the stats' QPC time of the read.
    m.vblank(kRefresh);
    REQUIRE(m.reports.size() == 1);
    CHECK(m.reports[0].gen == g);
    CHECK(m.reports[0].seq == a.seq);
    CHECK(m.reports[0].times.queue_done_ns == 4 * kMs);
    CHECK(m.reports[0].times.dequeued_ns == 4 * kMs);
    CHECK(m.reports[0].times.on_screen_ns == kRefresh);

    // Replaced by the next frame shown: not reported again; the next one is, at its own flip.
    const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
    m.present(b.seq, kRefresh + 3 * kMs, kRefresh + 3 * kMs + 50'000);
    m.vblank(2 * kRefresh);
    CHECK(m.end_of(a.seq) == FrameEnd::Replaced);
    REQUIRE(m.reports.size() == 2);
    CHECK(m.reports[1].seq == b.seq);
    CHECK(m.reports[1].times.on_screen_ns == 2 * kRefresh);
    CHECK_FALSE(m.model.frame_times(a.seq).has_value()); // ended
}

TEST_CASE("frame model: a frame with no fence is done when it is queued")
{
    // The fence fallback: the present waited for its queue to go idle, so its GPU work was done when it was queued.
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.acq(g, 1 * kMs);
    m.queue(a.seq, 1, nvdm::kNoFence, 2 * kMs);
    CHECK(m.model.frame_times(a.seq)->queue_done_ns == 2 * kMs);
    REQUIRE(m.present_started(a.seq, 3 * kMs));
    m.present_returned(a.seq, 0, 3 * kMs + 50'000);
    m.vblank(kRefresh);
    REQUIRE(m.reports.size() == 1);
    CHECK(m.reports[0].times.queue_done_ns == 2 * kMs);
    CHECK(m.reports[0].times.dequeued_ns == 3 * kMs);
    CHECK(m.reports[0].times.on_screen_ns == kRefresh);
}

TEST_CASE("frame model: frames that end unshown are reported once, with the stages they reached")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();

    // Retired: queued, and a newer frame was Presented instead. It reached no stage.
    const Acquired a = m.frame(g, 1 * kMs, 1);
    const Acquired b = m.frame(g, 2 * kMs, 2);
    m.present(b.seq, 3 * kMs, 3 * kMs + 50'000);
    CHECK(m.end_of(a.seq) == FrameEnd::Retired);
    REQUIRE(m.reports.size() == 1);
    CHECK(m.reports[0].seq == a.seq);
    CHECK(m.reports[0].times.queue_done_ns == 0);
    CHECK(m.reports[0].times.dequeued_ns == 0);
    CHECK(m.reports[0].times.on_screen_ns == 0);
    m.vblank(kRefresh); // b shown
    REQUIRE(m.reports.size() == 2);

    // Failed: NVAPI refused its Present. Fence seen and dequeued, never on screen.
    const Acquired c = m.frame(g, kRefresh + 1 * kMs, 3);
    m.present(c.seq, kRefresh + 3 * kMs, kRefresh + 3 * kMs + 50'000, 5);
    CHECK(m.end_of(c.seq) == FrameEnd::Failed);
    REQUIRE(m.reports.size() == 3);
    CHECK(m.reports[2].seq == c.seq);
    CHECK(m.reports[2].times.queue_done_ns == kRefresh + 3 * kMs);
    CHECK(m.reports[2].times.dequeued_ns == kRefresh + 3 * kMs);
    CHECK(m.reports[2].times.on_screen_ns == 0);

    // Released before it was queued: never presented, so not reported.
    const Acquired d = m.acq(g, kRefresh + 4 * kMs);
    m.model.release(d.seq);
    m.check();
    CHECK(m.reports.size() == 3);

    // Dropped with its swapchain: reported under that swapchain's generation.
    const Acquired e = m.frame(g, kRefresh + 5 * kMs, 4);
    m.remove_swapchain(g);
    CHECK(m.end_of(e.seq) == FrameEnd::Dropped);
    REQUIRE(m.reports.size() == 4);
    CHECK(m.reports[3].gen == g);
    CHECK(m.reports[3].seq == e.seq);

    // Each frame once.
    std::set<FrameSeq> seqs;
    for (const nvdm::FrameReport& r : m.reports) {
        CHECK(seqs.insert(r.seq).second);
    }
    CHECK_FALSE(m.model.frame_times(9999).has_value());
}

TEST_CASE("frame model: queue says whether it took the frame")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired a = m.acq(g, 1 * kMs);
    CHECK(m.model.queue(a.seq, 1, nvdm::kNoFence, 2 * kMs));
    m.check();
    CHECK_FALSE(m.model.queue(a.seq, 2, nvdm::kNoFence, 3 * kMs)); // queued already
    CHECK_FALSE(m.model.queue(9999, 3, nvdm::kNoFence, 3 * kMs));  // no such frame
    m.check();
    CHECK(m.reports.empty());
}

// --- Present at a time (design 6.11) --------------------------------------------------------------------------------
// Flips at k * kRefresh. A frame held for `not_before` may be Presented once a flip F with F + kRefresh >= not_before
// has been seen: its Present then lands on F + kRefresh, the first flip not before the limit.

TEST_CASE("frame model: a frame held for its target goes after the flip before it, not sooner")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    const Acquired z = m.frame(g, 1 * kMs, 9);
    m.present(z.seq, 2 * kMs, 2 * kMs + 50'000);
    m.vblank(1 * kRefresh); // z shown at flip 1

    const Acquired a = m.frame(g, kRefresh + 1 * kMs, 1);
    m.hold(a.seq, 4 * kRefresh - 1 * kMs); // its target: flip 4, within 1 ms
    CHECK(m.counters().held == 1);
    CHECK_FALSE(m.candidate(kRefresh + 2 * kMs).has_value());
    m.signalled.insert(m.fence_of.at(a.seq));
    m.wait_seen(m.fence_of.at(a.seq));
    CHECK_FALSE(m.present_started(a.seq, kRefresh + 2 * kMs)); // the re-check refuses it too
    CHECK(m.state_of(a.seq) == FrameState::Queued);

    m.vblank(2 * kRefresh);
    CHECK_FALSE(m.candidate(2 * kRefresh + 1 * kMs).has_value());
    m.vblank(3 * kRefresh);
    const std::optional<Candidate> c = m.candidate(3 * kRefresh + 1 * kMs);
    REQUIRE(c.has_value());
    CHECK(c->seq == a.seq);
    REQUIRE(m.present_started(a.seq, 3 * kRefresh + 1 * kMs));
    m.present_returned(a.seq, 0, 3 * kRefresh + 1 * kMs + 50'000);
    m.vblank(4 * kRefresh);
    REQUIRE(m.reports.size() == 2);
    CHECK(m.reports[1].times.on_screen_ns == 4 * kRefresh); // the real flip
    CHECK(m.counters().held == 1);
    CHECK(m.counters().missed == 2);       // flips 2 and 3, as always...
    CHECK(m.counters().target_skips == 2); // ...which its target asked for

    // Held for flip 6 and Presented a flip late: 5 and 6 are missed, only 5 asked for.
    const Acquired b = m.frame(g, 4 * kRefresh + 1 * kMs, 2);
    m.hold(b.seq, 6 * kRefresh - kRefresh / 2); // NEAREST
    m.vblank(5 * kRefresh);
    m.vblank(6 * kRefresh);
    m.present(b.seq, 6 * kRefresh + 1 * kMs, 6 * kRefresh + 1 * kMs + 50'000);
    m.vblank(7 * kRefresh);
    REQUIRE(m.reports.size() == 3);
    CHECK(m.reports[2].times.on_screen_ns == 7 * kRefresh);
    CHECK(m.counters().held == 2);
    CHECK(m.counters().missed == 2 + 2);
    CHECK(m.counters().target_skips == 2 + 1);
}

TEST_CASE("frame model: an on-time or late frame is not held")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    m.vblank(1 * kRefresh);

    // Its target is the next flip, the first it can make: as if it had none.
    const Acquired a = m.frame(g, kRefresh + 1 * kMs, 1);
    m.hold(a.seq, 2 * kRefresh - 1 * kMs);
    REQUIRE(m.candidate(kRefresh + 2 * kMs).has_value());
    m.present(a.seq, kRefresh + 2 * kMs, kRefresh + 2 * kMs + 50'000);
    m.vblank(2 * kRefresh);

    // Its target has passed: the next flip, never later.
    const Acquired b = m.frame(g, 2 * kRefresh + 1 * kMs, 2);
    m.hold(b.seq, 1 * kRefresh);
    REQUIRE(m.candidate(2 * kRefresh + 2 * kMs).has_value());
    m.present(b.seq, 2 * kRefresh + 2 * kMs, 2 * kRefresh + 2 * kMs + 50'000);
    m.vblank(3 * kRefresh);

    // A limit before any QPC time (a target near 0): the same.
    const Acquired c = m.frame(g, 3 * kRefresh + 1 * kMs, 3);
    m.hold(c.seq, -5 * kMs);
    REQUIRE(m.candidate(3 * kRefresh + 2 * kMs).has_value());
    m.present(c.seq, 3 * kRefresh + 2 * kMs, 3 * kRefresh + 2 * kMs + 50'000);
    m.vblank(4 * kRefresh);

    REQUIRE(m.reports.size() == 3);
    CHECK(m.reports[0].times.on_screen_ns == 2 * kRefresh);
    CHECK(m.reports[1].times.on_screen_ns == 3 * kRefresh);
    CHECK(m.reports[2].times.on_screen_ns == 4 * kRefresh);
    CHECK(m.counters().held == 0);
}

TEST_CASE("frame model: a flip read with a stale time does not hold a frame one flip too long")
{
    // The stats' time of a read that saw the counter move is the refresh's; should it lag (an older refresh's time),
    // the count gives the flip instead, so a frame due at the next flip is not held for one more.
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    m.vblank(1 * kRefresh);
    const Acquired a = m.frame(g, kRefresh + 1 * kMs, 1);
    m.hold(a.seq, 3 * kRefresh - 1 * kMs);
    CHECK_FALSE(m.candidate(kRefresh + 2 * kMs).has_value());

    m.on_counter(++m.counter, 1 * kRefresh); // flip 2, read with flip 1's time
    REQUIRE(m.candidate(2 * kRefresh + 1 * kMs).has_value());

    // Two flips in one read, with a stale time: both counted.
    M n;
    n.start(0);
    const SwapchainGen h = n.add_swapchain();
    n.vblank(1 * kRefresh);
    const Acquired b = n.frame(h, kRefresh + 1 * kMs, 1);
    n.hold(b.seq, 4 * kRefresh - 1 * kMs);
    n.counter += 2;
    n.on_counter(n.counter, 1 * kRefresh + 100'000); // flips 2 and 3
    REQUIRE(n.candidate(3 * kRefresh + 1 * kMs).has_value());

    // A time a little early (the flips' jitter, under 0.5 ms) is taken as it is: the frame due at flip 4 within its
    // 1 ms waits for flip 3.
    M o;
    o.start(0);
    const SwapchainGen k = o.add_swapchain();
    o.vblank(1 * kRefresh);
    const Acquired c = o.frame(k, kRefresh + 1 * kMs, 1);
    o.hold(c.seq, 4 * kRefresh - 1 * kMs);
    o.vblank(2 * kRefresh - 300'000);
    CHECK_FALSE(o.candidate(2 * kRefresh + 1 * kMs).has_value());
    o.vblank(3 * kRefresh - 300'000); // 3 * kRefresh - 0.3 ms + kRefresh >= 4 * kRefresh - 1 ms
    REQUIRE(o.candidate(3 * kRefresh + 1 * kMs).has_value());
}

TEST_CASE("frame model: no queued frame overtakes a held one; at its flip the newest goes")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    m.vblank(1 * kRefresh);

    // An untargeted frame queued before the held one is not held by it.
    const Acquired a = m.frame(g, kRefresh + 1 * kMs, 1);
    const Acquired b = m.frame(g, kRefresh + 2 * kMs, 2);
    m.hold(b.seq, 4 * kRefresh - 1 * kMs);
    std::optional<Candidate> c = m.candidate(kRefresh + 3 * kMs);
    REQUIRE(c.has_value());
    CHECK(c->seq == a.seq);
    m.present(a.seq, kRefresh + 3 * kMs, kRefresh + 3 * kMs + 50'000);
    CHECK(m.state_of(b.seq) == FrameState::Queued); // not retired by a's Present: it is newer
    m.vblank(2 * kRefresh);

    // An untargeted frame queued after it waits for it, and goes in its place at its flip.
    const Acquired d = m.frame(g, 2 * kRefresh + 1 * kMs, 3);
    CHECK_FALSE(m.candidate(2 * kRefresh + 2 * kMs).has_value());
    m.signalled.insert(m.fence_of.at(d.seq));
    m.wait_seen(m.fence_of.at(d.seq));
    CHECK_FALSE(m.present_started(d.seq, 2 * kRefresh + 2 * kMs));
    m.vblank(3 * kRefresh);
    c = m.candidate(3 * kRefresh + 1 * kMs);
    REQUIRE(c.has_value());
    CHECK(c->seq == d.seq);
    REQUIRE(m.present_started(d.seq, 3 * kRefresh + 1 * kMs));
    CHECK(m.end_of(b.seq) == FrameEnd::Retired);
}

TEST_CASE("frame model: a frame behind a held one waits for the held one's flip")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();
    m.vblank(1 * kRefresh);
    const Acquired a = m.frame(g, kRefresh + 1 * kMs, 1);
    m.hold(a.seq, 4 * kRefresh - 1 * kMs);
    const Acquired b = m.frame(g, kRefresh + 2 * kMs, 2); // untargeted
    m.vblank(2 * kRefresh);
    CHECK_FALSE(m.candidate(2 * kRefresh + 1 * kMs).has_value());
    m.vblank(3 * kRefresh);
    const std::optional<Candidate> c = m.candidate(3 * kRefresh + 1 * kMs);
    REQUIRE(c.has_value());
    CHECK(c->seq == b.seq); // the newest of the frames due
    m.present(b.seq, 3 * kRefresh + 1 * kMs, 3 * kRefresh + 1 * kMs + 50'000);
    CHECK(m.end_of(a.seq) == FrameEnd::Retired);
    m.vblank(4 * kRefresh);
    REQUIRE(m.reports.size() == 2);
    CHECK(m.reports[0].seq == a.seq);
    CHECK(m.reports[0].times.on_screen_ns == 0);
    CHECK(m.reports[1].seq == b.seq);
    CHECK(m.reports[1].times.on_screen_ns == 4 * kRefresh);
}

TEST_CASE("frame model: nothing is held before an epoch's first flip, after a link drop, or while flips are stopped")
{
    M m;
    m.start(0);
    const SwapchainGen g = m.add_swapchain();

    // No flip seen yet: no phase to hold against.
    const Acquired a = m.frame(g, 1 * kMs, 1);
    m.hold(a.seq, 10 * kRefresh);
    CHECK(m.counters().held == 0);
    REQUIRE(m.candidate(2 * kMs).has_value());
    m.present(a.seq, 2 * kMs, 2 * kMs + 50'000);
    m.vblank(1 * kRefresh);

    // Held, then a link drop: the phase may have moved, so it goes at once.
    const Acquired b = m.frame(g, kRefresh + 1 * kMs, 2);
    m.hold(b.seq, 10 * kRefresh);
    CHECK_FALSE(m.candidate(kRefresh + 2 * kMs).has_value());
    m.on_counter(m.counter - 5, kRefresh + 3 * kMs);
    REQUIRE(m.candidate(kRefresh + 4 * kMs).has_value());

    // A new epoch (the display thread restarted): the same until its first flip.
    m.thread_started(kRefresh + 5 * kMs);
    m.on_counter(m.counter, kRefresh + 5 * kMs); // the baseline
    REQUIRE(m.candidate(kRefresh + 6 * kMs).has_value());
    m.vblank(2 * kRefresh);
    CHECK_FALSE(m.candidate(2 * kRefresh + 1 * kMs).has_value()); // held again from a flip on

    // Flips stopped: no refresh to hold for; the one Present still allowed may go.
    m.tick(2 * kRefresh + 700 * kMs);
    REQUIRE(m.model.flips_stopped());
    REQUIRE(m.candidate(2 * kRefresh + 700 * kMs).has_value());
}
