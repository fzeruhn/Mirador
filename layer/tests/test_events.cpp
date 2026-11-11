// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The display events of VK_EXT_display_control (design 6.9): the event table, and vkWaitForFences on lists that name
// display events, in fake time. A fake host plays the driver's ordinary fences (each signals at a scripted time, or
// never) and the display thread's refreshes (scripted too), and moves the clock only inside its waits.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <vector>

#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "fakes/fake_clock.hpp"
#include "layer/event_table.hpp"

namespace {

using dvl::EventTable;
using dvl::EventWait;

constexpr int64_t kMs = 1'000'000;
constexpr int64_t kS = 1'000'000'000;
constexpr int64_t kRefresh = 11'111'111;

template <class T>
T handle(uintptr_t value)
{
    return reinterpret_cast<T>(value);
}

const VkDevice kDevice = handle<VkDevice>(0xDE0);
const VkDevice kOtherDevice = handle<VkDevice>(0xDE1);
const VkFence kEvent = handle<VkFence>(0xE1);
const VkFence kEvent2 = handle<VkFence>(0xE2);
const VkFence kOrdinary = handle<VkFence>(0xF1);
const VkFence kOrdinary2 = handle<VkFence>(0xF2);

// The driver's ordinary fences and the display thread's refreshes, in fake time.
struct FakeHost : dvl::FenceWaitHost {
    nvdm::FakeClock clock{0};
    EventTable& table;
    std::multimap<int64_t, int64_t> refreshes;         // at fake time t, a refresh whose QPC time is the value
    std::map<VkFence, int64_t> ready_at;               // when each ordinary fence signals; absent: never
    std::vector<std::vector<VkFence>> down_calls;      // what wait_down was given
    std::vector<uint64_t> down_timeouts;
    size_t change_waits = 0;

    explicit FakeHost(EventTable& t) : table(t) {}

    // A refresh at fake time `at`, with that QPC time.
    void refresh_at(int64_t at) { refreshes.emplace(at, at); }

    // Moves the clock to `t`, running every refresh due on the way.
    void run_until(int64_t t)
    {
        while (!refreshes.empty() && refreshes.begin()->first <= t) {
            const auto it = refreshes.begin();
            if (it->first > clock.now_ns()) {
                clock.advance(it->first - clock.now_ns());
            }
            table.on_refresh(it->second);
            refreshes.erase(it);
        }
        if (t > clock.now_ns()) {
            clock.advance(t - clock.now_ns());
        }
    }

    int64_t now_ns() override { return clock.now_ns(); }

    VkResult wait_down(const std::vector<VkFence>& fences, bool wait_all, uint64_t timeout_ns) override
    {
        down_calls.push_back(fences);
        down_timeouts.push_back(timeout_ns);
        int64_t done = wait_all ? 0 : INT64_MAX;
        for (const VkFence f : fences) {
            const auto it = ready_at.find(f);
            const int64_t at = it == ready_at.end() ? INT64_MAX : it->second;
            done = wait_all ? std::max(done, at) : std::min(done, at);
        }
        const int64_t now = clock.now_ns();
        if (done <= now) {
            return VK_SUCCESS;
        }
        const int64_t end = timeout_ns >= static_cast<uint64_t>(INT64_MAX - now) ? INT64_MAX
                                                                                 : now + static_cast<int64_t>(timeout_ns);
        if (done <= end) {
            run_until(done);
            return VK_SUCCESS;
        }
        run_until(end);
        return VK_TIMEOUT;
    }

    void wait_change(std::unique_lock<std::mutex>& lock, int64_t until_ns) override
    {
        REQUIRE(lock.owns_lock()); // the caller holds the session lock, as with a condition variable
        ++change_waits;
        const int64_t next = refreshes.empty() ? until_ns : std::min(until_ns, refreshes.begin()->first);
        run_until(next);
    }
};

struct WaitRig {
    EventTable table;
    FakeHost host{table};
    std::mutex lock;

    EventWait wait(std::vector<VkFence> fences, bool wait_all, uint64_t timeout_ns)
    {
        std::unique_lock<std::mutex> hold(lock);
        const EventWait result = dvl::wait_for_fences(table, hold, kDevice, fences.data(),
                                                      static_cast<uint32_t>(fences.size()), wait_all, timeout_ns, host);
        CHECK(hold.owns_lock()); // given back held
        return result;
    }
};

} // namespace

TEST_CASE("event_set_by_first_refresh_after_registration")
{
    EventTable table;
    table.add(kDevice, kEvent, 100 * kMs);
    CHECK(table.is_event(kDevice, kEvent));
    CHECK_FALSE(table.is_set(kDevice, kEvent));
    CHECK(table.pending());

    CHECK(table.on_refresh(105 * kMs) == 1); // any refresh, whether or not it showed a new frame
    CHECK(table.is_set(kDevice, kEvent));
    REQUIRE(table.set_at(kDevice, kEvent).has_value());
    CHECK(*table.set_at(kDevice, kEvent) == 105 * kMs);
    CHECK_FALSE(table.pending());

    // A later refresh does not move it, and an event registered after that refresh waits for the next one.
    table.add(kDevice, kEvent2, 106 * kMs);
    CHECK(table.on_refresh(116 * kMs) == 1);
    CHECK(*table.set_at(kDevice, kEvent) == 105 * kMs);
    CHECK(*table.set_at(kDevice, kEvent2) == 116 * kMs);

    // The same fence handle on another device is another event, and not this one.
    CHECK_FALSE(table.is_event(kOtherDevice, kEvent));
}

TEST_CASE("event_not_set_by_refresh_before_registration")
{
    EventTable table;
    table.add(kDevice, kEvent, 100 * kMs);
    // A refresh the display thread reports after the registration, but whose QPC time is before it (or at it), showed
    // a frame before the event was asked for.
    CHECK(table.on_refresh(95 * kMs) == 0);
    CHECK(table.on_refresh(100 * kMs) == 0);
    CHECK_FALSE(table.is_set(kDevice, kEvent));
    CHECK(table.pending());
    CHECK(table.on_refresh(100 * kMs + kRefresh) == 1);
    CHECK(table.is_set(kDevice, kEvent));
}

TEST_CASE("wait_honours_timeout")
{
    WaitRig rig;
    rig.table.add(kDevice, kEvent, 0);

    // Monado's 1 s, no refresh: a timeout at 1 s, not before and not much after.
    const EventWait one = rig.wait({kEvent}, true, 1'000'000'000);
    CHECK(one.result == VK_TIMEOUT);
    CHECK(rig.host.now_ns() >= kS);
    CHECK(rig.host.now_ns() <= kS + kMs);
    CHECK_FALSE(one.set_ns.has_value());
    CHECK(rig.host.down_calls.empty()); // nothing to pass down

    // A timeout of 0 only looks.
    const int64_t before = rig.host.now_ns();
    CHECK(rig.wait({kEvent}, true, 0).result == VK_TIMEOUT);
    CHECK(rig.host.now_ns() == before);

    // A refresh within the timeout ends it then, and says which refresh did.
    rig.host.refresh_at(before + 7 * kMs);
    const EventWait set = rig.wait({kEvent}, true, 1'000'000'000);
    CHECK(set.result == VK_SUCCESS);
    CHECK(rig.host.now_ns() == before + 7 * kMs);
    REQUIRE(set.set_ns.has_value());
    CHECK(*set.set_ns == before + 7 * kMs);

    // Signalled stays signalled: another wait returns at once.
    const int64_t after = rig.host.now_ns();
    CHECK(rig.wait({kEvent}, true, 1'000'000'000).result == VK_SUCCESS);
    CHECK(rig.host.now_ns() == after);
}

TEST_CASE("mixed_list_split_wait_all_and_any")
{
    SECTION("wait all: the event and the ordinary fence, the ordinary one passed down alone")
    {
        WaitRig rig;
        rig.table.add(kDevice, kEvent, 0);
        rig.host.ready_at[kOrdinary] = 5 * kMs;
        rig.host.refresh_at(11 * kMs);
        const EventWait w = rig.wait({kOrdinary, kEvent}, true, kS);
        CHECK(w.result == VK_SUCCESS);
        CHECK(rig.host.now_ns() == 11 * kMs); // both: the later of the two
        REQUIRE_FALSE(rig.host.down_calls.empty());
        for (const std::vector<VkFence>& call : rig.host.down_calls) {
            CHECK(call == std::vector<VkFence>{kOrdinary}); // never the event's fence
        }
    }
    SECTION("wait all: the ordinary fence late, within the timeout")
    {
        WaitRig rig;
        rig.table.add(kDevice, kEvent, 0);
        rig.host.ready_at[kOrdinary] = 30 * kMs;
        rig.host.refresh_at(11 * kMs);
        CHECK(rig.wait({kEvent, kOrdinary}, true, kS).result == VK_SUCCESS);
        CHECK(rig.host.now_ns() == 30 * kMs);
    }
    SECTION("wait all: the timeout is one budget for both kinds")
    {
        WaitRig rig;
        rig.table.add(kDevice, kEvent, 0);
        rig.host.ready_at[kOrdinary] = 30 * kMs;
        rig.host.refresh_at(11 * kMs);
        CHECK(rig.wait({kOrdinary, kEvent}, true, 20 * kMs).result == VK_TIMEOUT);
        CHECK(rig.host.now_ns() >= 20 * kMs);
        CHECK(rig.host.now_ns() <= 21 * kMs);
    }
    SECTION("wait any: the ordinary fence first")
    {
        WaitRig rig;
        rig.table.add(kDevice, kEvent, 0);
        rig.host.ready_at[kOrdinary] = 5 * kMs;
        rig.host.refresh_at(11 * kMs);
        const EventWait w = rig.wait({kEvent, kOrdinary}, false, kS);
        CHECK(w.result == VK_SUCCESS);
        CHECK(rig.host.now_ns() >= 5 * kMs);
        CHECK(rig.host.now_ns() <= 6 * kMs); // looked at within a millisecond of it signalling
        CHECK_FALSE(w.set_ns.has_value());   // no event ended it
        CHECK_FALSE(rig.table.is_set(kDevice, kEvent));
    }
    SECTION("wait any: the event first")
    {
        WaitRig rig;
        rig.table.add(kDevice, kEvent, 0);
        rig.host.refresh_at(11 * kMs); // the ordinary fence never signals
        const EventWait w = rig.wait({kOrdinary, kEvent, kOrdinary2}, false, kS);
        CHECK(w.result == VK_SUCCESS);
        CHECK(rig.host.now_ns() == 11 * kMs);
        REQUIRE(w.set_ns.has_value());
        CHECK(*w.set_ns == 11 * kMs);
        for (const std::vector<VkFence>& call : rig.host.down_calls) {
            CHECK(call == std::vector<VkFence>{kOrdinary, kOrdinary2});
            // Only looks: it cannot wait for the event and the driver at once, and must not sleep through a refresh.
        }
        for (const uint64_t timeout : rig.host.down_timeouts) {
            CHECK(timeout == 0);
        }
    }
    SECTION("wait any: neither, a timeout")
    {
        WaitRig rig;
        rig.table.add(kDevice, kEvent, 0);
        CHECK(rig.wait({kOrdinary, kEvent}, false, 25 * kMs).result == VK_TIMEOUT);
        CHECK(rig.host.now_ns() >= 25 * kMs);
        CHECK(rig.host.now_ns() <= 26 * kMs);
    }
    SECTION("two events, wait all and wait any")
    {
        WaitRig rig;
        rig.table.add(kDevice, kEvent, 0);
        rig.table.add(kDevice, kEvent2, 12 * kMs); // after the first refresh: it needs the second
        rig.host.refresh_at(11 * kMs);
        rig.host.refresh_at(22 * kMs);
        CHECK(rig.wait({kEvent, kEvent2}, false, kS).result == VK_SUCCESS);
        CHECK(rig.host.now_ns() == 11 * kMs);
        CHECK(rig.wait({kEvent, kEvent2}, true, kS).result == VK_SUCCESS);
        CHECK(rig.host.now_ns() == 22 * kMs);
        CHECK(rig.host.down_calls.empty());
    }
    SECTION("the split keeps each kind in order")
    {
        EventTable table;
        table.add(kDevice, kEvent, 0);
        table.add(kDevice, kEvent2, 0);
        const VkFence list[] = {kOrdinary, kEvent2, kOrdinary2, kEvent};
        const EventTable::Split split = table.split(kDevice, list, 4);
        CHECK(split.events == std::vector<VkFence>{kEvent2, kEvent});
        CHECK(split.ordinary == std::vector<VkFence>{kOrdinary, kOrdinary2});
        // On another device none of them is an event.
        const EventTable::Split other = table.split(kOtherDevice, list, 4);
        CHECK(other.events.empty());
        CHECK(other.ordinary.size() == 4);
    }
}

TEST_CASE("reset_unsignals_an_event_for_good")
{
    // vkResetFences on an event fence: unsignalled, and not set again by later refreshes (the event happened once).
    EventTable table;
    table.add(kDevice, kEvent, 0);
    table.on_refresh(kRefresh);
    REQUIRE(table.is_set(kDevice, kEvent));
    table.reset(kDevice, kEvent);
    CHECK_FALSE(table.is_set(kDevice, kEvent));
    table.on_refresh(2 * kRefresh);
    CHECK_FALSE(table.is_set(kDevice, kEvent));
    CHECK_FALSE(table.pending());
}

TEST_CASE("destroy_removes_event")
{
    EventTable table;
    table.add(kDevice, kEvent, 0);
    table.add(kDevice, kEvent2, 0);
    table.add(kOtherDevice, kEvent, 0);
    REQUIRE(table.size() == 3);

    CHECK(table.remove(kDevice, kEvent));
    CHECK_FALSE(table.remove(kDevice, kEvent)); // gone: an ordinary fence from now on
    CHECK_FALSE(table.is_event(kDevice, kEvent));
    CHECK(table.is_event(kOtherDevice, kEvent));
    const EventTable::Split split = table.split(kDevice, &kEvent, 1);
    CHECK(split.events.empty());
    CHECK(split.ordinary == std::vector<VkFence>{kEvent});

    // vkDestroyDevice forgets the device's events, and only those.
    CHECK(table.remove_device(kDevice) == 1);
    CHECK(table.size() == 1);
    CHECK(table.remove(kOtherDevice, kEvent));
    CHECK(table.size() == 0);
    CHECK_FALSE(table.pending());
    CHECK(table.on_refresh(kS) == 0);
}

TEST_CASE("no_event_set_while_flips_stopped")
{
    // The frame model's refreshes feed the table, as the display loop's refresh_seen does (design 6.7 step 4). While
    // flips are stopped there are none, so no event is set: not by the "flips stopped" rule, not by any tick.
    nvdm::FencePool pool;
    nvdm::FrameModel model(nvdm::FrameModelConfig{}, &pool);
    EventTable table;
    const auto feed = [&] {
        for (const int64_t qpc : model.drain_refreshes()) {
            table.on_refresh(qpc);
        }
    };

    model.thread_started(0);
    model.on_counter(100, 0); // the baseline
    feed();
    table.add(kDevice, kEvent, 5 * kMs);
    for (int64_t t = 50 * kMs; t <= 1500 * kMs; t += 50 * kMs) {
        model.on_counter(100, 0); // the counter does not move
        model.tick(t);
        feed();
    }
    REQUIRE(model.flips_stopped());
    CHECK_FALSE(table.is_set(kDevice, kEvent));
    CHECK(table.pending());

    // The counter moves again: that refresh sets it.
    model.on_counter(101, 1510 * kMs);
    feed();
    CHECK_FALSE(model.flips_stopped());
    CHECK(table.is_set(kDevice, kEvent));
    CHECK(*table.set_at(kDevice, kEvent) == 1510 * kMs);
}
