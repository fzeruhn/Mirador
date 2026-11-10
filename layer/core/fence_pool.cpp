// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "fence_pool.hpp"

#include <stdexcept>
#include <string>
#include <utility>

namespace nvdm {

FencePool::FencePool(uint32_t cap, std::function<bool(FenceSlot)> is_signalled)
    : cap_(cap), is_signalled_(std::move(is_signalled))
{
}

bool FencePool::valid(FenceSlot slot) const
{
    return slot != kNoFence && slot < slots_.size();
}

FenceSlot FencePool::hand_out(FenceSlot slot)
{
    Slot& s = slots_[slot];
    s.state = FenceState::Taken;
    s.seen = false; // the caller resets it before its next submit
    return slot;
}

std::optional<FenceSlot> FencePool::take()
{
    // 1. The pool.
    for (FenceSlot slot = 0; slot < slots_.size(); ++slot) {
        if (slots_[slot].state == FenceState::InPool && !slots_[slot].waited_on) {
            return hand_out(slot);
        }
    }
    // 2. A left-over one whose status says signalled, the oldest first.
    std::optional<FenceSlot> best;
    for (FenceSlot slot = 0; slot < slots_.size(); ++slot) {
        const Slot& s = slots_[slot];
        if (s.state != FenceState::LeftOver || s.waited_on) {
            continue;
        }
        if (best && slots_[*best].left_over_order < s.left_over_order) {
            continue;
        }
        if (is_signalled_ && is_signalled_(slot)) {
            best = slot;
        }
    }
    if (best) {
        return hand_out(*best);
    }
    // 3. A new one below the cap.
    if (slots_.size() < cap_) {
        slots_.push_back(Slot{});
        return static_cast<FenceSlot>(slots_.size() - 1);
    }
    return std::nullopt;
}

std::optional<FenceSlot> FencePool::oldest_left_over() const
{
    std::optional<FenceSlot> best;
    for (FenceSlot slot = 0; slot < slots_.size(); ++slot) {
        const Slot& s = slots_[slot];
        if (s.state == FenceState::LeftOver && !s.waited_on &&
            (!best || s.left_over_order < slots_[*best].left_over_order)) {
            best = slot;
        }
    }
    return best;
}

void FencePool::give_back(FenceSlot slot)
{
    if (valid(slot) && slots_[slot].state == FenceState::Taken) {
        slots_[slot].state = FenceState::InPool;
        slots_[slot].seen = false;
    }
}

void FencePool::attach(FenceSlot slot)
{
    if (slot == kNoFence) {
        return;
    }
    if (!valid(slot) || slots_[slot].state != FenceState::Taken) {
        throw std::logic_error("invariant 2: fence " + std::to_string(slot) + " attached without take()");
    }
    slots_[slot].state = FenceState::Attached;
}

void FencePool::waiting(FenceSlot slot, bool on)
{
    if (valid(slot)) {
        slots_[slot].waited_on = on;
    }
}

void FencePool::seen_signalled(FenceSlot slot)
{
    if (!valid(slot)) {
        return;
    }
    Slot& s = slots_[slot];
    if (!s.waited_on) {
        return; // stale: the slot may have been handed out again since whatever saw it signalled (see the header)
    }
    if (s.state == FenceState::Attached) {
        s.seen = true;
    } else if (s.state == FenceState::LeftOver) {
        s.state = FenceState::InPool;
        s.seen = true;
    }
}

void FencePool::frame_ended(FenceSlot slot)
{
    if (!valid(slot) || slots_[slot].state != FenceState::Attached) {
        return;
    }
    Slot& s = slots_[slot];
    if (s.seen) {
        s.state = FenceState::InPool;
    } else {
        s.state = FenceState::LeftOver;
        s.left_over_order = ++left_over_count_;
    }
}

size_t FencePool::created() const
{
    return slots_.size();
}

FenceState FencePool::state(FenceSlot slot) const
{
    if (!valid(slot)) {
        throw std::out_of_range("fence slot " + std::to_string(slot) + " was never created");
    }
    return slots_[slot].state;
}

} // namespace nvdm
