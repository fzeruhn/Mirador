// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "frame_model.hpp"

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <iterator>
#include <set>
#include <stdexcept>
#include <utility>

namespace nvdm {
namespace {

// Between two frames shown, a gap longer than this is idle (no app session), not missed (design 6.8).
constexpr int64_t kIdleGapNs = 1'000'000'000;
// A failed Present is logged at most this often (design 6.7 step 2).
constexpr int64_t kFailedLogNs = 1'000'000'000;
// How many ended frames frame_end() remembers, and how many refresh times wait for drain_refreshes().
constexpr size_t kEndedKept = 4096;
constexpr size_t kRefreshesKept = 4096;
// hold(): a flip's stats time more than this before the time its count gives (the last flip + increments x period) is
// stale, and the count's time stands in. NVAPI's times are within ~0.1 ms of the period grid (2026-11-02 trace).
constexpr int64_t kStaleFlipNs = 500'000;

std::string format(const char* fmt, ...)
{
    char text[256];
    va_list args;
    va_start(args, fmt);
    vsnprintf(text, sizeof(text), fmt, args);
    va_end(args);
    return text;
}

const char* name(BufferState state)
{
    switch (state) {
    case BufferState::Idle:
        return "idle";
    case BufferState::Presented:
        return "presented";
    case BufferState::OnScreen:
        return "on screen";
    case BufferState::Reserved:
        return "reserved";
    }
    return "?";
}

const char* name(FrameState state)
{
    switch (state) {
    case FrameState::Acquired:
        return "acquired";
    case FrameState::Queued:
        return "queued";
    case FrameState::Presented:
        return "presented";
    case FrameState::OnScreen:
        return "on screen";
    }
    return "?";
}

[[noreturn]] void broken(int invariant, const std::string& what)
{
    throw std::logic_error("invariant " + std::to_string(invariant) + ": " + what);
}

unsigned long long ull(uint64_t value)
{
    return static_cast<unsigned long long>(value);
}

} // namespace

FrameModel::FrameModel(FrameModelConfig config, FencePool* pool)
    : config_(config), pool_(pool), buffers_(config.buffers)
{
}

void FrameModel::set_pool(FencePool* pool)
{
    pool_ = pool;
    ++pool_serial_;
}

uint64_t FrameModel::pool_serial() const
{
    return pool_serial_;
}

void FrameModel::set_log(std::function<void(const std::string&)> log)
{
    log_ = std::move(log);
}

void FrameModel::set_report(std::function<void(const FrameReport&)> report)
{
    report_ = std::move(report);
}

void FrameModel::say(const std::string& line) const
{
    if (log_) {
        log_(line);
    }
}

void FrameModel::report(FrameSeq seq, const Frame& frame) const
{
    if (report_) {
        report_(FrameReport{frame.gen, seq, frame.times});
    }
}

// --- Swapchains --------------------------------------------------------------------------------------------------

SwapchainGen FrameModel::add_swapchain()
{
    const SwapchainGen gen = next_gen_++;
    completed_[gen] = 0;
    return gen;
}

void FrameModel::remove_swapchain(SwapchainGen gen)
{
    // The record goes first, so the ids of the frames dropped below are not written anywhere.
    if (completed_.erase(gen) == 0) {
        return;
    }
    checked_completed_.erase(gen);
    std::vector<FrameSeq> drop;
    for (const auto& [seq, frame] : frames_) {
        if (frame.gen == gen && (frame.state == FrameState::Acquired || frame.state == FrameState::Queued)) {
            drop.push_back(seq);
        }
    }
    for (const FrameSeq seq : drop) {
        end_frame(seq, FrameEnd::Dropped);
    }
}

// --- Acquire -----------------------------------------------------------------------------------------------------

std::optional<uint32_t> FrameModel::presented_buffer() const
{
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        if (buffers_[b].state == BufferState::Presented) {
            return b;
        }
    }
    return std::nullopt;
}

std::optional<uint32_t> FrameModel::oldest_free() const
{
    std::optional<uint32_t> best;
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        const Buffer& buf = buffers_[b];
        if (buf.state == BufferState::Idle && buf.holder == 0 && (!best || buf.stamp < buffers_[*best].stamp)) {
            best = b;
        }
    }
    return best;
}

bool FrameModel::stopped_at(int64_t now_ns) const
{
    return stopped_ || (started_ && now_ns - last_refresh_ns_ >= config_.stopped_ns);
}

Acquired FrameModel::take(uint32_t buffer, SwapchainGen gen, Acquired::Via via)
{
    Buffer& buf = buffers_[buffer];
    if (buf.state != BufferState::Idle || buf.holder != 0) {
        broken(1, format("back-buffer %u handed out while %s, held by frame %llu", buffer, name(buf.state),
                         ull(buf.holder)));
    }
    const FrameSeq seq = next_seq_++;
    Frame frame;
    frame.buffer = buffer;
    frame.gen = gen;
    frames_.emplace(seq, frame);
    buf.holder = seq;
    buf.stamp = ++stamp_;
    return Acquired{buffer, seq, via};
}

int64_t FrameModel::acquire_bound_ns() const
{
    int64_t ref = last_credited_ns_;
    if (const std::optional<uint32_t> p = presented_buffer()) {
        const Buffer& buf = buffers_[*p];
        ref = std::max(ref, buf.returned ? buf.return_ns : std::max(buf.start_ns, last_refresh_ns_));
    }
    return ref + config_.bound_ns;
}

std::optional<Acquired> FrameModel::acquire(SwapchainGen gen, int64_t now_ns)
{
    if (completed_.count(gen) == 0) {
        return std::nullopt;
    }
    if (const std::optional<uint32_t> b = oldest_free()) {
        return take(*b, gen, Acquired::Via::Free);
    }
    if (now_ns < acquire_bound_ns()) {
        return std::nullopt;
    }
    // After the bound (design 6.6): 1. the oldest queued frame's back-buffer.
    for (const auto& [seq, frame] : frames_) {
        if (frame.state == FrameState::Queued) {
            const uint32_t b = frame.buffer;
            end_frame(seq, FrameEnd::Dropped);
            return take(b, gen, Acquired::Via::DroppedQueued);
        }
    }
    // 2. The reserved one (the older, if two are).
    std::optional<uint32_t> reserved;
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        if (buffers_[b].state == BufferState::Reserved &&
            (!reserved || buffers_[b].reserved_order < buffers_[*reserved].reserved_order)) {
            reserved = b;
        }
    }
    if (reserved) {
        say(format("acquire bound: took reserved back-buffer %u", *reserved));
        make_idle(*reserved);
        return take(*reserved, gen, Acquired::Via::Reserved);
    }
    // 3. Only while flips have stopped: the presented one. Acquire does not wait for the display thread's tick() to
    //    notice (invariant 6); if it has not yet, flips stop here (no frame is queued to drop, step 1 found none). The
    //    Present it takes counts as the one Present of the stopped period, so none follows until the counter moves.
    if (stopped_at(now_ns)) {
        if (const std::optional<uint32_t> p = presented_buffer()) {
            if (!stopped_) {
                enter_stopped(now_ns);
            }
            if (const FrameSeq holder = buffers_[*p].holder) {
                end_frame(holder, FrameEnd::Unconfirmed);
            }
            say(format("acquire bound: took presented back-buffer %u while flips are stopped", *p));
            make_idle(*p);
            stopped_present_used_ = true;
            return take(*p, gen, Acquired::Via::Presented);
        }
    }
    return std::nullopt; // only the on-screen one is left (more images held than Vulkan allows)
}

void FrameModel::release(FrameSeq seq)
{
    const auto it = frames_.find(seq);
    if (it != frames_.end() && it->second.state == FrameState::Acquired) {
        end_frame(seq, FrameEnd::Dropped);
    }
}

// --- Present -----------------------------------------------------------------------------------------------------

bool FrameModel::present_allowed() const
{
    return !presented_buffer() && !wait_increment_ && !(stopped_ && stopped_present_used_);
}

std::optional<FrameSeq> FrameModel::newest_queued() const
{
    for (auto it = frames_.rbegin(); it != frames_.rend(); ++it) {
        if (it->second.state == FrameState::Queued) {
            return it->first;
        }
    }
    return std::nullopt;
}

bool FrameModel::queue(FrameSeq seq, uint64_t present_id, FenceSlot fence, int64_t qpc_ns)
{
    const auto it = frames_.find(seq);
    if (it == frames_.end() || it->second.state != FrameState::Acquired) {
        // Not a frame waiting for its present (its swapchain or device went meanwhile): nothing to show, and the
        // fence, submitted already, is left over.
        if (pool_ != nullptr && fence != kNoFence) {
            pool_->attach(fence);
            pool_->frame_ended(fence);
        }
        return false;
    }
    Frame& frame = it->second;
    frame.state = FrameState::Queued;
    frame.present_id = present_id;
    frame.fence = fence;
    if (fence == kNoFence) {
        frame.times.queue_done_ns = qpc_ns; // the present waited for its queue to go idle
    }
    if (pool_ != nullptr) {
        pool_->attach(fence);
    }
    if (stopped_) {
        drop_queued_for_stop();
    }
    return true;
}

void FrameModel::fence_seen(FenceSlot fence)
{
    if (pool_ != nullptr) {
        pool_->seen_signalled(fence);
    }
}

void FrameModel::fence_waiting(FenceSlot fence, bool on)
{
    if (pool_ != nullptr) {
        pool_->waiting(fence, on);
    }
}

void FrameModel::hold(FrameSeq seq, int64_t not_before_flip_ns)
{
    const auto it = frames_.find(seq);
    if (it == frames_.end() || it->second.state != FrameState::Queued) {
        return;
    }
    it->second.not_before_ns = not_before_flip_ns;
    if (!shown_at_next_flip(it->second)) {
        ++counters_.held;
    }
}

// A Present made now lands on the flip after the last one seen: at about flip_ns_ + the period, or later if that flip
// has passed unseen. So the frame may go if that flip is not before its limit.
bool FrameModel::shown_at_next_flip(const Frame& frame) const
{
    if (frame.not_before_ns == 0 || !have_flip_ || stopped_) {
        return true;
    }
    return frame.not_before_ns <= flip_ns_ + config_.refresh_ns;
}

// The newest of the queued frames, taken oldest first, before the first one that is held back.
std::optional<FrameSeq> FrameModel::newest_presentable() const
{
    std::optional<FrameSeq> newest;
    for (const auto& [seq, frame] : frames_) {
        if (frame.state != FrameState::Queued) {
            continue;
        }
        if (!shown_at_next_flip(frame)) {
            break;
        }
        newest = seq;
    }
    return newest;
}

std::optional<Candidate> FrameModel::candidate(int64_t now_ns) const
{
    if (presented_buffer() || wait_increment_ || (stopped_at(now_ns) && stopped_present_used_)) {
        return std::nullopt;
    }
    const std::optional<FrameSeq> seq = newest_presentable();
    if (!seq) {
        return std::nullopt;
    }
    const Frame& frame = frames_.at(*seq);
    return Candidate{*seq, frame.buffer, frame.fence};
}

bool FrameModel::present_started(FrameSeq seq, int64_t qpc_ns)
{
    const auto it = frames_.find(seq);
    if (!present_allowed() || it == frames_.end() || it->second.state != FrameState::Queued) {
        return false;
    }
    for (const auto& [other, frame] : frames_) {
        if (other > seq) {
            break;
        }
        if (frame.state == FrameState::Queued && !shown_at_next_flip(frame)) {
            return false; // held back, itself or by an older queued frame (design 6.11)
        }
    }
    std::vector<FrameSeq> older;
    int64_t not_before_ns = it->second.not_before_ns; // the latest limit of the frames this Present takes the place of
    for (const auto& [other, frame] : frames_) {
        if (other >= seq) {
            break;
        }
        if (frame.state == FrameState::Queued) {
            older.push_back(other);
            not_before_ns = std::max(not_before_ns, frame.not_before_ns);
        }
    }
    for (const FrameSeq other : older) {
        end_frame(other, FrameEnd::Retired);
    }
    Frame& frame = it->second;
    frame.state = FrameState::Presented;
    if (frame.times.queue_done_ns == 0) {
        frame.times.queue_done_ns = qpc_ns; // its fence was seen signalled in this lock hold (DisplayLoop)
    }
    frame.times.dequeued_ns = qpc_ns;
    make_idle(frame.buffer); // clears what an earlier Present left
    Buffer& buf = buffers_[frame.buffer];
    buf.state = BufferState::Presented;
    buf.present_seq = seq;
    buf.start_ns = qpc_ns;
    buf.not_before_ns = not_before_ns;
    ++counters_.presents;
    if (stopped_) {
        stopped_present_used_ = true;
    }
    return true;
}

void FrameModel::present_returned(FrameSeq seq, int code, int64_t qpc_ns)
{
    if (code != 0) {
        // The next Present waits for a counter increment after this one (design 6.7 step 2).
        wait_increment_ = true;
        wait_after_ns_ = qpc_ns;
    }
    std::optional<uint32_t> found;
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        const Buffer& buf = buffers_[b];
        if (buf.present_seq == seq && !buf.returned &&
            (buf.state == BufferState::Presented || buf.state == BufferState::Reserved)) {
            found = b;
        }
    }
    if (code != 0 && (!failed_logged_ || qpc_ns - failed_logged_ns_ >= kFailedLogNs)) {
        say(format("Present of frame %llu returned %d: its back-buffer is reserved (logged at most once a second)",
                   ull(seq), code));
        failed_logged_ = true;
        failed_logged_ns_ = qpc_ns;
    }
    if (!found) {
        return; // acquire took its back-buffer meanwhile (flips stopped)
    }
    Buffer& buf = buffers_[*found];
    buf.returned = true;
    buf.return_ns = qpc_ns;
    if (buf.state == BufferState::Reserved) {
        // Reserved already, after three uncredited increments: now the real return time is known.
        buf.reserved_ref_ns = std::max(buf.reserved_ref_ns, qpc_ns);
        buf.reserved_failed = buf.reserved_failed || code != 0;
        return;
    }
    if (code != 0) {
        if (const FrameSeq holder = buf.holder) {
            end_frame(holder, FrameEnd::Failed);
        }
        make_reserved(*found, qpc_ns, true);
    }
}

// --- Refreshes ---------------------------------------------------------------------------------------------------

void FrameModel::thread_started(int64_t now_ns)
{
    started_ = true;
    last_refresh_ns_ = now_ns;
    last_credited_ns_ = now_ns;
    have_counter_ = false;
    stopped_ = false;
    stopped_present_used_ = false;
    prev_credit_valid_ = false;
    have_flip_ = false;
}

void FrameModel::on_counter(uint32_t counter, int64_t qpc_ns)
{
    if (!have_counter_) {
        have_counter_ = true;
        counter_ = counter;
        return;
    }
    if (counter == counter_) {
        return;
    }
    if (counter < counter_) {
        ++counters_.link_drops;
        say(format("link drop: refresh counter went from %u to %u", counter_, counter));
        counter_ = counter;
        have_flip_ = false; // the phase may have moved
        return;
    }
    const uint32_t delta = counter - counter_;
    counter_ = counter;
    refresh_count_ += delta;
    counters_.refreshes += delta;
    last_refresh_ns_ = qpc_ns;
    // hold()'s flip: the stats' time, unless it is well before what the count gives (a stale time would hold frames a
    // flip too long). After a stop the phase may have moved: the stats' time as it is.
    if (have_flip_ && !stopped_) {
        const int64_t counted = flip_ns_ + static_cast<int64_t>(delta) * config_.refresh_ns;
        flip_ns_ = qpc_ns < counted - kStaleFlipNs ? counted : qpc_ns;
    } else {
        flip_ns_ = qpc_ns;
        have_flip_ = true;
    }
    refreshes_.push_back(qpc_ns);
    if (refreshes_.size() > kRefreshesKept) {
        refreshes_.erase(refreshes_.begin());
    }
    if (stopped_) {
        stopped_ = false;
        stopped_present_used_ = false;
        say("flips resumed");
    }
    if (const std::optional<uint32_t> p = presented_buffer()) {
        Buffer& buf = buffers_[*p];
        if (buf.returned && qpc_ns > buf.return_ns) {
            credit(*p, qpc_ns);
        } else {
            buf.uncredited += delta;
            if (buf.uncredited >= config_.unconfirmed_after) {
                if (const FrameSeq holder = buf.holder) {
                    end_frame(holder, FrameEnd::Unconfirmed);
                }
                say(format("Present of back-buffer %u not credited after %llu refreshes: reserved", *p,
                           ull(buf.uncredited)));
                make_reserved(*p, buf.returned ? buf.return_ns : qpc_ns, false);
            }
        }
    }
    if (wait_increment_ && qpc_ns > wait_after_ns_) {
        wait_increment_ = false;
    }
    free_older_reserved(qpc_ns);
}

void FrameModel::credit(uint32_t buffer, int64_t qpc_ns)
{
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        if (b == buffer) {
            continue;
        }
        if (buffers_[b].state == BufferState::OnScreen) {
            if (const FrameSeq holder = buffers_[b].holder) {
                end_frame(holder, FrameEnd::Replaced);
            }
            make_idle(b);
        } else if (buffers_[b].state == BufferState::Reserved) {
            make_idle(b);
        }
    }
    Buffer& buf = buffers_[buffer];
    buf.state = BufferState::OnScreen;
    buf.on_screen_ns = qpc_ns;
    buf.uncredited = 0;
    if (buf.holder != 0) {
        Frame& frame = frames_.at(buf.holder);
        frame.state = FrameState::OnScreen;
        frame.times.on_screen_ns = qpc_ns;
        complete_id(frame);
        report(buf.holder, frame);
    }
    last_credited_ns_ = qpc_ns;
    if (prev_credit_valid_ && qpc_ns - prev_credit_ns_ <= kIdleGapNs && refresh_count_ - prev_credit_count_ > 1) {
        const uint64_t gap = refresh_count_ - prev_credit_count_;
        counters_.missed += gap - 1;
        // Of those, the flips before the first one its target allowed (design 6.11; the target of this frame or of
        // one it took the place of): asked for, not late.
        const int64_t period = std::max<int64_t>(config_.refresh_ns, 1);
        if (buf.not_before_ns > prev_credit_ns_ + period) {
            const auto allowed = static_cast<uint64_t>((buf.not_before_ns - prev_credit_ns_ + period - 1) / period);
            counters_.target_skips += std::min(gap, allowed) - 1;
        }
    }
    prev_credit_valid_ = true;
    prev_credit_ns_ = qpc_ns;
    prev_credit_count_ = refresh_count_;
}

void FrameModel::make_idle(uint32_t buffer)
{
    Buffer& buf = buffers_[buffer];
    buf.state = BufferState::Idle;
    buf.present_seq = 0;
    buf.start_ns = 0;
    buf.return_ns = 0;
    buf.returned = false;
    buf.on_screen_ns = 0;
    buf.uncredited = 0;
    buf.reserved_order = 0;
    buf.reserved_ref_ns = 0;
    buf.reserved_failed = false;
    buf.not_before_ns = 0;
}

void FrameModel::make_reserved(uint32_t buffer, int64_t ref_ns, bool failed)
{
    std::optional<uint32_t> other;
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        if (b != buffer && buffers_[b].state == BufferState::Reserved &&
            (!other || buffers_[b].reserved_order > buffers_[*other].reserved_order)) {
            other = b;
        }
    }
    Buffer& buf = buffers_[buffer];
    buf.state = BufferState::Reserved;
    buf.uncredited = 0;
    buf.reserved_order = ++reserved_order_;
    buf.reserved_ref_ns = ref_ns;
    buf.reserved_failed = failed;
    ++counters_.reserved;
    if (other && failed && buffers_[*other].reserved_failed) {
        say(format("two non-zero Presents in a row (back-buffers %u and %u): which one NVAPI scans is unknown", *other,
                   buffer));
    }
}

void FrameModel::free_older_reserved(int64_t qpc_ns)
{
    std::optional<uint32_t> newest;
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        if (buffers_[b].state == BufferState::Reserved &&
            (!newest || buffers_[b].reserved_order > buffers_[*newest].reserved_order)) {
            newest = b;
        }
    }
    if (!newest || qpc_ns <= buffers_[*newest].reserved_ref_ns) {
        return;
    }
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        if (b != *newest && buffers_[b].state == BufferState::Reserved) {
            say(format("back-buffer %u no longer reserved: a refresh passed after back-buffer %u's Present", b,
                       *newest));
            make_idle(b);
        }
    }
}

void FrameModel::tick(int64_t now_ns)
{
    if (!started_ || stopped_ || now_ns - last_refresh_ns_ < config_.stopped_ns) {
        return;
    }
    enter_stopped(now_ns);
}

void FrameModel::enter_stopped(int64_t now_ns)
{
    stopped_ = true;
    stopped_present_used_ = presented_buffer().has_value(); // an outstanding Present stays the only one
    const long long silent_ms = static_cast<long long>((now_ns - last_refresh_ns_) / 1'000'000);
    say(format("flips stopped: no refresh for %lld ms", silent_ms));
    drop_queued_for_stop();
}

void FrameModel::drop_queued_for_stop()
{
    const std::optional<FrameSeq> keep = present_allowed() ? newest_queued() : std::nullopt;
    std::vector<FrameSeq> drop;
    for (const auto& [seq, frame] : frames_) {
        if (frame.state == FrameState::Queued && seq != keep) {
            drop.push_back(seq);
        }
    }
    for (const FrameSeq seq : drop) {
        end_frame(seq, FrameEnd::Dropped);
    }
}

bool FrameModel::flips_stopped() const
{
    return stopped_;
}

bool FrameModel::has_queued() const
{
    return newest_queued().has_value();
}

bool FrameModel::present_outstanding() const
{
    return presented_buffer().has_value();
}

// --- Frames ------------------------------------------------------------------------------------------------------

void FrameModel::complete_id(Frame& frame)
{
    if (frame.id_done) {
        broken(3, format("present id %llu completed twice", ull(frame.present_id)));
    }
    frame.id_done = true;
    ++ids_completed_;
    const auto it = completed_.find(frame.gen); // only while that swapchain is alive
    if (it != completed_.end()) {
        it->second = std::max(it->second, frame.present_id);
    }
}

void FrameModel::end_frame(FrameSeq seq, FrameEnd end)
{
    const auto it = frames_.find(seq);
    if (it == frames_.end()) {
        return;
    }
    Frame& frame = it->second;
    const bool has_id = frame.state != FrameState::Acquired; // an acquired frame has no present id yet
    switch (end) {
    case FrameEnd::Retired:
    case FrameEnd::Dropped:
    case FrameEnd::Failed:
    case FrameEnd::Unconfirmed:
        if (has_id) {
            complete_id(frame);
        }
        break;
    case FrameEnd::Replaced:  // completed when it went on screen
    case FrameEnd::Destroyed: // completes nothing
        break;
    }
    if (has_id && frame.state != FrameState::OnScreen) {
        report(seq, frame); // a frame on screen was reported when it went there
    }
    if (end == FrameEnd::Dropped) {
        ++counters_.dropped;
    } else if (end == FrameEnd::Failed) {
        ++counters_.failed;
    } else if (end == FrameEnd::Unconfirmed) {
        ++counters_.unconfirmed;
    }
    Buffer& buf = buffers_[frame.buffer];
    if (buf.holder == seq) {
        buf.holder = 0;
    }
    if (pool_ != nullptr) {
        pool_->frame_ended(frame.fence);
    }
    ended_[seq] = end;
    if (ended_.size() > kEndedKept) {
        ended_.erase(ended_.begin());
    }
    frames_.erase(it);
}

void FrameModel::device_destroyed()
{
    std::vector<FrameSeq> all;
    for (const auto& entry : frames_) {
        all.push_back(entry.first);
    }
    for (const FrameSeq seq : all) {
        end_frame(seq, FrameEnd::Destroyed);
    }
    pool_ = nullptr;
    ++pool_serial_;
}

// --- Queries -----------------------------------------------------------------------------------------------------

uint64_t FrameModel::completed_present_id(SwapchainGen gen) const
{
    const auto it = completed_.find(gen);
    return it == completed_.end() ? 0 : it->second;
}

BufferState FrameModel::buffer_state(uint32_t buffer) const
{
    return buffers_.at(buffer).state;
}

int64_t FrameModel::last_refresh_ns() const
{
    return last_refresh_ns_;
}

int64_t FrameModel::last_credited_ns() const
{
    return last_credited_ns_;
}

std::vector<int64_t> FrameModel::drain_refreshes()
{
    std::vector<int64_t> out;
    out.swap(refreshes_);
    return out;
}

FrameModel::Counters FrameModel::counters() const
{
    return counters_;
}

std::optional<FrameState> FrameModel::frame_state(FrameSeq seq) const
{
    const auto it = frames_.find(seq);
    if (it == frames_.end()) {
        return std::nullopt;
    }
    return it->second.state;
}

std::optional<FrameTimes> FrameModel::frame_times(FrameSeq seq) const
{
    const auto it = frames_.find(seq);
    if (it == frames_.end()) {
        return std::nullopt;
    }
    return it->second.times;
}

std::optional<FrameEnd> FrameModel::frame_end(FrameSeq seq) const
{
    const auto it = ended_.find(seq);
    if (it == ended_.end()) {
        return std::nullopt;
    }
    return it->second;
}

uint64_t FrameModel::ids_completed() const
{
    return ids_completed_;
}

// --- Invariants (design 6.5) ---------------------------------------------------------------------------------------

void FrameModel::check_invariants() const
{
    // 1. Back-buffers and the frames holding them agree; acquired and queued frames hold idle ones; a presented or
    //    on-screen frame's back-buffer has the same NVAPI state; a reserved one is held by no frame; one on screen.
    int on_screen = 0;
    int presented = 0;
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        const Buffer& buf = buffers_[b];
        on_screen += buf.state == BufferState::OnScreen ? 1 : 0;
        presented += buf.state == BufferState::Presented ? 1 : 0;
        if (buf.holder != 0) {
            const auto it = frames_.find(buf.holder);
            if (it == frames_.end() || it->second.buffer != b) {
                broken(1, format("back-buffer %u held by frame %llu, which does not hold it", b, ull(buf.holder)));
            }
            if (buf.state == BufferState::Reserved) {
                broken(1, format("reserved back-buffer %u held by frame %llu", b, ull(buf.holder)));
            }
        }
    }
    for (const auto& [seq, frame] : frames_) {
        if (frame.buffer >= buffers_.size()) {
            broken(1, format("frame %llu on back-buffer %u of %zu", ull(seq), frame.buffer, buffers_.size()));
        }
        const Buffer& buf = buffers_[frame.buffer];
        if (buf.holder != seq) {
            broken(1, format("frame %llu on back-buffer %u, which frame %llu holds", ull(seq), frame.buffer,
                             ull(buf.holder)));
        }
        const bool fits = (frame.state == FrameState::Acquired || frame.state == FrameState::Queued)
                              ? buf.state == BufferState::Idle
                              : buf.present_seq == seq && buf.state == (frame.state == FrameState::Presented
                                                                            ? BufferState::Presented
                                                                            : BufferState::OnScreen);
        if (!fits) {
            broken(1, format("frame %llu is %s on back-buffer %u, which is %s", ull(seq), name(frame.state),
                             frame.buffer, name(buf.state)));
        }
    }
    if (on_screen > 1) {
        broken(1, format("%d back-buffers on screen", on_screen));
    }

    // 2. A frame's fence is attached to it alone; an acquired frame has none yet; an attached fence has a frame.
    std::set<FenceSlot> fences;
    for (const auto& [seq, frame] : frames_) {
        if (frame.fence == kNoFence) {
            continue;
        }
        if (frame.state == FrameState::Acquired) {
            broken(2, format("acquired frame %llu has fence %u", ull(seq), frame.fence));
        }
        if (!fences.insert(frame.fence).second) {
            broken(2, format("fence %u attached to two frames", frame.fence));
        }
        if (pool_ != nullptr &&
            (frame.fence >= pool_->created() || pool_->state(frame.fence) != FenceState::Attached)) {
            broken(2, format("frame %llu's fence %u is not attached in the pool", ull(seq), frame.fence));
        }
    }
    if (pool_ != nullptr) {
        for (FenceSlot slot = 0; slot < pool_->created(); ++slot) {
            if (pool_->state(slot) == FenceState::Attached && fences.count(slot) == 0) {
                broken(2, format("fence %u attached, but to no live frame", slot));
            }
        }
    }

    // 3. Only on-screen frames have had their id completed (ended frames are gone); a swapchain's completed id never
    //    goes back.
    for (const auto& [seq, frame] : frames_) {
        if (frame.id_done != (frame.state == FrameState::OnScreen)) {
            broken(3, format("frame %llu is %s with its id %s", ull(seq), name(frame.state),
                             frame.id_done ? "completed" : "not completed"));
        }
    }
    for (auto it = checked_completed_.begin(); it != checked_completed_.end();) {
        it = completed_.count(it->first) == 0 ? checked_completed_.erase(it) : std::next(it);
    }
    for (const auto& [gen, id] : completed_) {
        uint64_t& seen = checked_completed_[gen];
        if (id < seen) {
            broken(3, format("swapchain %llu's completed id went back from %llu to %llu", ull(gen), ull(seen),
                             ull(id)));
        }
        seen = id;
    }

    // 4. At most one Present outstanding; a back-buffer went on screen only at a refresh after its Present returned.
    if (presented > 1) {
        broken(4, format("%d Presents outstanding", presented));
    }
    for (uint32_t b = 0; b < buffers_.size(); ++b) {
        const Buffer& buf = buffers_[b];
        if (buf.state == BufferState::OnScreen && (!buf.returned || buf.on_screen_ns <= buf.return_ns)) {
            broken(4, format("back-buffer %u went on screen at %lld, not after its Present returned", b,
                             static_cast<long long>(buf.on_screen_ns)));
        }
    }
}

} // namespace nvdm
