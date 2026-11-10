// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <windows.h>

#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "check/options.hpp"
#include "check/report.hpp"
#include "core/display_session.hpp"

namespace dvl {

// What a mode hands back to main(): whether every step worked (and if not, which one failed first) and what the run
// counted. main() turns it into the verdict (judge) and the exit code.
struct ModeResult {
    bool steps_ok = true;
    std::string failed_step; // set when !steps_ok: the step, what happened and its return value
    RunCounts counts;
};

// Ctrl+C and Ctrl+Break end a run early instead of killing the process (a killed process may leave the display thread
// inside NVAPI): main() installs the handler, the loops look at the flag. Closing the console, or a second Ctrl+C, ends
// the process through the default handler, but only after the handler has run the stop hook (below) and waited up to
// 100 ms for it, so the NVAPI mode's display thread is not killed inside NVAPI.
void install_interrupt_handler();
bool interrupted();

// The handler install_interrupt_handler() installs; tests call it directly.
BOOL on_interrupt_event(DWORD event);
// What stops the NVAPI mode's display thread (stop_and_join with a 100 ms timeout), for the handler to run before the
// process ends. Set while that thread runs, empty otherwise. Replacing it waits for a run in progress to end, so what
// it refers to may be destroyed once it has been replaced.
void set_interrupt_stop_hook(std::function<void()> hook);
// Tests: the flag a handled event set is cleared again.
void clear_interrupted_for_tests();

// --- Default mode: NVAPI only (design 7) ------------------------------------------------------------------------
//
// Opens the process's display session (driver version, every private id, display, EnableDirectMode, BindDevice,
// back-buffers, mode) on the DXGI adapter that drives the headset, then presents solid colours for options.seconds
// through the same frame model and display thread the layer uses, with a FenceOps that is always signalled (no Vulkan
// here), and counts presents and flips. Touches the real NVAPI: never call it from a test.
ModeResult run_nvapi_mode(const Options& options, Output& out);

// What run_nvapi_check needs from outside, so a test can put a session on a fake NVAPI in place of the process's.
struct NvapiEnv {
    nvdm::DisplaySession* session = nullptr;
    std::vector<LUID> adapters;       // The NVIDIA adapters to try, in order; none: one try that names the display's.
    bool clear_back_buffers = true;   // Clear them through a D3D11 device (the real handles only); false: not at all.
    std::function<std::vector<std::string>()> session_lines; // The session's log so far, for the driver version.
};

ModeResult run_nvapi_check(const NvapiEnv& env, const Options& options, Output& out);

} // namespace dvl
