// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <deque>
#include <optional>
#include <string>
#include <vector>

#include "frame_model.hpp"

namespace nvdm {

// When stats lines come (design 6.8).
inline constexpr int64_t kSessionGapNs = 1'000'000'000; // more than this without a present: a new app session
inline constexpr uint64_t kSessionLinePresents = 90;     // the first line of an app session, after this many presents
inline constexpr uint64_t kWindowPresents = 2700;        // then a line after every this many (30 s at 90 Hz)

// The stats line of design 6.8: the target's line ("N presents in S s (Hz), F flips (Hz), M missed (%), L link drops")
// plus the frame ends, the back-buffers reserved and the display-event wake times. One line per window of presents.
//
// An app session starts at the first present after more than kSessionGapNs without one; that present opens its first
// window. A window closes at its kSessionLinePresents-th present after the one that opened it (kWindowPresents-th for
// the later ones), which makes the line and opens the next window. Each line covers its window alone: the presents
// after the opening one, the time between the two presents, the flips in between, and how much each counter moved. A
// window an app session does not finish is dropped.
//
// Fields and where they come from:
// - flips: on_refresh(); the counter's increments. A counter lower than the last one is a link drop: it is not a
//   flip, and the increments from the new value count.
// - missed, link drops, dropped, failed, unconfirmed, reserved: FrameModel::Counters (on_counters), which owns them
//   (missed is "flip gap - 1, gaps over 1 s are idle"). They are not counted again here.
// - event wake: on_wake(), each display-event wait's wake time minus NVAPI's refresh QPC time (design 6.9); median and
//   maximum over the window, 0 when there were none.
//
// Not thread-safe: callers hold the display-session lock (the display loop, and the layer's display-event waits).
class StatsWindow {
public:
    // An NVAPI Present made at now_ns. Call on_counters() first, so the line that this may close is up to date.
    void on_present(int64_t now_ns);
    // A GetPresentStats read: the flip counter and its last flip's QPC time. Every read may be passed.
    void on_refresh(uint32_t counter, int64_t qpc_ns);
    void on_wake(int64_t delta_ns);
    void on_counters(const FrameModel::Counters& counters);

    // The oldest line not taken yet, without a trailing newline.
    std::optional<std::string> take_line();

private:
    void open_window(int64_t now_ns, uint64_t size);
    void close_window(int64_t now_ns);

    bool in_session_ = false;
    int64_t last_present_ns_ = 0;

    // The open window.
    int64_t window_start_ns_ = 0;
    uint64_t window_size_ = 0;
    uint64_t window_presents_ = 0;
    uint64_t window_flips_start_ = 0;
    FrameModel::Counters window_counters_start_;
    std::vector<int64_t> wakes_;
    int64_t max_wake_ns_ = 0;

    // Running totals.
    bool have_counter_ = false;
    uint32_t counter_ = 0;
    uint64_t flips_ = 0;
    FrameModel::Counters counters_;

    std::deque<std::string> lines_;
};

} // namespace nvdm
