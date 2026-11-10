// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <vector>

namespace nvdm {

// A frame fence, by its slot in a device's pool. The layer keeps one VkFence per slot; slots are numbered 0, 1, 2, ...
// in the order they were created, so a slot equal to the old created() is new (create its VkFence) and any other one
// is a used fence (reset it before it is submitted again).
using FenceSlot = uint32_t;
constexpr FenceSlot kNoFence = UINT32_MAX;

// At most this many frame fences per device (design 6.5).
inline constexpr uint32_t kFencePoolCap = 16;

// Where a fence is (design 6.5): in the pool, taken for a present that is being submitted, attached to a frame, or
// left over (its frame ended before the fence was seen signalled).
enum class FenceState { InPool, Taken, Attached, LeftOver };

// The frame fences of one VkDevice (design 6.5, invariant 2). Pure bookkeeping; callers hold the display-session lock.
// Whether a slot's VkFence is signalled (vkGetFenceStatus) comes from `is_signalled`, which take() calls under the
// lock. A fence the display thread waits on is never handed out again until the wait is over.
//
// A wait on (or status read of) a fence outside the lock follows the fence protocol of frame_model.hpp: waiting(slot,
// true) under the lock before letting go of it; after the wait, seen_signalled(slot) if it signalled and then
// waiting(slot, false), in one lock hold. seen_signalled() counts only while the slot is marked waited; any other call
// is stale (the slot may have been handed out and attached to a new frame since) and does nothing.
class FencePool {
public:
    explicit FencePool(uint32_t cap = kFencePoolCap,
                       std::function<bool(FenceSlot)> is_signalled = [](FenceSlot) { return false; });

    // A fence for the next present (invariant 2): one from the pool; else the oldest left-over one whose status says
    // signalled; else a new one below the cap; else nothing (the caller then waits on oldest_left_over(), outside the
    // lock). Never one the display thread is waiting on. The slot is Taken until attach().
    std::optional<FenceSlot> take();
    // The left-over fence that became left over first, for the caller to wait on; never one being waited on.
    std::optional<FenceSlot> oldest_left_over() const;
    // A fence take() handed out that was never submitted (the present's submit failed, or its VkFence could not be
    // made or reset): Taken -> InPool, so the slot is not lost. Any other slot, or kNoFence, is left as it is. The
    // caller resets every fence it takes that is not new before submitting it, so one given back is fit to use.
    void give_back(FenceSlot slot);

    // The frame model calls these. kNoFence is ignored by all of them.
    void attach(FenceSlot slot);              // Taken -> Attached; throws std::logic_error for any other slot
    void waiting(FenceSlot slot, bool on);    // a wait on it outside the lock begins (on) or is over (off)
    void seen_signalled(FenceSlot slot);      // only while waited on: Attached: remembered; LeftOver: back in the pool
    void frame_ended(FenceSlot slot);         // Attached -> InPool if seen signalled, else LeftOver

    size_t created() const;
    FenceState state(FenceSlot slot) const;   // slot < created()

private:
    struct Slot {
        FenceState state = FenceState::Taken;
        bool seen = false;
        bool waited_on = false;
        uint64_t left_over_order = 0;
    };
    bool valid(FenceSlot slot) const;
    FenceSlot hand_out(FenceSlot slot);

    uint32_t cap_;
    std::function<bool(FenceSlot)> is_signalled_;
    std::vector<Slot> slots_;
    uint64_t left_over_count_ = 0;
};

} // namespace nvdm
