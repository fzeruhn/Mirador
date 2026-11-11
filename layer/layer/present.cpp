// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// Acquire, present and present-wait on the layer's swapchain (design 6.5, 6.6, 6.8), and what a present asks of the
// present timing (design 6.11). Every decision is the frame model's, made under the display-session lock; every wait
// is bounded and made on a condition variable (never a spin); the GPU work (the acquire signal on the private queue,
// the present's fence submit on the caller's queue) is done with the lock let go. Swapchains that are not the layer's
// go down unchanged.

#include <vulkan/vulkan.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "layer/dispatch.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/present_timing.hpp"
#include "layer/swapchain.hpp"

namespace dvl {
namespace {

unsigned long long ull(uint64_t value)
{
    return static_cast<unsigned long long>(value);
}

// The fence fallback's line is written at most this often: Monado presents at 90 Hz, holding its queue
// lock. The warnings' line, once per app session, says it too.
constexpr int64_t kFallbackLineEveryNs = 1'000'000'000;

// A present took the fence fallback: counted, and its line written if none was in the last second.
void note_fence_fallback(Presenter& p)
{
    const uint64_t total = ++p.fence_fallbacks;
    const int64_t now = p.clock.now_ns();
    int64_t last = p.fence_fallback_line_ns.load();
    if (last != Presenter::kNever && now - last < kFallbackLineEveryNs) {
        return;
    }
    if (!p.fence_fallback_line_ns.compare_exchange_strong(last, now)) {
        return; // another present wrote it just now
    }
    log(Level::Warn, "vkQueuePresentKHR: no frame fence was free; waiting for the queue to go idle instead (%llu "
                     "presents have fallen back so far; this line comes at most once a second)",
        ull(total));
}

// A present that returns an error: its image's acquired frame is released (dropped), as a failed acquire's is, so a
// later acquire gets the image back. Monado acquires again after a failed present and never presents that image, and
// an image left acquired would hold its back-buffer for good. Takes the lock.
void release_acquired(Presenter& p, SwapchainRecord& record, uint32_t index)
{
    Locked lk(p);
    const nvdm::FrameSeq seq = index < kImageCount ? record.acquired[index] : 0;
    if (seq != 0) {
        record.acquired[index] = 0;
        p.model.release(seq);
        p.changed.notify_all();
    }
}

// The warnings' look after an acquire or a present: the model's counters and stopped flips, and the 2 s rule.
void note_state_locked(Presenter& p, int64_t now_ns)
{
    p.warnings.on_counters(p.model.counters(), p.model.flips_stopped());
    p.warnings.check(now_ns);
}

// --- Acquire (design 6.6) ------------------------------------------------------------------------------------------

VkResult acquire_ours(const std::shared_ptr<SwapchainRecord>& record, uint64_t timeout, VkSemaphore semaphore,
                      VkFence fence, uint32_t* index)
{
    Presenter& p = presenter();
    DeviceData& d = *record->device;
    const int64_t deadline = deadline_after(p.clock, timeout);
    nvdm::Acquired got{};
    {
        Locked lk(p);
        for (;;) {
            if (!record->alive) {
                // Destroyed during the acquire: an application error (the swapchain is externally synchronised).
                // Never out-of-date (design 6.6: Monado answers that with a recreate loop).
                p.pending.add(Level::Error, "vkAcquireNextImageKHR: the swapchain was destroyed during the call");
                return VK_ERROR_UNKNOWN;
            }
            const int64_t now = p.clock.now_ns();
            if (const std::optional<nvdm::Acquired> a = p.model.acquire(record->gen, now)) {
                got = *a;
                break;
            }
            if (timeout == 0) {
                return VK_NOT_READY;
            }
            if (now >= deadline) {
                return VK_TIMEOUT;
            }
            // No free back-buffer: wait for one, but no longer than the bound, after which the model always finds one
            // for an infinite timeout (design 6.6). Past the bound with nothing to take (a removed generation, more
            // images held than Vulkan allows): wait a refresh period for a change instead of spinning.
            int64_t until = std::min(deadline, p.model.acquire_bound_ns());
            if (until <= now) {
                until = std::min(deadline, now + p.refresh_ns);
            }
            p.wait_changed(lk.hold, until);
        }
        record->acquired[got.buffer] = got.seq;
        p.note(nvdm::TraceKind::Acquire, got.seq, got.buffer);
        if (got.via == nvdm::Acquired::Via::DroppedQueued) {
            char line[128];
            std::snprintf(line, sizeof(line), "acquire bound: took back-buffer %u from a queued frame, which is dropped",
                          got.buffer);
            p.pending.add(Level::Info, line);
        }
        note_state_locked(p, p.clock.now_ns());
    }
    *index = got.buffer;
    if (semaphore == VK_NULL_HANDLE && fence == VK_NULL_HANDLE) {
        return VK_SUCCESS;
    }
    // One empty submit on the private queue, under its mutex, with the session lock let go (design 6.6, invariant 7).
    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.signalSemaphoreCount = semaphore != VK_NULL_HANDLE ? 1 : 0;
    submit.pSignalSemaphores = &semaphore;
    VkResult r = VK_ERROR_EXTENSION_NOT_PRESENT;
    {
        std::lock_guard<std::mutex> queue(d.queue_mutex);
        if (d.QueueSubmit != nullptr) {
            r = d.QueueSubmit(d.private_queue, semaphore != VK_NULL_HANDLE ? 1 : 0, &submit, fence);
        }
    }
    p.note(nvdm::TraceKind::AcquireSubmitted, got.seq);
    if (r != VK_SUCCESS) {
        // A Vulkan failure the layer cannot recover from (a lost device, out of memory): logged, returned, and the
        // image is not left acquired (design 6.10).
        log(Level::Error, "vkAcquireNextImageKHR: the signal submit on the private queue returned %d",
            static_cast<int>(r));
        Locked lk(p);
        if (record->acquired[got.buffer] == got.seq) {
            record->acquired[got.buffer] = 0;
            p.model.release(got.seq);
            p.changed.notify_all();
        }
        return r;
    }
    return VK_SUCCESS;
}

// --- Present (design 6.6) ------------------------------------------------------------------------------------------

// A frame fence for the next present (design 6.5 invariant 2), ready to submit (made, or reset), or kNoFence when
// there is none: then the present is followed by vkQueueWaitIdle. Taken from the pool; else a left-over one whose
// status says signalled; else a new one below the cap; else the oldest left-over one, waited on for up to one frame
// period outside the lock (the fence protocol of frame_model.hpp), then the pool again.
nvdm::FenceSlot take_frame_fence(Presenter& p, DeviceData& d)
{
    std::optional<nvdm::FenceSlot> slot;
    std::optional<nvdm::FenceSlot> oldest;
    uint64_t serial = 0;
    {
        Locked lk(p);
        if (p.device != &d) {
            return nvdm::kNoFence; // the device does not present (an application error, refused by the caller)
        }
        slot = d.fence_pool.take();
        if (!slot) {
            oldest = d.fence_pool.oldest_left_over();
            if (oldest) {
                p.model.fence_waiting(*oldest, true);
                serial = p.model.pool_serial();
            }
        }
    }
    if (!slot && oldest) {
        const VkFence fence = d.fences[*oldest];
        const VkResult r = d.WaitForFences != nullptr
                               ? d.WaitForFences(d.handle, 1, &fence, VK_TRUE, static_cast<uint64_t>(p.refresh_ns))
                               : VK_ERROR_UNKNOWN;
        Locked lk(p);
        if (p.model.pool_serial() == serial) { // invariant 5's re-check: still the same pool
            if (r == VK_SUCCESS) {
                p.model.fence_seen(*oldest);
            }
            p.model.fence_waiting(*oldest, false);
            slot = d.fence_pool.take();
        }
    }
    if (!slot) {
        return nvdm::kNoFence;
    }
    // A slot without a VkFence yet is new (or given back before it had one): make it. Any other has been seen
    // signalled: reset it before it is submitted again.
    VkFence& fence = d.fences[*slot];
    VkResult r = VK_SUCCESS;
    if (fence == VK_NULL_HANDLE) {
        VkFenceCreateInfo info{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
        VkFence made = VK_NULL_HANDLE;
        r = d.CreateFence != nullptr ? d.CreateFence(d.handle, &info, nullptr, &made) : VK_ERROR_EXTENSION_NOT_PRESENT;
        if (r == VK_SUCCESS) {
            fence = made;
        }
    } else {
        r = d.ResetFences != nullptr ? d.ResetFences(d.handle, 1, &fence) : VK_ERROR_EXTENSION_NOT_PRESENT;
    }
    if (r != VK_SUCCESS) {
        log(Level::Error, "vkQueuePresentKHR: %s frame fence %u returned %d", fence == VK_NULL_HANDLE ? "making"
                                                                                                    : "resetting",
            *slot, static_cast<int>(r));
        Locked lk(p);
        d.fence_pool.give_back(*slot);
        return nvdm::kNoFence;
    }
    return *slot;
}

// Design 6.11's lines, each said once per process: the first present held to its target time (VkPresentTimingInfoEXT::
// targetTime); a target taken as none (present at a time switched off, a relative one, one too far ahead), the frame
// then shown at the first refresh it can make; and a present refused because its swapchain's results queue is full (an
// application error).
std::atomic<bool> g_target_held_logged{false};
std::atomic<bool> g_target_off_logged{false};
std::atomic<bool> g_target_relative_logged{false};
std::atomic<bool> g_target_far_logged{false};
std::atomic<bool> g_queue_full_logged{false};

// Under the lock: the line for a present's target, if it is the first of its kind.
void note_target_locked(Presenter& p, const Target& target, uint64_t target_time, int64_t now_ns)
{
    char line[320];
    switch (target.use) {
    case Target::Use::Hold:
        if (!g_target_held_logged.exchange(true)) {
            std::snprintf(line, sizeof(line),
                          "vkQueuePresentKHR: presents ask for a time to be shown at; each frame is held for the first "
                          "refresh at or after its target (the first: %.2f ms from now; said once)",
                          static_cast<double>(static_cast<int64_t>(target_time) - now_ns) / 1e6);
            p.pending.add(Level::Info, line);
        }
        break;
    case Target::Use::Off:
        if (!g_target_off_logged.exchange(true)) {
            p.pending.add(Level::Warn, "vkQueuePresentKHR: a present asks for a time to be shown at, but present at a "
                                       "time is off (MIRADOR_DIRECT_MODE_NO_PRESENT_AT; the surface says so); frames are "
                                       "shown at the first refresh they can make (said once)");
        }
        break;
    case Target::Use::Relative:
        if (!g_target_relative_logged.exchange(true)) {
            p.pending.add(Level::Warn, "vkQueuePresentKHR: a present asks for a relative time to be shown at "
                                       "(PRESENT_AT_RELATIVE_TIME), which the surface does not offer; such frames are "
                                       "shown at the first refresh they can make (said once)");
        }
        break;
    case Target::Use::TooFar:
        if (!g_target_far_logged.exchange(true)) {
            std::snprintf(line, sizeof(line),
                          "vkQueuePresentKHR: a present's target time is %.1f ms ahead, more than the %.0f ms held for "
                          "(a wrong clock?); such frames are shown at the first refresh they can make (said once)",
                          static_cast<double>(target_time) / 1e6 - static_cast<double>(now_ns) / 1e6,
                          static_cast<double>(kFarTargetNs) / 1e6);
            p.pending.add(Level::Warn, line);
        }
        break;
    case Target::Use::None:
        break;
    }
}

void set_results(const VkPresentInfoKHR& info, VkResult result)
{
    if (info.pResults != nullptr) {
        for (uint32_t i = 0; i < info.swapchainCount; ++i) {
            info.pResults[i] = result;
        }
    }
}

VkResult present_ours(VkQueue queue, const VkPresentInfoKHR& info, const std::shared_ptr<SwapchainRecord>& record)
{
    DeviceData& d = device_data(queue);
    if (&d != record->device) {
        log(Level::Error, "vkQueuePresentKHR: a queue of device %p presents the layer's swapchain of device %p; "
                          "refused",
            static_cast<void*>(d.handle), static_cast<void*>(record->device->handle));
        set_results(info, VK_ERROR_UNKNOWN);
        return VK_ERROR_UNKNOWN;
    }
    Presenter& p = presenter();
    const uint32_t index = info.pImageIndices[0];
    const PresentRequest request = read_present_request(info);
    const uint64_t present_id = request.present_id;
    p.note(nvdm::TraceKind::PresentEnter, present_id, index);

    // Design 6.11: a present that asks for present stages needs a free slot of the swapchain's results queue. Without
    // one it fails before anything is submitted, as the extension says: its semaphores and its image stay as they were.
    {
        Locked lk(p);
        if (!p.timings.slot_free(record->gen, request)) {
            if (!g_queue_full_logged.exchange(true)) {
                p.pending.add(Level::Warn, "vkQueuePresentKHR: the swapchain's present timing results queue is full "
                                           "(vkSetSwapchainPresentTimingQueueSizeEXT, vkGetPastPresentationTimingEXT); "
                                           "the present fails with VK_ERROR_PRESENT_TIMING_QUEUE_FULL_EXT (said once)");
            }
            set_results(info, VK_ERROR_PRESENT_TIMING_QUEUE_FULL_EXT);
            return VK_ERROR_PRESENT_TIMING_QUEUE_FULL_EXT;
        }
    }
    const nvdm::FenceSlot slot = take_frame_fence(p, d);
    const VkFence fence = slot != nvdm::kNoFence ? d.fences[slot] : VK_NULL_HANDLE;

    // One empty submit on the caller's queue (Monado holds its lock for the call): it waits on the present's
    // semaphores at ALL_COMMANDS and signals the frame fence the display thread waits on before the NVAPI Present.
    std::vector<VkPipelineStageFlags> stages(info.waitSemaphoreCount, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT);
    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.waitSemaphoreCount = info.waitSemaphoreCount;
    submit.pWaitSemaphores = info.pWaitSemaphores;
    submit.pWaitDstStageMask = stages.data();
    VkResult r = d.QueueSubmit != nullptr ? d.QueueSubmit(queue, 1, &submit, fence) : VK_ERROR_EXTENSION_NOT_PRESENT;
    if (r != VK_SUCCESS) {
        if (slot != nvdm::kNoFence) {
            Locked lk(p);
            d.fence_pool.give_back(slot); // never submitted: not lost
        }
        log(Level::Error, "vkQueuePresentKHR: the present's submit returned %d; image %u is released",
            static_cast<int>(r), index);
        release_acquired(p, *record, index);
        set_results(info, r);
        return r;
    }
    if (slot == nvdm::kNoFence) {
        // The fallback of invariant 2: the frame needs no fence, its GPU work is done when this returns.
        note_fence_fallback(p);
        r = d.QueueWaitIdle != nullptr ? d.QueueWaitIdle(queue) : VK_ERROR_EXTENSION_NOT_PRESENT;
        if (r != VK_SUCCESS) {
            log(Level::Error, "vkQueuePresentKHR: vkQueueWaitIdle returned %d; image %u is released",
                static_cast<int>(r), index);
            release_acquired(p, *record, index);
            set_results(info, r);
            return r;
        }
    }

    {
        Locked lk(p);
        const nvdm::FrameSeq seq = index < kImageCount ? record->acquired[index] : 0;
        if (seq != 0) {
            record->acquired[index] = 0;
        } else {
            char line[160];
            std::snprintf(line, sizeof(line), "vkQueuePresentKHR: image %u of swapchain %p was not acquired; nothing "
                                              "to show",
                          index, static_cast<void*>(record.get()));
            p.pending.add(Level::Warn, line);
        }
        // Queued with its present id; a frame that is not acquired (any more) leaves its submitted fence left over. Its
        // timing request first: the model may report the frame at once (dropped while flips are stopped). Its target
        // time in the same lock hold, so the display thread never sees it queued and not yet held (design 6.11).
        const int64_t now = p.clock.now_ns();
        if (seq != 0) {
            p.timings.queued(record->gen, seq, request);
        }
        const bool queued = p.model.queue(seq, present_id, slot, now);
        if (!queued && seq != 0) {
            p.timings.not_queued(record->gen, seq);
        }
        const Target target = read_target(request, p.refresh_ns, now, record->present_at);
        if (queued && target.use == Target::Use::Hold) {
            p.model.hold(seq, target.not_before_ns);
            p.note(nvdm::TraceKind::PresentTarget, seq, target.not_before_ns);
        }
        note_target_locked(p, target, request.target_time, now);
        p.note(nvdm::TraceKind::PresentQueued, present_id, static_cast<int64_t>(seq));
        p.warnings.on_present(now);
        if (slot == nvdm::kNoFence) {
            p.warnings.on_fence_fallback();
        }
        note_state_locked(p, now);
    }
    p.waiter.signal_frame_queued(); // after model.queue(), outside the lock
    p.note(nvdm::TraceKind::PresentReturn, present_id);
    set_results(info, VK_SUCCESS);
    return VK_SUCCESS;
}

// --- Present-wait (design 6.8) -------------------------------------------------------------------------------------

// vkWaitForPresentKHR and vkWaitForPresent2KHR (`command`), which wait alike.
VkResult wait_for_present_ours(const char* command, const std::shared_ptr<SwapchainRecord>& record,
                               uint64_t present_id, uint64_t timeout)
{
    Presenter& p = presenter();
    const int64_t deadline = deadline_after(p.clock, timeout);
    p.note(nvdm::TraceKind::PresentWaitEnter, present_id);
    // Monado's pacer reads when this returns (a wait ending late is its "missed frame"): pending lines are left for
    // later.
    Locked lk(p, Locked::Flush::Later);
    p.warnings.on_present_wait();
    for (;;) {
        if (!record->alive) {
            p.pending.add(Level::Error, std::string(command) + ": the swapchain was destroyed during the call");
            return VK_ERROR_UNKNOWN;
        }
        if (p.model.completed_present_id(record->gen) >= present_id) {
            p.note(nvdm::TraceKind::PresentWaitReturn, present_id, VK_SUCCESS);
            return VK_SUCCESS;
        }
        if (timeout == 0 || p.clock.now_ns() >= deadline) {
            p.note(nvdm::TraceKind::PresentWaitReturn, present_id, VK_TIMEOUT);
            return VK_TIMEOUT;
        }
        p.wait_changed(lk.hold, deadline);
    }
}

} // namespace

VKAPI_ATTR VkResult VKAPI_CALL layer_AcquireNextImageKHR(VkDevice device, VkSwapchainKHR swapchain, uint64_t timeout,
                                                         VkSemaphore semaphore, VkFence fence, uint32_t* index)
{
    return guarded("vkAcquireNextImageKHR", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(swapchain)) {
            return acquire_ours(record, timeout, semaphore, fence, index);
        }
        DeviceData& data = device_data(device);
        return call_down(data.AcquireNextImageKHR, "vkAcquireNextImageKHR", device, swapchain, timeout, semaphore,
                         fence, index);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_AcquireNextImage2KHR(VkDevice device, const VkAcquireNextImageInfoKHR* info,
                                                          uint32_t* index)
{
    return guarded("vkAcquireNextImage2KHR", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(info->swapchain)) {
            // deviceMask: one GPU, presenting its own images.
            return acquire_ours(record, info->timeout, info->semaphore, info->fence, index);
        }
        DeviceData& data = device_data(device);
        return call_down(data.AcquireNextImage2KHR, "vkAcquireNextImage2KHR", device, info, index);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_QueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* info)
{
    return guarded("vkQueuePresentKHR", [&] {
        std::shared_ptr<SwapchainRecord> ours;
        uint32_t count = 0;
        for (uint32_t i = 0; i < info->swapchainCount; ++i) {
            if (std::shared_ptr<SwapchainRecord> record = find_swapchain(info->pSwapchains[i])) {
                ours = std::move(record);
                ++count;
            }
        }
        if (count == 0) {
            DeviceData& data = device_data(queue);
            return call_down(data.QueuePresentKHR, "vkQueuePresentKHR", queue, info);
        }
        if (info->swapchainCount != 1) {
            // Design 6.6: a present that lists the layer's swapchain with another is not supported (one of design
            // 6.10's exceptions; Monado never does it). Nothing goes down: the driver must never see our handle.
            log(Level::Error, "vkQueuePresentKHR: a present of %u swapchains, %u of them the layer's; the layer's "
                              "swapchain is presented alone. Refused",
                info->swapchainCount, count);
            set_results(*info, VK_ERROR_UNKNOWN);
            return VK_ERROR_UNKNOWN;
        }
        return present_ours(queue, *info, ours);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_WaitForPresentKHR(VkDevice device, VkSwapchainKHR swapchain, uint64_t present_id,
                                                       uint64_t timeout)
{
    return guarded("vkWaitForPresentKHR", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(swapchain)) {
            return wait_for_present_ours("vkWaitForPresentKHR", record, present_id, timeout);
        }
        DeviceData& data = device_data(device);
        return call_down(data.WaitForPresentKHR, "vkWaitForPresentKHR", device, swapchain, present_id, timeout);
    });
}

VKAPI_ATTR VkResult VKAPI_CALL layer_WaitForPresent2KHR(VkDevice device, VkSwapchainKHR swapchain,
                                                        const VkPresentWait2InfoKHR* info)
{
    return guarded("vkWaitForPresent2KHR", [&] {
        if (std::shared_ptr<SwapchainRecord> record = find_swapchain(swapchain)) {
            // The ids are the VkPresentId2KHR ones, completed as present_id's are (design 6.8).
            return wait_for_present_ours("vkWaitForPresent2KHR", record, info->presentId, info->timeout);
        }
        DeviceData& data = device_data(device);
        return call_down(data.WaitForPresent2KHR, "vkWaitForPresent2KHR", device, swapchain, info);
    });
}

} // namespace dvl
