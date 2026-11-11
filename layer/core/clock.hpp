// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>

namespace nvdm {

// Time in nanoseconds on a monotonic timeline. Everything in nvdm that waits or predicts reads this, so tests can
// replace it with a clock that only moves when told to.
class Clock {
public:
    virtual ~Clock() = default;
    virtual int64_t now_ns() = 0;
};

// QueryPerformanceCounter ticks to nanoseconds, without overflowing 64 bits for any tick count a machine reaches.
// Returns 0 for a frequency of 0 or less.
int64_t qpc_ticks_to_ns(int64_t ticks, int64_t frequency);

// The real clock: QueryPerformanceCounter in nanoseconds. NVAPI's present stats are in the same ticks, so
// RefreshStats::qpc_ns from make_nvapi() and now_ns() of this clock compare directly.
class QpcClock : public Clock {
public:
    QpcClock();
    int64_t now_ns() override;

private:
    int64_t frequency_;
};

} // namespace nvdm
