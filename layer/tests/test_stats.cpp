// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The stats line (design 6.8): its windows, its app sessions and its fields. A feeder plays a display that flips every
// 1/90 s and an app that presents 2 ms after each flip; the missed test drives the frame model, which owns that count.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <optional>
#include <string>

#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "core/stats.hpp"

namespace {

using nvdm::Acquired;
using nvdm::FencePool;
using nvdm::FenceSlot;
using nvdm::FrameModel;
using nvdm::FrameModelConfig;
using nvdm::StatsWindow;
using nvdm::SwapchainGen;

constexpr int64_t kUs = 1'000;
constexpr int64_t kMs = 1'000'000;
constexpr int64_t kRefresh = 11'111'111;

// The stats window alone: flips through on_refresh, presents through on_present, counters set by the test.
struct Feeder {
    StatsWindow stats;
    FrameModel::Counters counters;
    uint32_t counter = 5000;
    int64_t t = 0; // the last flip

    Feeder() { stats.on_refresh(counter, t); }

    void flip()
    {
        t += kRefresh;
        stats.on_refresh(++counter, t);
    }
    // A flip, then a present 2 ms after it. Returns the line that present closed, if any.
    std::optional<std::string> frame()
    {
        flip();
        stats.on_counters(counters);
        stats.on_present(t + 2 * kMs);
        return stats.take_line();
    }
    // Frames until one closes a line; how many it took. The line is kept in `line`.
    int frames_to_line(int limit)
    {
        for (int i = 1; i <= limit; ++i) {
            line = frame();
            if (line) {
                return i;
            }
        }
        return -1;
    }
    std::optional<std::string> line;
};

bool starts_with(const std::string& text, const std::string& head)
{
    return text.compare(0, head.size(), head) == 0;
}

// The frame model behind the stats, as the display loop feeds them: a frame per call, Presented 2 ms after a flip and
// credited at the next one.
struct Drive {
    FencePool pool{16, [](FenceSlot) { return true; }};
    FrameModel model{FrameModelConfig{}, &pool};
    StatsWindow stats;
    SwapchainGen gen = 0;
    uint32_t counter = 100;
    int64_t t = 0;
    uint64_t id = 0;

    Drive()
    {
        model.thread_started(t);
        model.on_counter(counter, t);
        stats.on_refresh(counter, t);
        gen = model.add_swapchain();
    }
    void flip()
    {
        t += kRefresh;
        model.on_counter(++counter, t);
        stats.on_refresh(counter, t);
        stats.on_counters(model.counters());
    }
    void flips(int n)
    {
        for (int i = 0; i < n; ++i) {
            flip();
        }
    }
    std::optional<std::string> present()
    {
        const std::optional<Acquired> a = model.acquire(gen, t + 1 * kMs);
        REQUIRE(a.has_value());
        const std::optional<FenceSlot> fence = pool.take();
        REQUIRE(fence.has_value());
        model.queue(a->seq, ++id, *fence, t + 1 * kMs);
        model.fence_waiting(*fence, true);
        model.fence_seen(*fence);
        model.fence_waiting(*fence, false);
        REQUIRE(model.present_started(a->seq, t + 2 * kMs));
        stats.on_counters(model.counters());
        stats.on_present(t + 2 * kMs);
        model.present_returned(a->seq, 0, t + 2 * kMs + 50 * kUs);
        return stats.take_line();
    }
};

} // namespace

TEST_CASE("first_line_after_90_then_every_2700")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value()); // the app session's first present opens its window

    CHECK(f.frames_to_line(100) == 90);
    CHECK(f.frames_to_line(3000) == 2700);
    CHECK(f.frames_to_line(3000) == 2700);
    CHECK_FALSE(f.stats.take_line().has_value()); // each line is taken once
}

TEST_CASE("new_session_after_1s_gap")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    REQUIRE(f.frames_to_line(100) == 90);
    for (int i = 0; i < 50; ++i) {
        REQUIRE_FALSE(f.frame().has_value()); // into the 2700-present window
    }

    SECTION("over 1 s without a present: a new app session, whose first 90 presents get a line")
    {
        for (int i = 0; i < 100; ++i) { // 1.11 s of flips, no present
            f.flip();
        }
        REQUIRE_FALSE(f.frame().has_value()); // opens the new session's window; the old one is dropped
        CHECK(f.frames_to_line(3000) == 90);
        REQUIRE(f.line.has_value());
        CHECK(starts_with(*f.line, "90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz),")); // not the gap's flips
    }
    SECTION("exactly 1 s without a present: the same session")
    {
        f.flip();
        f.stats.on_present(f.t + 2 * kMs); // present 51 of the window
        f.t += 1'000'000'000;
        f.stats.on_present(f.t + 2 * kMs); // 52, 1 s later and not more
        CHECK_FALSE(f.stats.take_line().has_value());
        CHECK(f.frames_to_line(3000) == 2700 - 52);
    }
}

TEST_CASE("missed_counts_gap_minus_one_ignoring_over_1s")
{
    Drive d;
    std::optional<std::string> line;
    int presents = 0;
    auto frame = [&] {
        line = d.present();
        ++presents;
        d.flip(); // shown at the next flip
    };
    frame(); // the session's first present opens the window
    // A frame shown three flips after the one before it: two flips repeated the old frame.
    frame();
    d.flips(2);
    frame();
    // Half a second without a frame (45 flips): still the app session, 44 missed.
    d.flips(44);
    frame();
    while (!line) {
        frame();
    }
    CHECK(presents == 91);
    // 90 flips after the window's first present, plus the 2 and the 44 that repeated a frame.
    CHECK(starts_with(*line, "90 presents in 1.5 s (59.6 Hz), 136 flips (90.0 Hz), 46 missed (33.82 %),"));

    // A second and a half without a frame (135 flips): a new app session; the gap is idle, not missed.
    d.flips(134);
    line.reset();
    presents = 0;
    while (!line) {
        frame();
    }
    CHECK(presents == 91);
    CHECK(starts_with(*line, "90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz), 0 missed (0.00 %),"));
    CHECK(d.model.counters().missed == 46); // the model, which owns the count, did not count the gap either
}

TEST_CASE("line_format_matches")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    REQUIRE(f.frames_to_line(100) == 90);

    // A normal 30 s: every flip shows a new frame. The display-event waits woke 0.10 ms after the flip, once 0.25 ms.
    std::optional<std::string> line;
    for (int i = 0; i < 2700 && !line; ++i) {
        f.stats.on_wake(i == 1000 ? 250 * kUs : 100 * kUs);
        line = f.frame();
    }
    REQUIRE(line.has_value());
    CHECK(*line == "2700 presents in 30.0 s (90.0 Hz), 2700 flips (90.0 Hz), 0 missed (0.00 %), 0 link drops; "
                   "dropped 0, failed 0, unconfirmed 0, reserved 0, held 0, target skips 0; event wake median 0.10 ms, max 0.25 ms");
}

TEST_CASE("stats: frame ends and link drops are the window's own")
{
    Feeder f;
    f.counters.dropped = 7; // before the session
    f.counters.link_drops = 1;
    REQUIRE_FALSE(f.frame().has_value());
    REQUIRE(f.frames_to_line(100) == 90);

    std::optional<std::string> line;
    for (int i = 0; i < 2700 && !line; ++i) {
        if (i == 10) {
            f.counters.dropped += 2;
            f.counters.failed += 1;
            f.counters.unconfirmed += 3;
            f.counters.reserved += 4;
            f.counters.link_drops += 1;
            f.counters.missed += 27;
        }
        line = f.frame();
    }
    REQUIRE(line.has_value());
    CHECK(*line == "2700 presents in 30.0 s (90.0 Hz), 2700 flips (90.0 Hz), 27 missed (1.00 %), 1 link drops; "
                   "dropped 2, failed 1, unconfirmed 3, reserved 4, held 0, target skips 0; event wake median 0.00 ms, max 0.00 ms");
}

TEST_CASE("stats: flips count the counter's increments, a link drop counting on from the new value")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    for (int i = 0; i < 40; ++i) {
        REQUIRE_FALSE(f.frame().has_value());
    }
    f.counter = 3;                       // the link dropped: the counter restarted
    f.stats.on_refresh(f.counter, f.t);  // not a flip
    f.stats.on_refresh(f.counter, f.t);  // the same value read again
    std::optional<std::string> line;
    for (int i = 0; i < 100 && !line; ++i) {
        line = f.frame();
    }
    REQUIRE(line.has_value());
    CHECK(starts_with(*line, "90 presents in 1.0 s (90.0 Hz), 90 flips (90.0 Hz),"));
}

TEST_CASE("stats: event wake median of an even count is the mean of the middle two")
{
    Feeder f;
    REQUIRE_FALSE(f.frame().has_value());
    f.stats.on_wake(100 * kUs);
    f.stats.on_wake(400 * kUs);
    f.stats.on_wake(200 * kUs);
    f.stats.on_wake(300 * kUs);
    std::optional<std::string> line;
    for (int i = 0; i < 100 && !line; ++i) {
        line = f.frame();
    }
    REQUIRE(line.has_value());
    const std::string tail = "event wake median 0.25 ms, max 0.40 ms";
    REQUIRE(line->size() > tail.size());
    CHECK(line->substr(line->size() - tail.size()) == tail);
}
