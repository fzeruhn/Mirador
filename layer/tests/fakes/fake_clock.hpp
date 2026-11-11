// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <atomic>
#include <chrono>
#include <cstdint>

#include "core/clock.hpp"

namespace nvdm {

// A clock that only moves when advance() says so. Safe to read from the thread under test while a test advances it.
class FakeClock : public Clock {
public:
    explicit FakeClock(int64_t start_ns = 0) : now_ns_(start_ns) {}

    int64_t now_ns() override { return now_ns_.load(); }
    void advance(int64_t ns) { now_ns_ += ns; }

private:
    std::atomic<int64_t> now_ns_;
};

// A FakeClock that also runs at the real rate (steady_clock, which is QueryPerformanceCounter here) from when it is
// made; advance() still moves it on at once. For tests that run the real display thread, its Win32 waits and the
// layer's condition-variable waits against a FakeApi, whose counter then flips every 1/90 s of real time.
class RunningClock : public FakeClock {
public:
    explicit RunningClock(int64_t start_ns = 0) : FakeClock(start_ns), origin_(std::chrono::steady_clock::now()) {}

    int64_t now_ns() override
    {
        const auto elapsed = std::chrono::steady_clock::now() - origin_;
        return FakeClock::now_ns() + std::chrono::duration_cast<std::chrono::nanoseconds>(elapsed).count();
    }

private:
    std::chrono::steady_clock::time_point origin_;
};

} // namespace nvdm
