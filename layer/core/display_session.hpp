// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>
#include <unknwn.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <string>
#include <vector>

#include "api.hpp"
#include "clock.hpp"
#include "holders.hpp"
#include "product_table.hpp"

namespace nvdm {

// The driver versions the layer has been run on. Any other is tried, with a warning (design 7).
inline constexpr const char* kTestedDrivers[] = {"621.01"};

bool is_tested_driver(const std::string& version);

// The steps of DisplaySession::open(), in order.
enum class OpenStep {
    Holders,           // No program that holds the display runs.
    LoadNvapi,         // nvapi64.dll loaded and NVAPI initialised; the driver version is logged.
    CheckIds,          // Every private NVAPI id resolves.
    Enumerate,         // A direct-mode display of vendor PVR is there (waits for the board to wake it).
    Luid,              // The display's adapter is the queried device's.
    Product,           // The EDID names a product the table has (none readable: the board names the Dream Air).
    EnableDirectMode,
    D3D11,             // A D3D11 device on the adapter.
    BindDevice,
    CreateBackBuffers, // Three of them.
    SetDisplayMode,
    Done,
};

const char* step_name(OpenStep step); // "Holders", "LoadNvapi", ...

// How an open() attempt ended. `step` is the step that failed, or Done. `code` is the status of the Api call that
// failed (NVAPI's, or one of our k... codes in api.hpp), 0 if no call failed (a holder, no display, a wrong adapter, a
// product the table lacks). `message` is the one line for the log: the step's name, what happened and the code.
struct OpenResult {
    bool ok;
    OpenStep step;
    int code;
    std::string message;
};

// What a session needs from outside, so tests can run it on fakes.
struct SessionDeps {
    Api& api;
    Clock& clock;

    // A D3D11 device on the adapter with this LUID, as an IUnknown* that is the ID3D11Device itself (see
    // Api::bind_device), or NULL on failure. The session never calls AddRef or Release on it: whoever makes it keeps it
    // alive for the life of the process.
    std::function<IUnknown*(const LUID&)> make_d3d11;

    std::function<std::vector<std::string>()> processes; // The running programs' names (list_processes()). Unset: that.
    std::function<void()> pin_module;                    // Keeps the layer DLL loaded until the process exits.
    std::function<bool()> board_present; // The Dream Air's board is connected (board_present()). Unset: that.
    bool ignore_holders = false;                         // Skips the holder check (tests only, design 4).
    bool skip_edid = false;                              // Skips every EDID route (tests only, design 8).
    uint32_t wait_ms = 10000;                            // How long open() waits for the display to appear.
    std::function<void(int64_t ns)> sleep;               // Waits that long. Unset: std::this_thread::sleep_for.

    // Where the session's lines go, one call per line, no trailing newline. A line that starts with "error: " is the
    // failure of a step (its OpenResult::message after the prefix), one that starts with "warning: " a warning (an
    // untested driver, the last EDID route); the others are information. Unset: dropped.
    std::function<void(const std::string&)> log;
};

// The process-wide display session of design 6.3: NVAPI loaded, the display found, bound to a D3D11 device, three
// back-buffers made and the mode set. It holds nothing from Vulkan.
//
// Progress is kept per step: a step that succeeded is not repeated, and a failed one is tried again, with every step
// after it, at the next open(). So NVAPI is initialised once, EnableDirectMode and BindDevice succeed at most once, and
// a board that is slow to wake is simply waited for again. Only the holder check (before everything) and the
// comparison of the adapter run on every attempt.
//
// open() may be called from any thread; calls are serialised among themselves. It calls NVAPI, sleeps and takes no
// lock() (design invariant 7): never call it while holding lock().
class DisplaySession {
public:
    explicit DisplaySession(SessionDeps deps);
    DisplaySession(const DisplaySession&) = delete;
    DisplaySession& operator=(const DisplaySession&) = delete;

    // Takes the session as far as it goes, for the Vulkan device whose adapter LUID is `queried`.
    // Once it is open this makes no Api call: it answers for the session, or says the queried adapter is not its
    // display's.
    OpenResult open(const LUID& queried);

    bool is_open() const { return open_.load(std::memory_order_acquire); }

    // These are valid once is_open(), and do not change after.
    const Product& product() const { return product_; }
    LUID luid() const { return luid_; }                // The display's adapter.
    const BackBuffer& back_buffer(uint32_t index) const; // index < kBackBufferCount; std::out_of_range otherwise.
    Mode mode() const;                                 // What SetDisplayMode got, from the product.
    EdidRoute edid_route() const { return edid_route_; } // Which route the product came from (EdidRoute).

    Api& api() { return deps_.api; }
    // The session's clock: the timeline of the Api's refresh times (RefreshStats::qpc_ns), which the frame model, the
    // display thread and the layer's waits share.
    Clock& clock() { return deps_.clock; }

    // The display-session lock of design 6.5. The session does not take it itself; the frame model and the display
    // loop do, with their callers.
    std::mutex& lock() { return lock_; }

private:
    OpenResult run_steps(const LUID& queried);
    OpenResult check_holders();
    OpenResult load_nvapi();
    OpenResult check_ids();
    OpenResult find_display();
    OpenResult check_adapter(const LUID& queried);
    OpenResult identify_product();
    OpenResult enable_direct_mode();
    OpenResult make_device();
    OpenResult bind_device();
    OpenResult create_back_buffers();
    OpenResult set_display_mode();

    OpenResult proceed(OpenStep step) const { return OpenResult{true, step, 0, std::string()}; }
    OpenResult fail(OpenStep step, int code, const std::string& detail);
    void log(const std::string& line) const;
    void report_driver_version();

    SessionDeps deps_;
    std::mutex lock_;
    std::mutex open_mutex_; // Serialises open(); separate from lock_, which open() must not take.
    std::atomic<bool> open_{false};

    // Progress, written only by open() under open_mutex_, and read by others only once open_ is true.
    bool pinned_ = false;
    bool nvapi_loaded_ = false;
    bool ids_checked_ = false;
    uint32_t display_id_ = 0; // 0 until enumerate() finds one.
    const DirectModeVendor* vendor_ = nullptr; // Whose display it is, once found.
    bool luid_known_ = false;
    LUID luid_{};
    size_t display_count_ = 0; // How many direct-mode displays of vendor PVR the enumerate that found one listed.
    bool product_known_ = false;
    bool skip_edid_logged_ = false;
    Product product_{0, 0, "", 0, 0, 0};
    EdidRoute edid_route_ = EdidRoute::None;
    bool direct_mode_enabled_ = false;
    IUnknown* device_ = nullptr;
    bool bound_ = false;
    std::vector<BackBuffer> back_buffers_;
    std::string done_message_;
};

// The layer's variables for the session (design 8): MIRADOR_DIRECT_MODE_WAIT_MS (milliseconds, default 10000),
// MIRADOR_DIRECT_MODE_IGNORE_HOLDERS and MIRADOR_DIRECT_MODE_TEST_NO_EDID (only "1" counts for these two). Null means not
// set; text that is not a number that fits is the default. Separate from the environment so it can be tested.
struct SessionSettings {
    uint32_t wait_ms = 10000;
    bool ignore_holders = false;
    bool skip_edid = false;
};
SessionSettings parse_session_settings(const char* wait_ms, const char* ignore_holders, const char* no_edid = nullptr);

// Where the process session's lines go. Set it before the first process_session(); by default they go to stderr and
// OutputDebugString. The sink may be called from any thread that calls open().
void set_process_session_log(std::function<void(const std::string&)> sink);

// The one display session of this process, for the layer and the check tool. Built on the first call from the real
// parts: make_nvapi(), QpcClock, a D3D11 device made on the display's adapter and never released, list_processes(),
// board_present(), GetModuleHandleExW with GET_MODULE_HANDLE_EX_FLAG_PIN to keep the module loaded, Sleep, and the
// three variables above. It is never destroyed (no destructors at process exit, design 6.10). Do not call this in a
// unit test: its first open() talks to the real driver.
DisplaySession& process_session();

} // namespace nvdm
