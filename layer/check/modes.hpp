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

// --- --vulkan (design 7) ------------------------------------------------------------------------------------------
//
// Through the layer, in Monado's order: instance (API 1.0, Monado's extensions), device before the first display
// query (with VK_EXT_display_control and present id/wait), display, plane, mode, surface, swapchain, then clears with
// present-wait, one display event per frame, and the flip counter.

// What run_vulkan_check needs from outside, so a test can put a fake chain in place of the Vulkan loader.
struct VulkanEnv {
    PFN_vkGetInstanceProcAddr gipa = nullptr; // vkGetInstanceProcAddr of the loader
    bool elevated = false;                    // for the advice when the layer is missing
    bool disable_var_set = false;             // DISABLE_MIRADOR_DIRECT_MODE is set
    std::function<bool()> interrupted;        // unset: never
    // The layer's log, if it can be had (the real one reads %LOCALAPPDATA%\Mirador\direct-mode\layer.log): the tool prints
    // the EDID route and the back-buffer import's lines from it at the end, from the lines of process `pid` only.
    std::function<std::optional<std::string>()> layer_log;
    unsigned long pid = GetCurrentProcessId(); // the layer runs in this process
};

ModeResult run_vulkan_check(const VulkanEnv& env, const Options& options, Output& out);

// The whole text of a layer log file, or nullopt if it cannot be read.
std::optional<std::string> read_layer_log_file(const std::wstring& path);

// The real thing: applies the environment (MIRADOR_DIRECT_MODE=1, DISABLE_RTSS_LAYER=1, ...) before loading
// vulkan-1.dll, then run_vulkan_check on its vkGetInstanceProcAddr. Touches the real layer and so the real NVAPI.
ModeResult run_vulkan_mode(const Options& options, Output& out);

} // namespace dvl
