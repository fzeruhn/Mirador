// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/event_table.hpp"

#include <algorithm>

namespace dvl {

void EventTable::add(VkDevice device, VkFence fence, int64_t registered_ns)
{
    Event e;
    e.registered_ns = registered_ns;
    events_[Key{device, fence}] = e;
}

bool EventTable::remove(VkDevice device, VkFence fence)
{
    return events_.erase(Key{device, fence}) != 0;
}

size_t EventTable::remove_device(VkDevice device)
{
    size_t n = 0;
    for (auto it = events_.begin(); it != events_.end();) {
        if (it->first.first == device) {
            it = events_.erase(it);
            ++n;
        } else {
            ++it;
        }
    }
    return n;
}

std::vector<VkFence> EventTable::fences_of(VkDevice device) const
{
    std::vector<VkFence> out;
    for (const auto& entry : events_) {
        if (entry.first.first == device) {
            out.push_back(entry.first.second);
        }
    }
    return out;
}

bool EventTable::is_event(VkDevice device, VkFence fence) const
{
    return events_.count(Key{device, fence}) != 0;
}

bool EventTable::is_set(VkDevice device, VkFence fence) const
{
    const auto it = events_.find(Key{device, fence});
    return it != events_.end() && it->second.set;
}

std::optional<int64_t> EventTable::set_at(VkDevice device, VkFence fence) const
{
    const auto it = events_.find(Key{device, fence});
    if (it == events_.end() || !it->second.set) {
        return std::nullopt;
    }
    return it->second.set_ns;
}

void EventTable::reset(VkDevice device, VkFence fence)
{
    const auto it = events_.find(Key{device, fence});
    if (it != events_.end()) {
        it->second.set = false;
    }
}

size_t EventTable::on_refresh(int64_t qpc_ns)
{
    size_t n = 0;
    for (auto& entry : events_) {
        Event& e = entry.second;
        if (!e.fired && e.registered_ns < qpc_ns) {
            e.fired = true;
            e.set = true;
            e.set_ns = qpc_ns;
            ++n;
        }
    }
    return n;
}

bool EventTable::pending() const
{
    return std::any_of(events_.begin(), events_.end(), [](const auto& entry) { return !entry.second.fired; });
}

size_t EventTable::size() const
{
    return events_.size();
}

EventTable::Split EventTable::split(VkDevice device, const VkFence* fences, uint32_t count) const
{
    Split out;
    for (uint32_t i = 0; i < count; ++i) {
        (is_event(device, fences[i]) ? out.events : out.ordinary).push_back(fences[i]);
    }
    return out;
}

namespace {

// The events' state now: whether the wait is satisfied by them, and the refresh time that did it.
struct EventState {
    bool done = false;
    std::optional<int64_t> set_ns;
};

EventState look(const EventTable& table, VkDevice device, const std::vector<VkFence>& events, bool wait_all)
{
    EventState s;
    if (wait_all) {
        int64_t latest = INT64_MIN;
        for (const VkFence f : events) {
            const std::optional<int64_t> at = table.set_at(device, f);
            if (!at) {
                return s;
            }
            latest = std::max(latest, *at);
        }
        s.done = true;
        if (!events.empty()) {
            s.set_ns = latest;
        }
        return s;
    }
    for (const VkFence f : events) {
        if (const std::optional<int64_t> at = table.set_at(device, f)) {
            s.done = true;
            s.set_ns = s.set_ns ? std::min(*s.set_ns, *at) : *at;
        }
    }
    return s;
}

// The time left before `deadline_ns`, as a Vulkan timeout; UINT64_MAX for none.
uint64_t left_until(int64_t deadline_ns, int64_t now_ns)
{
    if (deadline_ns == INT64_MAX) {
        return UINT64_MAX;
    }
    return deadline_ns > now_ns ? static_cast<uint64_t>(deadline_ns - now_ns) : 0;
}

} // namespace

EventWait wait_for_fences(EventTable& table, std::unique_lock<std::mutex>& lock, VkDevice device,
                          const VkFence* fences, uint32_t count, bool wait_all, uint64_t timeout_ns,
                          FenceWaitHost& host)
{
    const EventTable::Split split = table.split(device, fences, count);
    const int64_t start = host.now_ns();
    const int64_t deadline = timeout_ns >= static_cast<uint64_t>(INT64_MAX - std::max<int64_t>(start, 0))
                                 ? INT64_MAX
                                 : start + static_cast<int64_t>(timeout_ns);
    EventWait out;

    if (wait_all) {
        // The events first, under the lock; then the ordinary fences down, with what is left of the same timeout.
        for (;;) {
            const EventState s = look(table, device, split.events, true);
            if (s.done) {
                out.set_ns = s.set_ns;
                break;
            }
            if (host.now_ns() >= deadline) {
                out.result = VK_TIMEOUT;
                return out;
            }
            host.wait_change(lock, deadline);
        }
        if (split.ordinary.empty()) {
            out.result = VK_SUCCESS;
            return out;
        }
        const uint64_t left = left_until(deadline, host.now_ns());
        lock.unlock();
        const VkResult r = host.wait_down(split.ordinary, true, left);
        lock.lock();
        out.result = r;
        if (r != VK_SUCCESS) {
            out.set_ns.reset(); // the events did not end it
        }
        return out;
    }

    // Wait any: whichever kind is done first. The driver is only asked to look (timeout 0), between waits for a
    // refresh that end after kMixedPollNs at most while ordinary fences are in the list.
    for (;;) {
        const EventState s = look(table, device, split.events, false);
        if (s.done) {
            out.result = VK_SUCCESS;
            out.set_ns = s.set_ns;
            return out;
        }
        if (!split.ordinary.empty()) {
            lock.unlock();
            const VkResult r = host.wait_down(split.ordinary, false, 0);
            lock.lock();
            if (r != VK_TIMEOUT) {
                out.result = r; // VK_SUCCESS, or an error such as a lost device
                return out;
            }
        }
        const int64_t now = host.now_ns();
        if (now >= deadline) {
            out.result = VK_TIMEOUT;
            return out;
        }
        const int64_t until =
            split.ordinary.empty() ? deadline : std::min(deadline, now + std::min<int64_t>(kMixedPollNs, deadline - now));
        host.wait_change(lock, until);
    }
}

} // namespace dvl
