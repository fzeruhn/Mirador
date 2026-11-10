// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <utility>
#include <vector>

namespace dvl {

// The display events of VK_EXT_display_control (design 6.9). vkRegisterDisplayEventEXT hands out a real driver fence
// (vkCreateFence) that is never submitted; the layer answers every fence call on it from this table: it is signalled
// by the first refresh (counter increment, whether or not it showed a new frame) whose QPC time is after the event was
// registered, and by nothing else (not the "flips stopped" rule, not teardown). Pure; callers hold the display-session
// lock, which the display thread holds when it reports a refresh.
class EventTable {
public:
    // A display event registered on `device` at `registered_ns` (QPC nanoseconds, the clock of the refreshes), with its
    // fence. The same fence registered again starts over.
    void add(VkDevice device, VkFence fence, int64_t registered_ns);
    // vkDestroyFence: forgets it. True if it was an event.
    bool remove(VkDevice device, VkFence fence);
    // vkDestroyDevice: forgets the device's events; how many there were.
    size_t remove_device(VkDevice device);
    // The device's event fences.
    std::vector<VkFence> fences_of(VkDevice device) const;

    bool is_event(VkDevice device, VkFence fence) const;
    // Signalled: a refresh after its registration was seen, and no vkResetFences since.
    bool is_set(VkDevice device, VkFence fence) const;
    // When it was set: the QPC time of the refresh that set it. nullopt while it is not.
    std::optional<int64_t> set_at(VkDevice device, VkFence fence) const;
    // vkResetFences: unsignalled again, for good: the event happened once, and no later refresh sets it again.
    void reset(VkDevice device, VkFence fence);

    // The display thread saw a counter increment whose QPC time is `qpc_ns` (design 6.7 step 4): every event
    // registered strictly before then that has not fired yet is set, with that time. How many were.
    size_t on_refresh(int64_t qpc_ns);
    // An event is registered that has not fired yet: the display thread then watches each refresh closely (design
    // 6.7, Idle).
    bool pending() const;
    size_t size() const;

    // A fence list as vkWaitForFences gets it: the table's events of `device`, and the rest, each in the list's order.
    struct Split {
        std::vector<VkFence> events;
        std::vector<VkFence> ordinary;
    };
    Split split(VkDevice device, const VkFence* fences, uint32_t count) const;

private:
    struct Event {
        int64_t registered_ns = 0;
        bool fired = false; // a refresh set it once
        bool set = false;   // signalled now (fired, and not reset since)
        int64_t set_ns = 0;
    };
    using Key = std::pair<VkDevice, VkFence>;
    std::map<Key, Event> events_;
};

// What a vkWaitForFences that names display events needs besides the table. The layer gives the device's call down,
// the session's clock and the condition variable the display thread notifies at each refresh; tests give fakes.
class FenceWaitHost {
public:
    virtual ~FenceWaitHost() = default;
    // Now, on the refreshes' clock.
    virtual int64_t now_ns() = 0;
    // vkWaitForFences down, on ordinary fences only. Called with the lock let go (design invariant 7).
    virtual VkResult wait_down(const std::vector<VkFence>& fences, bool wait_all, uint64_t timeout_ns) = 0;
    // Called holding `lock`: waits until the table may have changed (a refresh) or until `until_ns`, whichever comes
    // first, letting go of the lock meanwhile, as a condition variable's wait does. May return early.
    virtual void wait_change(std::unique_lock<std::mutex>& lock, int64_t until_ns) = 0;
};

// How a wait on a list with display events ended.
struct EventWait {
    VkResult result = VK_TIMEOUT;
    // The QPC time of the refresh that ended the wait, when the events ended it: for the stats' display-event wake
    // time (design 6.8). With "wait all", the latest of the events' refreshes; with "wait any", the earliest.
    std::optional<int64_t> set_ns;
};

// While a "wait any" mixes events and ordinary fences, the ordinary ones are looked at this often: the driver cannot
// wait for both kinds at once, and a wait down must not sleep through the refresh an event is waiting for.
inline constexpr int64_t kMixedPollNs = 1'000'000;

// vkWaitForFences on a list that names display events (design 6.9): the events answered from `table`, any ordinary
// fences passed down alone; "wait all" and "wait any" both kept, and the caller's timeout honoured as one budget for
// both kinds (VK_TIMEOUT once it has passed, at once for 0, unless the fences are done). An error from the wait down is
// returned as it is. The caller holds `lock` (the display-session lock, which guards `table`) and gets it back held;
// it is let go during every wait. Never spins: each pass waits for a refresh, the driver, or kMixedPollNs.
EventWait wait_for_fences(EventTable& table, std::unique_lock<std::mutex>& lock, VkDevice device,
                          const VkFence* fences, uint32_t count, bool wait_all, uint64_t timeout_ns,
                          FenceWaitHost& host);

} // namespace dvl
