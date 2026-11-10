// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>

namespace nvdm {

// What a trace record says. a and b by kind; every time is QPC nanoseconds (Clock::now_ns, RefreshStats::qpc_ns).
enum class TraceKind : uint32_t {
    Acquire = 1,          // vkAcquireNextImageKHR got a back-buffer: a = frame seq, b = back-buffer
    AcquireSubmitted = 2, // its signal submit on the private queue returned: a = frame seq
    PresentEnter = 3,     // vkQueuePresentKHR entered: a = present id, b = image index
    PresentQueued = 4,    // the frame is queued (under the lock): a = present id, b = frame seq
    PresentReturn = 5,    // vkQueuePresentKHR returns: a = present id
    PresentTarget = 6,    // the present asks for a time (design 6.11): a = frame seq, b = the earliest flip it may take
    Step1Wait = 10,       // the display thread's step 1 wait starts: a = mode (0 active, 1 idle, 2 stopped), b = until
    Step1Woke = 11,       // it ended: a = 1 if the "frame queued" event ended it
    FenceWait = 12,       // step 2's fence wait starts: a = frame seq, b = deadline
    FenceDone = 13,       // a fence wait ended (b = kFenceSignalled or kFenceNotSignalled), or a status read saw it
                          // signalled (b = kFenceStatusSignalled)
    NvPresentStart = 20,  // just before NVAPI Present: a = frame seq, b = back-buffer
    NvPresentReturn = 21, // NVAPI Present returned: a = frame seq, b = its return code
    PollBegin = 30,       // step 3 starts: b = the predicted refresh
    Counter = 31,         // a GetPresentStats read saw the counter move: a = counter, b = the stats' QPC time
    Credit = 32,          // the outstanding Present went on screen: a = frame seq, b = the stats' QPC time
    PollEnd = 33,         // step 3 ends: a = 1 if the counter moved
    PresentWaitEnter = 40,  // vkWaitForPresentKHR entered: a = present id
    PresentWaitReturn = 41, // it returns: a = present id, b = its VkResult
    EventRegister = 50,     // vkRegisterDisplayEventEXT: a = the fence's handle
    EventWake = 51,         // a display-event wait returned VK_SUCCESS: a = 0, b = the refresh QPC time that set it
};

// FenceDone's b. A status read is 3, not 2, so that traces already analysed read the same.
inline constexpr int64_t kFenceNotSignalled = 0;
inline constexpr int64_t kFenceSignalled = 1;
inline constexpr int64_t kFenceStatusSignalled = 3;

// A per-frame timing trace for debugging frame pacing (the layer's MIRADOR_DIRECT_MODE_TRACE; off, no Trace, by
// default). A fixed array of records any thread appends to without a lock and without allocating; once it is full,
// later records are dropped (counted). append_csv() writes what was added since its last call, while others append.
class Trace {
public:
    // `on_full` is called once, by the add() that finds the array full for the first time (the first record dropped),
    // on whatever thread that is and possibly under a lock of its own: it must be quick and must not block. An
    // exception it throws is dropped.
    explicit Trace(size_t capacity, std::function<void()> on_full = {});
    Trace(const Trace&) = delete;
    Trace& operator=(const Trace&) = delete;

    void add(TraceKind kind, int64_t t_ns, uint64_t a = 0, int64_t b = 0) noexcept;

    size_t size() const;     // records held
    uint64_t dropped() const; // records that did not fit

    struct Written {
        bool ok = false;    // false if the file could not be opened or written
        size_t records = 0; // lines written
        size_t skipped = 0; // older records left out because of the bound; they are not written later
    };
    // Adds the records not written yet to the file, oldest first, as "t_ns,kind,a,b" lines: the first call makes the
    // file new with a header line, later ones append. With `newest_at_most`, only that many of the newest are written.
    // Stops at a record another thread is still writing; it comes with the next call. A file that cannot be opened
    // keeps the records for the next call. Any thread; calls are one at a time (a second one waits for the first).
    Written append_csv(const std::wstring& path, size_t newest_at_most = SIZE_MAX);

    struct Record {
        int64_t t_ns;
        uint64_t a;
        int64_t b;
        std::atomic<uint32_t> kind; // written last: 0 while the record is being written
    };
    // The record at index i < size(), for tests; kind 0 if it was still being written.
    const Record& at(size_t i) const;

private:
    std::unique_ptr<Record[]> records_;
    size_t capacity_;
    std::function<void()> on_full_;
    std::atomic<size_t> next_{0};

    std::mutex write_mutex_; // append_csv(): the two below
    size_t written_ = 0;     // records before this index are written (or left out)
    bool header_written_ = false;
};

} // namespace nvdm
