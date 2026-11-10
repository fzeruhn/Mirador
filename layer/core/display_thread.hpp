// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>

#include "display_loop.hpp"

namespace nvdm {

// A new epoch waits this long for the one before it to leave NVAPI, and then checks again this often (design 6.7).
inline constexpr int64_t kBatonWaitNs = 1'000'000'000;
// The layer's join timeout when the last swapchain goes (design 6.10).
inline constexpr int64_t kJoinTimeoutNs = 1'000'000'000;

// The display thread of design 6.7: a raw Win32 thread (CreateThread; no std::thread in anything that may be a
// global, design 6.10) that runs a DisplayLoop's passes until it is asked to stop. It raises its priority with MMCSS
// ("Games"), or THREAD_PRIORITY_TIME_CRITICAL where MMCSS refuses.
//
// Thread epochs. Each start() is a new epoch with its own loop. Only one epoch at a time holds the NVAPI baton
// (design invariant 8): a thread takes it before its first NVAPI call and gives it back when it exits. A thread left
// behind by a join that timed out (stop_and_join() false: it was inside an NVAPI call) keeps the baton until that call
// returns; its loop is abandoned, so it then exits touching nothing. A new epoch waits up to kBatonWaitNs for the
// baton; if it has not got it, it logs that once and makes no NVAPI call, checking again every kBatonWaitNs (and
// stopping at once if asked to). Acquire and present-wait bound themselves, so Monado runs on at its own timeouts.
//
// start() and stop_and_join() may be called from any thread, one at a time, never under the display-session lock.
class DisplayThread {
public:
    // `log` gets the thread's lines (epoch start, the baton, a thread left behind); unset: dropped. `baton_wait_ns` is
    // kBatonWaitNs but for tests.
    explicit DisplayThread(std::function<void(const std::string&)> log = {}, int64_t baton_wait_ns = kBatonWaitNs);
    // Asks a running thread to stop and does not wait for it (design 6.10: nothing at process exit waits). Destroy it
    // only after stop_and_join(); the layer keeps its one for the life of the process.
    ~DisplayThread();
    DisplayThread(const DisplayThread&) = delete;
    DisplayThread& operator=(const DisplayThread&) = delete;

    // A new epoch: makes its loop now, on this thread (making one calls no NVAPI), then starts the thread that runs it.
    // A thread still running is stopped first (stop_and_join(kJoinTimeoutNs)). Nothing starts if make_loop gives none,
    // or the thread cannot be made (logged).
    void start(std::function<std::unique_ptr<DisplayLoop>()> make_loop);

    // Asks the running epoch to stop and waits up to timeout_ns for its thread to exit. True if it exited, or none ran.
    // False: it is left behind, inside an NVAPI call; its loop is abandoned (DisplayLoop::abandon) and it is logged.
    bool stop_and_join(int64_t timeout_ns);

    // The latest epoch started, 0 before the first.
    uint64_t epoch() const;

    struct Epoch; // one start(): its loop, its thread and its stop event
    struct Baton; // the NVAPI baton the epochs pass on

private:
    bool stop_locked(int64_t timeout_ns);
    void say(const std::string& line) const;

    std::function<void(const std::string&)> log_;
    int64_t baton_wait_ns_;
    std::shared_ptr<Baton> baton_;
    std::mutex mutex_; // serialises start() and stop_and_join()
    std::shared_ptr<Epoch> current_;
    std::atomic<uint64_t> epoch_{0};
};

} // namespace nvdm
