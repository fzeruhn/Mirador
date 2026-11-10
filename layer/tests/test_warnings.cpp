// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// Design 8's degradation warnings: each fires at most once per app session (a session starts at the first present
// after more than 1 s without one). A player drives dvl::Warnings the way the layer does at 90 Hz: Monado presents,
// then registers a display event and waits on it, then waits for the present.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

#include "core/frame_model.hpp"
#include "layer/warnings.hpp"

namespace {

constexpr int64_t kMs = 1'000'000;
constexpr int64_t kS = 1'000'000'000;
constexpr int64_t kRefresh = 11'111'111;

size_t count_with(const std::vector<std::string>& lines, const std::string& part)
{
    return static_cast<size_t>(std::count_if(lines.begin(), lines.end(), [&](const std::string& line) {
        return line.find(part) != std::string::npos;
    }));
}

struct Player {
    dvl::Warnings warnings;
    int64_t t = 10 * kS;
    uint64_t next_event = 1;
    bool events = true;       // Monado's vblank thread runs
    bool wait_events = true;  // its fence waits reach the layer
    bool present_wait = true; // XRT_COMPOSITOR_USE_PRESENT_WAIT=1
    std::vector<std::string> lines;

    Player()
    {
        warnings.set_display_control(true);
        warnings.set_present_mode(VK_PRESENT_MODE_FIFO_KHR);
        warnings.set_memory_fallback(false);
    }

    // One frame of Monado's loop, and the lines it gave.
    void frame()
    {
        t += kRefresh;
        warnings.on_present(t);
        if (events) {
            const uint64_t key = next_event++;
            warnings.on_event_registered(key, t + kMs);
            if (wait_events) {
                warnings.on_event_waited(key);
            }
            warnings.check(t + 2 * kMs);
            warnings.on_event_gone(key);
        }
        if (present_wait) {
            warnings.on_present_wait();
        }
        warnings.on_counters(nvdm::FrameModel::Counters{}, false);
        take();
    }
    void frames(int n)
    {
        for (int i = 0; i < n; ++i) {
            frame();
        }
    }
    void take()
    {
        for (std::string& line : warnings.take_lines()) {
            lines.push_back(std::move(line));
        }
    }
};

} // namespace

TEST_CASE("warns_once_when_no_present_wait_after_90")
{
    Player p;
    p.present_wait = false;
    p.frames(89);
    CHECK(p.lines.empty()); // not before the 90th present
    p.frame();
    REQUIRE(p.lines.size() == 1);
    CHECK(count_with(p.lines, "vkWaitForPresentKHR") == 1);
    CHECK(count_with(p.lines, "XRT_COMPOSITOR_USE_PRESENT_WAIT") == 1);
    p.frames(3000);
    CHECK(p.lines.size() == 1); // once per app session

    // More than 1 s without a present: a new app session, warned again after its 90th.
    p.t += 2 * kS;
    p.frames(89);
    CHECK(p.lines.size() == 1);
    p.frame();
    CHECK(count_with(p.lines, "vkWaitForPresentKHR") == 2);

    // The display event's counterpart, with the vblank thread missing.
    Player q;
    q.events = false;
    q.frames(90);
    REQUIRE(q.lines.size() == 1);
    CHECK(count_with(q.lines, "vkRegisterDisplayEventEXT") == 1);
    q.frames(500);
    CHECK(q.lines.size() == 1);
}

TEST_CASE("warns_event_without_wait_after_2s")
{
    dvl::Warnings w;
    w.set_display_control(true);
    w.set_present_mode(VK_PRESENT_MODE_FIFO_KHR);
    const int64_t t0 = 10 * kS;
    // Presents keep the app session going (one every 100 ms); the event is never waited on.
    const auto presents_until = [&](int64_t from, int64_t to) {
        for (int64_t t = from; t <= to; t += 100 * kMs) {
            w.on_present(t);
        }
    };
    presents_until(t0, t0);
    w.on_event_registered(1, t0);
    presents_until(t0 + 100 * kMs, t0 + 1900 * kMs);
    w.check(t0 + 1999 * kMs);
    CHECK(w.take_lines().empty());
    presents_until(t0 + 2000 * kMs, t0 + 2000 * kMs);
    w.check(t0 + 2 * kS);
    std::vector<std::string> lines = w.take_lines();
    REQUIRE(lines.size() == 1);
    CHECK(count_with(lines, "2 s") == 1);

    // Another event left unwaited in the same app session: no second line.
    presents_until(t0 + 2100 * kMs, t0 + 2100 * kMs);
    w.on_event_registered(2, t0 + 2100 * kMs);
    presents_until(t0 + 2200 * kMs, t0 + 5 * kS);
    w.check(t0 + 5 * kS);
    lines = w.take_lines();
    CHECK(count_with(lines, "2 s") == 0);

    // A waited event (then destroyed) is never overdue; nor is one whose wait arrives just before 2 s.
    dvl::Warnings fine;
    fine.set_display_control(true);
    fine.set_present_mode(VK_PRESENT_MODE_FIFO_KHR);
    fine.on_present(t0);
    fine.on_event_registered(1, t0);
    fine.on_event_registered(3, t0);
    fine.on_event_waited(1);
    fine.on_event_gone(1);
    fine.check(t0 + 1999 * kMs);
    fine.on_event_waited(3);
    fine.check(t0 + 10 * kS);
    CHECK(fine.take_lines().empty());

    // A fence destroyed with no wait first: the wait went past the layer (Monado waits before it destroys). Warned
    // at 2 s, as for one never destroyed.
    dvl::Warnings bypassed;
    bypassed.set_display_control(true);
    bypassed.set_present_mode(VK_PRESENT_MODE_FIFO_KHR);
    bypassed.on_present(t0);
    bypassed.on_event_registered(2, t0);
    bypassed.on_event_gone(2);
    bypassed.check(t0 + 1999 * kMs);
    CHECK(bypassed.take_lines().empty());
    bypassed.on_present(t0 + 900 * kMs);
    bypassed.on_present(t0 + 1800 * kMs);
    bypassed.check(t0 + 2 * kS);
    CHECK(count_with(bypassed.take_lines(), "2 s") == 1);
}

TEST_CASE("no_warning_on_normal_session")
{
    Player p;
    p.frames(3000);
    p.t += 5 * kS; // a pause between app sessions
    p.frames(300);
    CHECK(p.lines.empty());
}

TEST_CASE("standing_conditions_warn_at_each_app_session")
{
    // The device without VK_EXT_display_control, a present mode other than FIFO, the memory-type fallback: one line
    // each at the first present of each app session.
    dvl::Warnings w;
    w.set_display_control(false);
    w.set_present_mode(VK_PRESENT_MODE_MAILBOX_KHR);
    w.set_memory_fallback(true);
    CHECK(w.take_lines().empty()); // no app session yet
    w.on_present(10 * kS);
    std::vector<std::string> lines = w.take_lines();
    CHECK(lines.size() == 3);
    CHECK(count_with(lines, "VK_EXT_display_control") == 1);
    CHECK(count_with(lines, "FIFO") == 1);
    CHECK(count_with(lines, "memory") == 1);
    for (int i = 1; i < 100; ++i) {
        w.on_present(10 * kS + i * kRefresh);
        w.on_present_wait();
        w.on_event_registered(static_cast<uint64_t>(i), 10 * kS + i * kRefresh);
        w.on_event_waited(static_cast<uint64_t>(i));
    }
    CHECK(w.take_lines().empty());
    w.on_present(20 * kS);
    CHECK(w.take_lines().size() == 3);
}

TEST_CASE("frame_model_degradations_warn_once_per_app_session")
{
    dvl::Warnings w;
    w.set_display_control(true);
    w.set_present_mode(VK_PRESENT_MODE_FIFO_KHR);
    w.on_present(10 * kS);
    nvdm::FrameModel::Counters c;
    w.on_counters(c, false);
    CHECK(w.take_lines().empty());

    c.dropped = 2;
    c.failed = 1;
    c.unconfirmed = 1;
    c.reserved = 1;
    w.on_counters(c, true);
    std::vector<std::string> lines = w.take_lines();
    CHECK(lines.size() == 5);
    CHECK(count_with(lines, "flips stopped") == 1);
    CHECK(count_with(lines, "dropped") == 1);
    CHECK(count_with(lines, "failed") == 1);
    CHECK(count_with(lines, "unconfirmed") == 1);
    CHECK(count_with(lines, "reserved") == 1);

    c.dropped = 9;
    c.failed = 4;
    w.on_counters(c, true);
    w.on_fence_fallback();
    w.on_fence_fallback();
    w.on_thread_left_behind();
    lines = w.take_lines();
    CHECK(lines.size() == 2); // the two that had not fired yet
    CHECK(count_with(lines, "vkQueueWaitIdle") == 1);
    CHECK(count_with(lines, "left behind") == 1);

    // The next app session warns again, on new increases only.
    w.on_present(20 * kS);
    w.on_counters(c, false);
    CHECK(w.take_lines().empty());
    c.dropped = 10;
    w.on_counters(c, false);
    lines = w.take_lines();
    REQUIRE(lines.size() == 1);
    CHECK(count_with(lines, "dropped") == 1);
}
