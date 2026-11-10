// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "display_thread.hpp"

#include <windows.h>
#include <avrt.h>

#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <exception>
#include <utility>

namespace nvdm {

struct DisplayThread::Baton {
    HANDLE free = nullptr;            // auto-reset event, set while no epoch holds the baton
    std::atomic<uint64_t> holder{0}; // the epoch holding it, 0 for none
    Baton() { free = CreateEventW(nullptr, FALSE, TRUE, nullptr); }
    ~Baton()
    {
        if (free != nullptr) {
            CloseHandle(free);
        }
    }
    Baton(const Baton&) = delete;
    Baton& operator=(const Baton&) = delete;
};

struct DisplayThread::Epoch {
    uint64_t number = 0;
    std::unique_ptr<DisplayLoop> loop;
    std::shared_ptr<Baton> baton;
    std::function<void(const std::string&)> log;
    int64_t baton_wait_ns = kBatonWaitNs;
    HANDLE stop = nullptr;   // manual-reset: ends the wait for the baton
    HANDLE thread = nullptr;
    Epoch() = default;
    ~Epoch()
    {
        if (thread != nullptr) {
            CloseHandle(thread);
        }
        if (stop != nullptr) {
            CloseHandle(stop);
        }
    }
    Epoch(const Epoch&) = delete;
    Epoch& operator=(const Epoch&) = delete;
};

namespace {

std::string format(const char* fmt, ...)
{
    char text[256];
    va_list args;
    va_start(args, fmt);
    vsnprintf(text, sizeof(text), fmt, args);
    va_end(args);
    return text;
}

unsigned long long ull(uint64_t value)
{
    return static_cast<unsigned long long>(value);
}

long long to_ms(int64_t ns)
{
    return static_cast<long long>(ns / 1'000'000);
}

// Nanoseconds to whole milliseconds for a Win32 wait, rounded up and below INFINITE.
DWORD wait_ms(int64_t ns)
{
    if (ns <= 0) {
        return 0;
    }
    return static_cast<DWORD>(std::min<int64_t>((ns + 999'999) / 1'000'000, 0x7fffffff));
}

void tell(const DisplayThread::Epoch& e, const std::string& line)
{
    if (e.log) {
        e.log(line);
    }
}

// Waits for the baton (design 6.7, thread epochs). False if the epoch was asked to stop first, or the wait failed: it
// then makes no NVAPI call.
bool take_baton(DisplayThread::Epoch& e)
{
    bool told = false;
    uint64_t old = 0; // the epoch that held it when we told
    for (;;) {
        const HANDLE handles[2] = {e.baton->free, e.stop};
        const DWORD result = WaitForMultipleObjects(2, handles, FALSE, wait_ms(e.baton_wait_ns));
        if (result == WAIT_OBJECT_0) {
            if (told) {
                tell(e, format("display thread epoch %llu: epoch %llu has left NVAPI; starting", ull(e.number),
                               ull(old)));
            }
            e.baton->holder.store(e.number);
            return true;
        }
        if (result == WAIT_OBJECT_0 + 1) {
            return false;
        }
        if (result != WAIT_TIMEOUT) {
            tell(e, format("display thread epoch %llu: waiting for the NVAPI baton failed (%lu); no NVAPI call",
                           ull(e.number), GetLastError()));
            return false;
        }
        if (!told) {
            old = e.baton->holder.load();
            tell(e, format("display thread epoch %llu: epoch %llu is still inside an NVAPI call after %lld ms; no NVAPI "
                           "call until it exits (checking every %lld ms)",
                           ull(e.number), ull(old), to_ms(e.baton_wait_ns), to_ms(e.baton_wait_ns)));
            told = true;
        }
    }
}

DWORD WINAPI run_epoch(void* param)
{
    std::shared_ptr<DisplayThread::Epoch>* handed = static_cast<std::shared_ptr<DisplayThread::Epoch>*>(param);
    const std::shared_ptr<DisplayThread::Epoch> e = std::move(*handed);
    delete handed;

    DWORD task = 0;
    const HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Games", &task);
    if (mmcss != nullptr) {
        AvSetMmThreadPriority(mmcss, AVRT_PRIORITY_HIGH);
    } else {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
    }
    tell(*e, format("display thread epoch %llu started (%s)", ull(e->number),
                    mmcss != nullptr ? "MMCSS \"Games\"" : "THREAD_PRIORITY_TIME_CRITICAL"));

    if (take_baton(*e)) {
        // Nothing may leave a thread procedure. The frame model throws only for a broken invariant (a bug): log it and
        // end this epoch; acquire and present-wait bound themselves, so Monado runs on at its own timeouts.
        try {
            while (!e->loop->stop_requested()) {
                e->loop->pass();
            }
        } catch (const std::exception& ex) {
            tell(*e, format("display thread epoch %llu stopped by an error, a bug: %s", ull(e->number), ex.what()));
        } catch (...) {
            tell(*e, format("display thread epoch %llu stopped by an unknown error, a bug", ull(e->number)));
        }
        e->baton->holder.store(0);
        SetEvent(e->baton->free);
    }

    if (mmcss != nullptr) {
        AvRevertMmThreadCharacteristics(mmcss);
    }
    return 0;
}

} // namespace

DisplayThread::DisplayThread(std::function<void(const std::string&)> log, int64_t baton_wait_ns)
    : log_(std::move(log)), baton_wait_ns_(baton_wait_ns), baton_(std::make_shared<Baton>())
{
}

DisplayThread::~DisplayThread()
{
    if (current_) {
        current_->loop->request_stop();
        SetEvent(current_->stop);
    }
}

void DisplayThread::say(const std::string& line) const
{
    if (log_) {
        log_(line);
    }
}

void DisplayThread::start(std::function<std::unique_ptr<DisplayLoop>()> make_loop)
{
    std::lock_guard<std::mutex> hold(mutex_);
    if (current_) {
        stop_locked(kJoinTimeoutNs);
    }
    auto e = std::make_shared<Epoch>();
    e->number = epoch_.load() + 1;
    e->loop = make_loop ? make_loop() : nullptr;
    if (!e->loop) {
        say(format("display thread epoch %llu: no loop to run; not started", ull(e->number)));
        return;
    }
    e->baton = baton_;
    e->log = log_;
    e->baton_wait_ns = baton_wait_ns_;
    e->stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (e->stop == nullptr) {
        say(format("display thread epoch %llu: CreateEventW failed (%lu); not started", ull(e->number),
                   GetLastError()));
        return;
    }
    auto* handed = new std::shared_ptr<Epoch>(e);
    e->thread = CreateThread(nullptr, 0, &run_epoch, handed, 0, nullptr);
    if (e->thread == nullptr) {
        delete handed;
        say(format("display thread epoch %llu: CreateThread failed (%lu); not started", ull(e->number),
                   GetLastError()));
        return;
    }
    epoch_.store(e->number);
    current_ = std::move(e);
}

bool DisplayThread::stop_and_join(int64_t timeout_ns)
{
    std::lock_guard<std::mutex> hold(mutex_);
    return stop_locked(timeout_ns);
}

bool DisplayThread::stop_locked(int64_t timeout_ns)
{
    if (!current_) {
        return true;
    }
    const std::shared_ptr<Epoch> e = std::move(current_);
    current_.reset();
    e->loop->request_stop();
    SetEvent(e->stop);
    if (WaitForSingleObject(e->thread, wait_ms(timeout_ns)) == WAIT_OBJECT_0) {
        return true;
    }
    e->loop->abandon();
    say(format("display thread epoch %llu did not stop within %lld ms: left behind inside NVAPI; it exits when the "
               "call returns, touching nothing",
               ull(e->number), to_ms(timeout_ns)));
    return false;
}

uint64_t DisplayThread::epoch() const
{
    return epoch_.load();
}

} // namespace nvdm
