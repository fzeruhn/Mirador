// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The per-device frame-fence pool (design 6.5, invariant 2). Slots stand for VkFences; whether one is signalled comes
// from the injected function, scripted here through a set.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <functional>
#include <optional>
#include <set>
#include <stdexcept>

#include "core/fence_pool.hpp"

namespace {

using nvdm::FencePool;
using nvdm::FenceSlot;
using nvdm::FenceState;

struct Signals {
    std::set<FenceSlot> on;
    std::function<bool(FenceSlot)> fn()
    {
        return [this](FenceSlot slot) { return on.count(slot) > 0; };
    }
};

// take() that must succeed.
FenceSlot must_take(FencePool& pool)
{
    std::optional<FenceSlot> slot = pool.take();
    REQUIRE(slot.has_value());
    return *slot;
}

} // namespace

TEST_CASE("take_prefers_pool_then_signalled_left_over_then_new")
{
    Signals sig;
    FencePool pool(16, sig.fn());

    const FenceSlot a = must_take(pool);
    const FenceSlot b = must_take(pool);
    REQUIRE(a != b);
    REQUIRE(pool.created() == 2);
    pool.attach(a);
    pool.attach(b);
    REQUIRE(pool.state(a) == FenceState::Attached);

    // a was seen signalled (by a wait, marked as such) before its frame ended: back to the pool. b was not: left over.
    pool.waiting(a, true);
    pool.seen_signalled(a);
    pool.waiting(a, false);
    pool.frame_ended(a);
    pool.frame_ended(b);
    REQUIRE(pool.state(a) == FenceState::InPool);
    REQUIRE(pool.state(b) == FenceState::LeftOver);
    sig.on.insert(b); // b's vkGetFenceStatus now says signalled

    CHECK(must_take(pool) == a);   // the pool first, though b could be reclaimed
    CHECK(must_take(pool) == b);   // then the signalled left-over one
    const FenceSlot c = must_take(pool); // then a new one
    CHECK(c != a);
    CHECK(c != b);
    CHECK(pool.created() == 3);
    CHECK(pool.state(c) == FenceState::Taken);
}

TEST_CASE("left_over_never_reused_unsignalled")
{
    Signals sig;
    FencePool pool(16, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.attach(a);
    pool.frame_ended(a); // never seen signalled
    REQUIRE(pool.state(a) == FenceState::LeftOver);
    REQUIRE(pool.oldest_left_over() == a);

    const FenceSlot b = must_take(pool);
    CHECK(b != a); // a new one rather than the unsignalled left-over
    CHECK(pool.state(a) == FenceState::LeftOver);

    SECTION("reclaimed once its status says signalled")
    {
        sig.on.insert(a);
        CHECK(must_take(pool) == a);
    }
    SECTION("or back in the pool once seen signalled (the wait on the oldest left-over one)")
    {
        pool.waiting(a, true);
        pool.seen_signalled(a);
        pool.waiting(a, false);
        CHECK(pool.state(a) == FenceState::InPool);
        CHECK_FALSE(pool.oldest_left_over().has_value());
        CHECK(must_take(pool) == a);
    }
    SECTION("the oldest left-over is the one that became left over first")
    {
        pool.attach(b);
        pool.frame_ended(b);
        CHECK(pool.oldest_left_over() == a);
    }
}

TEST_CASE("waited_on_fence_not_reclaimed")
{
    Signals sig;
    FencePool pool(16, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.attach(a);
    pool.waiting(a, true); // the display thread waits on it
    pool.frame_ended(a);   // its frame was retired meanwhile
    sig.on.insert(a);

    CHECK(must_take(pool) != a);                     // signalled, but waited on
    CHECK_FALSE(pool.oldest_left_over().has_value()); // not offered for the fallback wait either

    SECTION("seen signalled while waited on: in the pool, still not handed out")
    {
        pool.seen_signalled(a);
        CHECK(pool.state(a) == FenceState::InPool);
        CHECK(must_take(pool) != a);
        pool.waiting(a, false);
        CHECK(must_take(pool) == a);
    }
    SECTION("free to reclaim once the wait is over")
    {
        pool.waiting(a, false);
        CHECK(must_take(pool) == a);
    }
}

TEST_CASE("seen_signalled_counts_only_while_waited")
{
    Signals sig;
    FencePool pool(16, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.attach(a);

    pool.seen_signalled(a); // not marked waited: a stale call, ignored
    pool.frame_ended(a);
    CHECK(pool.state(a) == FenceState::LeftOver);
    pool.seen_signalled(a); // left over and not marked waited: ignored too
    CHECK(pool.state(a) == FenceState::LeftOver);
    CHECK(pool.take() != a); // unsignalled, so not reclaimed either

    pool.waiting(a, true); // a wait that sees it signalled, in its lock hold
    pool.seen_signalled(a);
    pool.waiting(a, false);
    CHECK(pool.state(a) == FenceState::InPool);
    CHECK(pool.take() == a);
}

TEST_CASE("cap_is_16")
{
    FencePool pool; // default cap and a status that never says signalled
    std::set<FenceSlot> seen;
    for (int i = 0; i < 16; ++i) {
        const FenceSlot slot = must_take(pool);
        CHECK(seen.insert(slot).second);
        pool.attach(slot);
        pool.frame_ended(slot); // left over, unsignalled
    }
    CHECK(pool.created() == 16);
    CHECK_FALSE(pool.take().has_value()); // the 17th
    CHECK(pool.created() == 16);
    CHECK(pool.oldest_left_over() == FenceSlot{0});
}

TEST_CASE("fence pool: attach needs a slot from take(), and kNoFence is ignored")
{
    FencePool pool;
    const FenceSlot a = must_take(pool);
    pool.attach(a);
    CHECK_THROWS_AS(pool.attach(a), std::logic_error); // attached twice
    pool.frame_ended(a);
    CHECK_THROWS_AS(pool.attach(a), std::logic_error); // left over, not taken
    CHECK_THROWS_AS(pool.attach(FenceSlot{7}), std::logic_error); // never created

    pool.attach(nvdm::kNoFence);
    pool.frame_ended(nvdm::kNoFence);
    pool.seen_signalled(nvdm::kNoFence);
    pool.waiting(nvdm::kNoFence, true);
    CHECK(pool.created() == 1);
}

TEST_CASE("give_back_returns_a_fence_whose_submit_failed")
{
    // A fence taken for a present whose submit then failed was never submitted: it goes back to the pool, so a slot of
    // the 16 is not lost. The caller resets every fence it takes that is not new, so it is fit to use.
    Signals sig;
    FencePool pool(2, sig.fn());
    const FenceSlot a = must_take(pool);
    pool.give_back(a);
    CHECK(pool.state(a) == FenceState::InPool);
    CHECK(must_take(pool) == a);
    const FenceSlot b = must_take(pool);
    pool.give_back(b);
    CHECK(must_take(pool) == b);
    CHECK(pool.created() == 2);
    CHECK_FALSE(pool.take().has_value()); // the cap, and nothing was lost

    // Only a taken fence can be given back; anything else is left as it is.
    pool.attach(a);
    pool.give_back(a);
    CHECK(pool.state(a) == FenceState::Attached);
    pool.give_back(nvdm::kNoFence);
    pool.give_back(7);
}
