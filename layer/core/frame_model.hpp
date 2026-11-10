// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "fence_pool.hpp"

namespace nvdm {

// A back-buffer's NVAPI state (design 6.5). It lives in the display session: it outlasts frames, swapchains and
// devices.
enum class BufferState {
    Idle,
    Presented, // given to NVAPI, not yet flipped (credited)
    OnScreen,
    Reserved,  // a Present whose outcome is unknown; NVAPI might still flip to it
};

// A frame's state (design 6.5), per device.
enum class FrameState { Acquired, Queued, Presented, OnScreen };

// The six ways a frame ends (design 6.5).
enum class FrameEnd {
    Replaced,    // it was on screen and a later frame was credited
    Retired,     // it was queued when a newer frame was Presented instead
    Dropped,     // it was queued when flips stopped, acquire's bound fired, or its swapchain was destroyed
    Failed,      // its Present returned non-zero
    Unconfirmed, // its Present was never credited (three increments), or acquire took its back-buffer
    Destroyed,   // its device went away
};

using SwapchainGen = uint64_t;
using FrameSeq = uint64_t;

struct FrameModelConfig {
    uint32_t buffers = 3;
    int64_t refresh_ns = 11'111'111;
    int64_t bound_ns = 600'000'000;   // acquire's bound (design 6.6)
    int64_t stopped_ns = 600'000'000; // "flips stopped" (design 6.7 step 5); keep it <= bound_ns (see acquire())
    uint32_t unconfirmed_after = 3;   // increments without a credit (design 6.7 step 4)
};

struct Acquired {
    uint32_t buffer;
    FrameSeq seq;
    enum class Via {
        Free,          // an idle back-buffer no live frame holds
        DroppedQueued, // after the bound: the oldest queued frame's, that frame dropped
        Reserved,      // after the bound: the reserved one, made idle
        Presented,     // after the bound, flips stopped: the presented one, its frame unconfirmed
    } via;
};

// The frame the display thread should Present next. The caller maps `buffer` to the session's back-buffer tag.
struct Candidate {
    FrameSeq seq;
    uint32_t buffer;
    FenceSlot fence; // kNoFence: the present was submitted with vkQueueWaitIdle instead, its GPU work is done
};

// When a frame reached each stage of its presentation (design 6.11), on the model's timeline (QPC nanoseconds); 0 for a
// stage it has not reached.
struct FrameTimes {
    // Its GPU work seen done: its fence seen signalled, which the display thread does in the lock hold that starts its
    // Present; or, for a frame with no fence (the present waited for its queue to go idle), when it was queued.
    int64_t queue_done_ns = 0;
    int64_t dequeued_ns = 0;  // its NVAPI Present started
    int64_t on_screen_ns = 0; // the refresh that put it on screen: the stats' QPC time of the read that credited it
};

// A frame whose times are final: it went on screen, or it ended without (design 6.11).
struct FrameReport {
    SwapchainGen gen;
    FrameSeq seq;
    FrameTimes times;
};

// The frame model of design 6.5-6.8: three back-buffers, the frames of the current device, the Present outstanding
// with NVAPI, refresh crediting and the acquire bound. Pure and single-threaded: no Vulkan, no NVAPI, no clock of its
// own. Every call is made under DisplaySession::lock(); times are QPC nanoseconds (Clock::now_ns,
// RefreshStats::qpc_ns).
//
// Design rules it keeps, by call:
// - acquire(): invariant 1 and design 6.6 (the bound and the order of fallbacks after it).
// - queue(), candidate(), present_started(), present_returned(): design 6.6 and 6.7 step 2, invariant 4.
// - fence_waiting(), fence_seen(): invariants 2 and 5, through the fence protocol (below, at the calls).
// - on_counter(): design 6.7 step 4 (credit, unconfirmed, link drop), invariant 4.
// - tick(): design 6.7 step 5 (flips stopped).
// - completed_present_id(): design 6.8 and invariant 3.
// - frame_times(), set_report(): design 6.11 (present timing), each queued frame reported once.
// - hold(): design 6.11 (present at a time): a frame is not shown at a flip before its target's.
// It logs, through set_log(), every event the design says is logged and only the model sees: a failed Present (at most
// once a second), an unconfirmed Present, a second reserved back-buffer and the older one made idle, two non-zero
// Presents in a row, the acquire fallbacks that take a reserved or presented back-buffer, a link drop, flips stopped
// and resumed. Callers do not log these again.
class FrameModel {
public:
    struct Counters {
        uint64_t presents = 0;    // NVAPI Presents made (present_started)
        uint64_t refreshes = 0;   // counter increments (every one, also those a poll saw together)
        uint64_t missed = 0;      // flip gap - 1 between frames shown; gaps over 1 s are idle, not missed
        uint64_t link_drops = 0;  // the counter went backwards
        uint64_t dropped = 0;     // frames ended Dropped
        uint64_t failed = 0;      // frames ended Failed
        uint64_t unconfirmed = 0; // frames ended Unconfirmed
        uint64_t reserved = 0;    // back-buffers that became reserved (with or without a live frame)
        uint64_t held = 0;        // frames held back for a target time they would otherwise have been shown before
        uint64_t target_skips = 0; // of missed, the flips before the first one a frame's target allowed (design 6.11)
    };

    explicit FrameModel(FrameModelConfig config, FencePool* pool);

    // The device's fence pool. A new device's pool is attached after device_destroyed() of the old one.
    void set_pool(FencePool* pool);
    // Changes whenever the pool is replaced (set_pool) or let go (device_destroyed): a slot read before and used after
    // a wait outside the lock names a fence of the same pool only if this has not changed (the fence protocol below).
    uint64_t pool_serial() const;
    // Where the model's log lines go (no-op by default). Called under the lock.
    void set_log(std::function<void(const std::string&)> log);
    // Where the frames' final times go (none by default), called under the lock from inside the model call that made
    // them final: once for every frame that was queued, when it goes on screen (credited) or ends without having been
    // on screen (retired, dropped, failed, unconfirmed, destroyed). An acquired frame that ends unqueued is not
    // reported. The callback must not call the model.
    void set_report(std::function<void(const FrameReport&)> report);

    // Swapchain records. Every swapchain wraps the same back-buffers; only completed present ids are per record.
    // remove_swapchain(): its queued and acquired frames are dropped (ids not written anywhere, fences left over unless
    // seen signalled); its presented and on-screen frames stay, unlinked: their ids are never written.
    SwapchainGen add_swapchain();
    void remove_swapchain(SwapchainGen gen);

    // The oldest free back-buffer for a new frame of `gen`, or nothing: wait and retry. With no free one it waits
    // until acquire_bound_ns(); after that it never returns nothing (for a live `gen`, while each swapchain holds at
    // most one image acquired, as Vulkan allows with three images and UINT64_MAX), taking in order: the oldest queued
    // frame's back-buffer (dropped), the reserved one (made idle; the older if two are), and while flips have stopped
    // (now_ns at least stopped_ns after the last refresh, or tick() said so) the presented one (its frame unconfirmed;
    // flips are then stopped, if tick() had not said so yet, and no other Present follows until the counter moves).
    // Never the on-screen one. An unknown or removed `gen` gets nothing.
    std::optional<Acquired> acquire(SwapchainGen gen, int64_t now_ns);
    // 600 ms after the later of the last credited refresh and the outstanding Present's return time. While that
    // Present is still in its call (no return time yet) its start time stands in, or the last refresh if one was seen
    // since (three of them make it reserved), so a stuck Present does not hold acquire forever and the bound is never
    // earlier than the "flips stopped" point that lets acquire take a presented back-buffer.
    int64_t acquire_bound_ns() const;

    // vkReleaseSwapchainImagesEXT: the acquired frame `seq` goes back unpresented. It ends Dropped (it has no present id
    // yet, so none completes) and its back-buffer is free again. A frame that is not acquired is left as it is.
    void release(FrameSeq seq);

    // vkQueuePresentKHR: the acquired frame `seq` is queued with its present id (0: none) and fence, at `qpc_ns`. While
    // flips are stopped it is dropped at once, unless it is the one frame still allowed a Present (design 6.7 step 5).
    // False if `seq` is not an acquired frame (nothing queued, nothing reported).
    bool queue(FrameSeq seq, uint64_t present_id, FenceSlot fence, int64_t qpc_ns);
    // Present at a time (design 6.11): the queued frame `seq` must not be shown at a flip earlier than
    // `not_before_flip_ns` (0: no limit). A Present made after flip F lands on the flip after it, at about F + the refresh
    // period, so the frame stays out of candidate() until a flip F with F + period >= not_before_flip_ns has been seen,
    // and so does every newer queued frame (none is shown before it). F is the last flip seen, its stats time kept from
    // going stale (a time well before the one its count gives is taken as that one), so a slow or stale read only ever
    // holds a frame back less, never shows it a flip early. Before the first flip of an epoch, and while flips are
    // stopped, nothing is held. Counts the frame as held if it is not presentable when this is called. A frame that is
    // not queued is left alone.
    void hold(FrameSeq seq, int64_t not_before_flip_ns);

    // The fence protocol (design 6.5 invariants 2 and 5), for every wait on or status read of a frame fence made
    // outside the lock: the display thread's fence wait and its status checks (step 2 and 3), and the present path's
    // wait on the oldest left-over fence (invariant 2).
    //  1. Under the lock, fence_waiting(fence, true), and read pool_serial(). From then on the pool never hands the
    //     fence out (FencePool::take, oldest_left_over), even if its frame ends meanwhile.
    //  2. Let go of the lock; wait on, or read the status of, the VkFence.
    //  3. Under the lock again, in ONE lock hold, if pool_serial() is unchanged: fence_seen(fence) if it signalled, then
    //     fence_waiting(fence, false). Then re-check the frame (sequence number, state) before acting on it
    //     (invariant 5; present_started() does that for a Present).
    // fence_seen() counts only while the fence is marked waited. Outside that window a call is stale: the slot may
    // have been handed out and attached to a new frame since, or be left over again from one, and marking it seen
    // would let an unsignalled fence back into the pool (invariant 2). Such a call does nothing.
    // At most one waiter per fence at a time: the display thread waits only on queued frames' (attached) fences, the
    // present path only on left-over ones not marked waited.
    void fence_waiting(FenceSlot fence, bool on);
    void fence_seen(FenceSlot fence);

    // The newest queued frame that may be shown at the next flip, if a Present may be made now: none outstanding, not
    // waiting for the increment after a non-zero Present, and while flips are stopped, no Present made since they
    // stopped (design 6.7 steps 2 and 5). Queued frames are taken oldest first up to the first one hold() keeps back
    // (design 6.11); the newest of those is the candidate.
    std::optional<Candidate> candidate(int64_t now_ns) const;
    // Before the NVAPI call: true and the frame and its back-buffer are presented (older queued frames retired), or
    // false and nothing changed, if `seq` is not queued, is held back (itself or by an older queued frame), or a Present
    // may not be made now (invariant 5's re-check: do not call NVAPI then).
    bool present_started(FrameSeq seq, int64_t qpc_ns);
    // After the NVAPI call returned `code` at `qpc_ns`. Non-zero: the frame fails (its id completes), the back-buffer
    // is reserved, and the next Present waits for a counter increment after `qpc_ns`. Also works after the frame was
    // destroyed with its device (the back-buffer state is kept). A left-behind thread does not call it.
    void present_returned(FrameSeq seq, int code, int64_t qpc_ns);

    // Every read whose counter differs from the last one (design 6.7 step 4). The first read after construction or
    // thread_started() only sets the baseline. Lower than the last value: a link drop, not a refresh. Higher: that
    // many refreshes, at `qpc_ns` (the stats' QPC time of the last one).
    void on_counter(uint32_t counter, int64_t qpc_ns);
    // The display thread starts (a new epoch): both clocks are set to now, the next read is a new baseline, flips are
    // not stopped (design 6.7, Start).
    void thread_started(int64_t now_ns);
    // Flips stopped: no refresh for stopped_ns. Queued frames are then dropped except the one a Present is still
    // allowed for (design 6.7 step 5). The next increment ends it.
    void tick(int64_t now_ns);
    bool flips_stopped() const;
    // A frame is queued. With present_outstanding(), what the display thread's idle rule asks (design 6.7, Idle).
    bool has_queued() const;
    // A back-buffer is presented: a Present not yet credited (invariant 4).
    bool present_outstanding() const;

    // vkDestroyDevice: every live frame ends Destroyed (no id completes); back-buffers they held become free, NVAPI
    // states stay. The pool is let go (the layer destroys it next).
    void device_destroyed();

    uint64_t completed_present_id(SwapchainGen gen) const; // 0 for a removed or unknown swapchain
    BufferState buffer_state(uint32_t buffer) const;
    int64_t last_refresh_ns() const;  // the last counter increment
    int64_t last_credited_ns() const; // the last time a back-buffer went on screen
    // The QPC time of each read that saw the counter go up, since the last drain (oldest first; one entry per read,
    // the stats' time of its last refresh). For display events and stats.
    std::vector<int64_t> drain_refreshes();
    Counters counters() const;

    std::optional<FrameState> frame_state(FrameSeq seq) const; // a live frame
    std::optional<FrameEnd> frame_end(FrameSeq seq) const;     // an ended one (the last 4096 kept)
    std::optional<FrameTimes> frame_times(FrameSeq seq) const; // a live frame's times so far
    uint64_t ids_completed() const; // how many frames have had their present id completed (written or not)

    // Throws std::logic_error("invariant N: ...") if design 6.5 invariant N (1-4) does not hold.
    void check_invariants() const;

private:
    struct Buffer {
        BufferState state = BufferState::Idle;
        FrameSeq holder = 0;      // the live frame holding it, 0 for none
        FrameSeq present_seq = 0; // the frame whose Present gave it its state (presented, on screen, reserved)
        int64_t start_ns = 0;
        int64_t return_ns = 0;
        bool returned = false;
        int64_t on_screen_ns = 0;
        uint64_t uncredited = 0;
        uint64_t reserved_order = 0; // which reserved one is newer
        int64_t not_before_ns = 0;   // presented: the hold() limit of its frame and those it retired (target_skips)
        int64_t reserved_ref_ns = 0; // its Present's return time (or when it became reserved, if unknown)
        bool reserved_failed = false;
        uint64_t stamp = 0;          // when a frame last took it, for "oldest free"
    };
    struct Frame {
        uint32_t buffer = 0;
        SwapchainGen gen = 0;
        FenceSlot fence = kNoFence;
        uint64_t present_id = 0;
        FrameState state = FrameState::Acquired;
        bool id_done = false;
        FrameTimes times;
        int64_t not_before_ns = 0; // hold(): the earliest flip it may be shown at; 0 for none
    };

    std::optional<uint32_t> presented_buffer() const;
    std::optional<uint32_t> oldest_free() const;
    bool stopped_at(int64_t now_ns) const;
    bool present_allowed() const;
    std::optional<FrameSeq> newest_queued() const;
    bool shown_at_next_flip(const Frame& frame) const;
    std::optional<FrameSeq> newest_presentable() const;
    Acquired take(uint32_t buffer, SwapchainGen gen, Acquired::Via via);
    void end_frame(FrameSeq seq, FrameEnd end);
    void complete_id(Frame& frame);
    void make_idle(uint32_t buffer);
    void make_reserved(uint32_t buffer, int64_t ref_ns, bool failed);
    void credit(uint32_t buffer, int64_t qpc_ns);
    void free_older_reserved(int64_t qpc_ns);
    void enter_stopped(int64_t now_ns);
    void drop_queued_for_stop();
    void say(const std::string& line) const;
    void report(FrameSeq seq, const Frame& frame) const;

    FrameModelConfig config_;
    FencePool* pool_;
    uint64_t pool_serial_ = 0;
    std::function<void(const std::string&)> log_;
    std::function<void(const FrameReport&)> report_;
    std::vector<Buffer> buffers_;
    std::map<FrameSeq, Frame> frames_;
    std::map<FrameSeq, FrameEnd> ended_;
    std::map<SwapchainGen, uint64_t> completed_;
    mutable std::map<SwapchainGen, uint64_t> checked_completed_;
    SwapchainGen next_gen_ = 1;
    FrameSeq next_seq_ = 1;
    uint64_t stamp_ = 0;
    uint64_t reserved_order_ = 0;
    uint64_t ids_completed_ = 0;

    bool started_ = false;
    bool have_counter_ = false;
    uint32_t counter_ = 0;
    uint64_t refresh_count_ = 0; // 64-bit, continues across link drops
    int64_t last_refresh_ns_ = 0;
    int64_t last_credited_ns_ = 0;
    bool have_flip_ = false;  // a flip of this epoch has been seen since the last link drop or stop (hold())
    int64_t flip_ns_ = 0;     // the last flip seen, kept from going stale (hold())
    bool prev_credit_valid_ = false;
    int64_t prev_credit_ns_ = 0;
    uint64_t prev_credit_count_ = 0;
    std::vector<int64_t> refreshes_;

    bool wait_increment_ = false; // after a non-zero Present
    int64_t wait_after_ns_ = 0;
    bool stopped_ = false;
    bool stopped_present_used_ = false;
    bool failed_logged_ = false;
    int64_t failed_logged_ns_ = 0;

    Counters counters_;
};

} // namespace nvdm
