// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// VK_EXT_display_control (design 6.9), the layer's own extension, and the fence commands that answer for its display
// events. vkRegisterDisplayEventEXT hands out a real driver fence that is never submitted; vkWaitForFences,
// vkGetFenceStatus and vkResetFences answer for it from the event table (dvl::EventTable), which the display thread
// sets at the first refresh after the registration; lists that mix event and ordinary fences are split, and ordinary
// fences go down alone. vkDestroyFence forgets the event and passes the fence down. Fences of a process that has no
// display event go down without the lock.

#include <vulkan/vulkan.h>

#include <memory>
#include <mutex>
#include <vector>

#include "layer/dispatch.hpp"
#include "layer/display.hpp"
#include "layer/event_table.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/swapchain.hpp"

namespace dvl {
namespace {

// The presenter, if the process has any display event now: the fence commands' look before they take the lock.
Presenter* with_events()
{
    Presenter* const p = presenter_if_any();
    return p != nullptr && p->event_count.load() != 0 ? p : nullptr;
}

// vkWaitForFences' host for dvl::wait_for_fences: the device's call down, the session's clock, and the condition
// variable the display thread notifies at each refresh.
class LayerFenceWaitHost : public FenceWaitHost {
public:
    LayerFenceWaitHost(Presenter& p, DeviceData& d) : p_(p), d_(d) {}

    int64_t now_ns() override { return p_.clock.now_ns(); }

    VkResult wait_down(const std::vector<VkFence>& fences, bool wait_all, uint64_t timeout_ns) override
    {
        return call_down(d_.WaitForFences, "vkWaitForFences", d_.handle, static_cast<uint32_t>(fences.size()),
                         fences.data(), wait_all ? VK_TRUE : VK_FALSE, timeout_ns);
    }

    void wait_change(std::unique_lock<std::mutex>& lock, int64_t until_ns) override
    {
        p_.wait_changed(lock, until_ns);
    }

private:
    Presenter& p_;
    DeviceData& d_;
};

} // namespace

VKAPI_ATTR VkResult VKAPI_CALL layer_RegisterDisplayEventEXT(VkDevice device, VkDisplayKHR display,
                                                             const VkDisplayEventInfoEXT* info,
                                                             const VkAllocationCallbacks* alloc, VkFence* fence)
{
    return guarded("vkRegisterDisplayEventEXT", [&] {
        if (!is_our_display(display)) {
            // Design 6.1: the only error the registry allows besides out of memory. Monado never does this.
            log(Level::Error, "vkRegisterDisplayEventEXT: display %p is not the layer's", static_cast<void*>(display));
            return VK_ERROR_UNKNOWN;
        }
        if (info != nullptr && info->displayEvent != VK_DISPLAY_EVENT_TYPE_FIRST_PIXEL_OUT_EXT) {
            log(Level::Warn, "vkRegisterDisplayEventEXT: event type %d is answered as FIRST_PIXEL_OUT",
                static_cast<int>(info->displayEvent));
        }
        DeviceData& d = device_data(device);
        // A real driver fence, never submitted: the layer answers for it (design 6.9).
        VkFenceCreateInfo create{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        VkFence made = VK_NULL_HANDLE;
        const VkResult r = call_down(d.CreateFence, "vkCreateFence", device, &create, alloc, &made);
        if (r != VK_SUCCESS) {
            log(Level::Error, "vkRegisterDisplayEventEXT: vkCreateFence returned %d", static_cast<int>(r));
            return r;
        }
        Presenter& p = presenter();
        {
            Locked lk(p);
            const int64_t now = p.clock.now_ns(); // set by the first refresh after this
            p.events.add(device, made, now);
            if (p.trace) {
                p.trace->add(nvdm::TraceKind::EventRegister, now, handle_key(made));
            }
            p.event_count.store(p.events.size());
            p.warnings.on_event_registered(handle_key(made), now);
            p.warnings.check(now);
        }
        // The display thread watches the next refresh closely while an event waits.
        p.waiter.signal_frame_queued();
        *fence = made;
        return VK_SUCCESS;
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_RegisterDeviceEventEXT(VkDevice, const VkDeviceEventInfoEXT* info,
                                                            const VkAllocationCallbacks*, VkFence*)
{
    return guarded("vkRegisterDeviceEventEXT", [&] {
        // Design 6.9: not supported (the only device event, DISPLAY_HOTPLUG, has nothing to say about the headset).
        // Monado never calls it.
        log(Level::Warn, "vkRegisterDeviceEventEXT: device event %d is not supported",
            info != nullptr ? static_cast<int>(info->deviceEvent) : -1);
        return VK_ERROR_UNKNOWN;
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_DisplayPowerControlEXT(VkDevice, VkDisplayKHR display,
                                                            const VkDisplayPowerInfoEXT* info)
{
    return guarded("vkDisplayPowerControlEXT", [&] {
        if (!is_our_display(display)) {
            log(Level::Error, "vkDisplayPowerControlEXT: display %p is not the layer's", static_cast<void*>(display));
            return VK_ERROR_UNKNOWN; // design 6.1
        }
        // Success, a no-op (design 6.9): the headset's power is the board's business.
        log(Level::Debug, "vkDisplayPowerControlEXT: power state %d on the layer's display: nothing to do",
            info != nullptr ? static_cast<int>(info->powerState) : -1);
        return VK_SUCCESS;
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetSwapchainCounterEXT(VkDevice device, VkSwapchainKHR swapchain,
                                                            VkSurfaceCounterFlagBitsEXT counter, uint64_t* value)
{
    return guarded("vkGetSwapchainCounterEXT", [&] {
        if (find_swapchain(swapchain)) {
            if (counter != VK_SURFACE_COUNTER_VBLANK_BIT_EXT) {
                log(Level::Error, "vkGetSwapchainCounterEXT: counter 0x%x; the layer's surface has VBLANK only",
                    static_cast<unsigned>(counter));
                return VK_ERROR_UNKNOWN;
            }
            // The 64-bit flip count (design 6.9): every counter increment, continued across link drops.
            Presenter& p = presenter();
            Locked lk(p);
            *value = p.model.counters().refreshes;
            return VK_SUCCESS;
        }
        DeviceData& d = device_data(device);
        return call_down(d.GetSwapchainCounterEXT, "vkGetSwapchainCounterEXT", device, swapchain, counter, value);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_WaitForFences(VkDevice device, uint32_t count, const VkFence* fences,
                                                   VkBool32 wait_all, uint64_t timeout)
{
    return guarded("vkWaitForFences", [&] {
        DeviceData& d = device_data(device);
        Presenter* const p = with_events();
        if (p == nullptr) {
            return call_down(d.WaitForFences, "vkWaitForFences", device, count, fences, wait_all, timeout);
        }
        // Monado's vblank thread stamps the refresh when this returns: pending lines are left for later.
        Locked lk(*p, Locked::Flush::Later);
        const EventTable::Split split = p->events.split(device, fences, count);
        if (split.events.empty()) {
            lk.hold.unlock();
            return call_down(d.WaitForFences, "vkWaitForFences", device, count, fences, wait_all, timeout);
        }
        for (const VkFence f : split.events) {
            p->warnings.on_event_waited(handle_key(f)); // the 2 s rule: the fence calls reach the layer
        }
        LayerFenceWaitHost host(*p, d);
        const EventWait w =
            wait_for_fences(p->events, lk.hold, device, fences, count, wait_all == VK_TRUE, timeout, host);
        if (w.result == VK_SUCCESS && w.set_ns) {
            // The stats' display-event wake time: when the wait returns minus NVAPI's refresh time (design 6.8).
            const int64_t woke = p->clock.now_ns();
            p->stats.on_wake(woke - *w.set_ns);
            if (p->trace) {
                p->trace->add(nvdm::TraceKind::EventWake, woke, 0, *w.set_ns);
            }
        }
        return w.result;
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_GetFenceStatus(VkDevice device, VkFence fence)
{
    return guarded("vkGetFenceStatus", [&] {
        DeviceData& d = device_data(device);
        if (Presenter* const p = with_events()) {
            Locked lk(*p, Locked::Flush::Later); // a look for the refresh, as the wait is
            if (p->events.is_event(device, fence)) {
                p->warnings.on_event_waited(handle_key(fence));
                return p->events.is_set(device, fence) ? VK_SUCCESS : VK_NOT_READY;
            }
        }
        return call_down(d.GetFenceStatus, "vkGetFenceStatus", device, fence);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_ResetFences(VkDevice device, uint32_t count, const VkFence* fences)
{
    return guarded("vkResetFences", [&] {
        DeviceData& d = device_data(device);
        Presenter* const p = with_events();
        if (p == nullptr) {
            return call_down(d.ResetFences, "vkResetFences", device, count, fences);
        }
        EventTable::Split split;
        {
            Locked lk(*p);
            split = p->events.split(device, fences, count);
            for (const VkFence f : split.events) {
                p->events.reset(device, f);
            }
        }
        if (split.events.empty()) {
            return call_down(d.ResetFences, "vkResetFences", device, count, fences);
        }
        if (split.ordinary.empty()) {
            return VK_SUCCESS;
        }
        return call_down(d.ResetFences, "vkResetFences", device, static_cast<uint32_t>(split.ordinary.size()),
                         split.ordinary.data());
    });
}

VKAPI_ATTR void VKAPI_CALL layer_DestroyFence(VkDevice device, VkFence fence, const VkAllocationCallbacks* alloc)
{
    guarded_void("vkDestroyFence", [&] {
        if (fence == VK_NULL_HANDLE) {
            return;
        }
        DeviceData& d = device_data(device);
        if (Presenter* const p = with_events()) {
            Locked lk(*p);
            if (p->events.remove(device, fence)) {
                p->event_count.store(p->events.size());
                p->warnings.on_event_gone(handle_key(fence));
            }
        }
        // The real fence goes down either way (design 6.9).
        if (d.DestroyFence == nullptr) {
            missing_below("vkDestroyFence");
            return;
        }
        d.DestroyFence(device, fence, alloc);
    });
}

} // namespace dvl
