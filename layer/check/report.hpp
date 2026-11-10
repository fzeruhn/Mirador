// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <sal.h>

#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "core/display_session.hpp"

namespace dvl {

// --- Output ------------------------------------------------------------------------------------------------------

// Where the tool's lines go, one call per line, no trailing newline. Thread safe: the display thread's stats lines and
// the vblank thread's results arrive from other threads.
class Output {
public:
    virtual ~Output() = default;
    virtual void line(const std::string& text) = 0;
};

// stdout, flushed per line (the tool is run from scripts and from a console that may be closed at any time).
class StdoutOutput final : public Output {
public:
    void line(const std::string& text) override;

private:
    std::mutex mutex_;
};

// For tests.
class CaptureOutput final : public Output {
public:
    void line(const std::string& text) override;
    std::vector<std::string> lines() const;
    std::string text() const; // the lines joined with '\n'

private:
    mutable std::mutex mutex_;
    std::vector<std::string> lines_;
};

// printf-style line.
void say(Output& out, _Printf_format_string_ const char* format, ...);

// --- The holder checks (design 4, 7) ----------------------------------------------------------------------------

struct HolderFindings {
    bool list_unreadable = false;      // The process list could not be read, so nothing can be ruled out.
    bool display_held = false;         // pi_server.exe or vrcompositor.exe runs.
    bool board_held = false;           // One of Pimax's six runs (the board and the eye tracker).
    bool monado_running = false;       // monado-service.exe runs.
    std::vector<std::string> blockers; // One line per thing another program holds, naming the programs.
    std::vector<std::string> warnings; // RTSS.
    bool blocked() const { return list_unreadable || !blockers.empty(); }
};

// The display (pi_server, vrcompositor), the board and eye tracker (Pimax's six) and Monado, from the running
// programs' names. The Lighthouse receiver's holders are the start script's business, not this tool's. `warnings`
// carries RTSS.exe, which can draw into the headset image (design 5).
HolderFindings holder_findings(const std::vector<std::string>& processes);

// --- The verdict (design 7) -------------------------------------------------------------------------------------

inline constexpr double kRefreshHz = 90.0; // The Dream Air's mode, 90.000 Hz: the rate when none is known.

// What a run counted. Presents and flips are over the same window of `seconds`.
struct RunCounts {
    uint64_t presents = 0;   // Frames presented (NVAPI Presents in the NVAPI mode, vkQueuePresentKHR in --vulkan).
    uint64_t flips = 0;      // The display's flip counter, as it moved over the window.
    uint64_t failures = 0;   // Presents that failed, frames never confirmed, present-waits that timed out.
    uint64_t link_drops = 0; // Information only: the counter went backwards and recovered.
    double seconds = 0;      // The window, measured.
    double refresh_hz = kRefreshHz; // The rate of the mode that was opened (an untested headset's is its EDID's).
    double requested_seconds = 0;
    bool have_events = false; // --vulkan: one display event per frame.
    uint64_t events_ok = 0;
    uint64_t events_timeout = 0;
};

// 90 Hz for `seconds`, rounded: 1800 for 20 s.
uint64_t expected_flips(double seconds, double refresh_hz = kRefreshHz);

struct Verdict {
    bool pass = false;
    std::string line; // "PASS: 1800 presents, 1800 flips in 20.0 s (90.0 Hz)" or "FAIL: <why>[; <why>]"
};

// Judges a run. `steps_ok` false: a step failed first, named by `failed_step`; the counts are not looked at. Otherwise
// presents and flips must each be within 1 % (at least 3) of 90 Hz for the measured time, no present may have failed,
// the run must not have stopped before 98 % of the requested time, and display events must not time out more than
// 1 % (at least 2).
Verdict judge(bool steps_ok, const std::string& failed_step, const RunCounts& counts);

// --- Lines about the display session and the layer --------------------------------------------------------------

// "Holders ok", "LoadNvapi ok", ... "BindDevice FAILED: <message>": every step up to the one that failed (all of them
// when the session opened).
std::vector<std::string> describe_open_result(const nvdm::OpenResult& result);

// The driver version in the session's lines ("driver version 621.01", or the "warning: untested driver version ..."
// one); empty if none says it.
std::string driver_version_from_lines(const std::vector<std::string>& lines);

// The route named by the last "EDID route: <route>" in the layer's log text, if there is one.
std::optional<std::string> edid_route_from_text(const std::string& log_text);

// What a display-session failure line ("<Step>: <detail>", as DisplaySession::open() words it) means and what to do
// about it, as lines that start "what it means: " and "what to do: ". The display that cannot be identified (Product)
// is explained here; any other step points to the README's Troubleshooting table. Empty for a line that names no step.
std::vector<std::string> explain_failure(const std::string& line);

// What --vulkan reads out of the layer's log (%LOCALAPPDATA%\Mirador\direct-mode\layer.log, a new file per process): the
// session's EDID route, and the back-buffer import: the trial import's line, and the memory-type fallback warning if
// the layer logged one. Only the lines the layer stamped with process `pid` ("[pid N tid M]") count: the file can be an
// older process's, or hold a line another process added.
struct LayerLogFacts {
    std::optional<std::string> edid_route;      // "NVAPI", "NVAPI-output", "registry" or "board"
    std::optional<std::string> trial_import;    // the last "trial import on device ..." line, as the log has it
    std::optional<std::string> memory_fallback; // the "... memory type comes from the image's memoryTypeBits ..." line
    std::optional<std::string> failure;         // the last error line, without its stamp: "<Step>: <detail>"
};
LayerLogFacts layer_log_facts(const std::string& log_text, unsigned long pid);

// A Vulkan driverVersion as NVIDIA numbers it (10 bits, 8 bits, 8 bits, 6 bits): 621.01.
std::string nvidia_driver_text(uint32_t driver_version);

// What --vulkan found out about the layer when instance creation could not have it.
struct LayerFacts {
    bool layer_listed = false;   // vkEnumerateInstanceLayerProperties names VK_LAYER_MIRADOR_direct_mode.
    bool elevated = false;       // This process is elevated.
    bool disable_var_set = false; // DISABLE_MIRADOR_DIRECT_MODE is set.
};
inline constexpr const char* kLayerName = "VK_LAYER_MIRADOR_direct_mode";

// Lines saying what to do about it.
std::vector<std::string> diagnose_missing_layer(const LayerFacts& facts);

} // namespace dvl
