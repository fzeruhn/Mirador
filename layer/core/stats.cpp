// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "stats.hpp"

#include <algorithm>
#include <cstdio>
#include <utility>

namespace nvdm {
namespace {

// Wake times kept per window for the median. 2700 presents make about as many; a window that takes far longer keeps
// its first ones (its maximum still sees them all).
constexpr size_t kWakesKept = 65536;

double to_ms(double ns)
{
    return ns / 1e6;
}

unsigned long long ull(uint64_t value)
{
    return static_cast<unsigned long long>(value);
}

double median(std::vector<int64_t> values)
{
    if (values.empty()) {
        return 0.0;
    }
    const size_t mid = values.size() / 2;
    std::nth_element(values.begin(), values.begin() + static_cast<std::ptrdiff_t>(mid), values.end());
    const double upper = static_cast<double>(values[mid]);
    if (values.size() % 2 == 1) {
        return upper;
    }
    const double lower = static_cast<double>(*std::max_element(values.begin(),
                                                               values.begin() + static_cast<std::ptrdiff_t>(mid)));
    return (lower + upper) / 2.0;
}

} // namespace

void StatsWindow::on_present(int64_t now_ns)
{
    if (!in_session_ || now_ns - last_present_ns_ > kSessionGapNs) {
        in_session_ = true;
        last_present_ns_ = now_ns;
        open_window(now_ns, kSessionLinePresents);
        return;
    }
    last_present_ns_ = now_ns;
    if (++window_presents_ >= window_size_) {
        close_window(now_ns);
        open_window(now_ns, kWindowPresents);
    }
}

void StatsWindow::on_refresh(uint32_t counter, int64_t qpc_ns)
{
    // The count is in the counter; its time is not needed for it.
    static_cast<void>(qpc_ns);
    if (have_counter_ && counter > counter_) {
        flips_ += counter - counter_;
    }
    have_counter_ = true;
    counter_ = counter;
}

void StatsWindow::on_wake(int64_t delta_ns)
{
    if (!in_session_) {
        return;
    }
    if (wakes_.size() < kWakesKept) {
        wakes_.push_back(delta_ns);
    }
    max_wake_ns_ = wakes_.size() == 1 ? delta_ns : std::max(max_wake_ns_, delta_ns);
}

void StatsWindow::on_counters(const FrameModel::Counters& counters)
{
    counters_ = counters;
}

std::optional<std::string> StatsWindow::take_line()
{
    if (lines_.empty()) {
        return std::nullopt;
    }
    std::string line = std::move(lines_.front());
    lines_.pop_front();
    return line;
}

void StatsWindow::open_window(int64_t now_ns, uint64_t size)
{
    window_start_ns_ = now_ns;
    window_size_ = size;
    window_presents_ = 0;
    window_flips_start_ = flips_;
    window_counters_start_ = counters_;
    wakes_.clear();
    max_wake_ns_ = 0;
}

void StatsWindow::close_window(int64_t now_ns)
{
    const FrameModel::Counters& a = window_counters_start_;
    const FrameModel::Counters& b = counters_;
    const double seconds = static_cast<double>(now_ns - window_start_ns_) / 1e9;
    const uint64_t presents = window_presents_;
    const uint64_t flips = flips_ - window_flips_start_;
    const uint64_t missed = b.missed - a.missed;
    const double per_second = seconds > 0.0 ? 1.0 / seconds : 0.0;
    const double missed_percent = flips != 0 ? 100.0 * static_cast<double>(missed) / static_cast<double>(flips) : 0.0;
    const double max_wake = wakes_.empty() ? 0.0 : static_cast<double>(max_wake_ns_);

    char text[512];
    snprintf(text, sizeof(text),
             "%u presents in %.1f s (%.1f Hz), %u flips (%.1f Hz), %llu missed (%.2f %%), %llu link drops; dropped %llu, "
             "failed %llu, unconfirmed %llu, reserved %llu, held %llu, target skips %llu; event wake median %.2f ms, "
             "max %.2f ms",
             static_cast<unsigned>(presents), seconds, static_cast<double>(presents) * per_second,
             static_cast<unsigned>(flips), static_cast<double>(flips) * per_second, ull(missed), missed_percent,
             ull(b.link_drops - a.link_drops), ull(b.dropped - a.dropped), ull(b.failed - a.failed),
             ull(b.unconfirmed - a.unconfirmed), ull(b.reserved - a.reserved), ull(b.held - a.held),
             ull(b.target_skips - a.target_skips),
             to_ms(median(wakes_)), to_ms(max_wake));
    lines_.emplace_back(text);
}

} // namespace nvdm
