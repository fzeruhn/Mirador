// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>
#include <unknwn.h>

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "nvapi_ids.hpp"

namespace nvdm {

// Statuses of our own, for what NVAPI's do not cover. NVAPI's own statuses are small negative numbers (-1 to about
// -250, and SetDisplayMode's -208 is success), so these stay clear of them.
inline constexpr int kNoNvapi = -10001;  // nvapi64.dll could not be loaded: not an NVIDIA driver.
inline constexpr int kNoEntry = -10002;  // nvapi_QueryInterface, or the id the call needs, is missing.
inline constexpr int kBadOutput = -10003; // NVAPI said OK but left an output we need empty.
inline constexpr int kNotBound = -10004;  // A call that takes the bound display's handle came before bind_device.
inline constexpr int kBadArgument = -10005; // A pointer the call needs was NULL; the driver was not called.

// Where the display's product came from: the route that read its EDID (Api::read_edid, in this order), or the
// session's last route, which reads none (DisplaySession, design 6.3).
enum class EdidRoute {
    None,        // No EDID could be read.
    Nvapi,       // NvAPI_GPU_GetEDID on the display's GPU with the display id itself: answers in direct mode.
    NvapiOutput, // NvAPI_GPU_GetEDID with the GPU's output id for the display id: does not answer in direct mode (M1).
    Registry,    // The value Windows kept under Enum\DISPLAY (read_edid_from_registry).
    Board,       // No EDID; the one PVR display taken to be the Dream Air because its board is connected.
};

// "none", "NVAPI", "NVAPI-output", "registry" or "board", for log lines and the check tool. One word each.
inline const char* edid_route_name(EdidRoute route)
{
    switch (route) {
    case EdidRoute::Nvapi: return "NVAPI";
    case EdidRoute::NvapiOutput: return "NVAPI-output";
    case EdidRoute::Registry: return "registry";
    case EdidRoute::Board: return "board";
    case EdidRoute::None: break;
    }
    return "none";
}

// One of the display's scan-out back-buffers as NVAPI made it.
struct BackBuffer {
    uint64_t tag = 0;        // What present() takes.
    HANDLE shared = nullptr; // A legacy DXGI shared handle (not reference counted, nothing to close).
};

// The display's flip counter and time, from GetPresentStats.
struct RefreshStats {
    uint32_t refresh_num = 0; // 0 while the link is down.
    uint32_t refresh_den = 0;
    uint32_t counter = 0;     // Counts flips, restarts at 0 when the link drops.
    int64_t qpc_ns = 0;       // Time of the last flip, on QpcClock's timeline.
};

// The NVAPI calls behind one interface, so everything above can be tested against a fake.
//
// Statuses are NVAPI's: 0 is success, anything else a failure to report with the step's name. The exceptions are
// noted per call. Not thread-safe: only one thread calls NVAPI at a time (design invariant 8), and the caller keeps
// it that way.
class Api {
public:
    virtual ~Api() = default;

    // Loads nvapi64.dll and initialises NVAPI. Safe to call again after it succeeded (it does nothing).
    // kNoNvapi without the DLL, kNoEntry without nvapi_QueryInterface or the Initialize id.
    virtual int initialize() = 0;

    // The name of the first private id (in call order) for which nvapi_QueryInterface returns NULL, as in
    // kAllIds, or nullptr if all resolved. Needs a successful initialize(). The string is static.
    virtual const char* first_missing_id() = 0;

    // The driver version as "621.01", or "" if the call is missing or fails.
    virtual std::string driver_version() = 0;

    // The direct-mode displays of a vendor (kVendorPvr). Empty with status 0 while none is there, which is how the
    // board looks until its heartbeat has woken the display. Ids that are 0 are left out.
    virtual int enumerate(uint32_t vendor, std::vector<uint32_t>& ids) = 0;

    // The adapter LUID of the GPU that drives the display (public NVAPI calls).
    virtual int adapter_luid(uint32_t display_id, LUID& out) = 0;

    // The display's EDID, all of its blocks. Tries the public NVAPI calls (EdidRoute::Nvapi, then NvapiOutput), then
    // the registry value (read_edid_from_registry). Returns the route that answered, or EdidRoute::None if none gives
    // an EDID (`out` is empty then).
    virtual EdidRoute read_edid(uint32_t display_id, std::vector<uint8_t>& out) = 0;

    virtual int enable_direct_mode(uint32_t vendor) = 0;

    // Binds the display to a D3D11 device on the GPU that drives it and keeps the handle every later call takes.
    //
    // `d3d11` is the ID3D11Device* itself, upcast to IUnknown* (an implicit conversion). It is not the result of
    // QueryInterface(IID_IUnknown), which COM may answer with another pointer than the one the driver expects.
    // The caller keeps the device alive for as long as the display is used. kBadArgument, without a driver call, if
    // `d3d11` is NULL. Fails (a status, or kBadOutput) if NVAPI gives no handle. A failed bind can be retried.
    virtual int bind_device(uint32_t display_id, IUnknown* d3d11) = 0;

    // Creates one back-buffer for the mode. kBadOutput if NVAPI returns no shared handle; kNotBound before bind.
    virtual int create_back_buffer(const Mode& mode, BackBuffer& out) = 0;

    // Returns kSetModeOk (-208) on success, not 0. The caller treats both as OK.
    virtual int set_display_mode(const Mode& mode) = 0;

    // Flips to the back-buffer with this tag at the next refresh. Does not wait for it.
    virtual int present(uint64_t tag) = 0;

    virtual int present_stats(RefreshStats& out) = 0;
};

// The real implementation, on nvapi64.dll through nvapi_QueryInterface. Makes no NVAPI call until initialize().
std::unique_ptr<Api> make_nvapi();

// The EDID Windows kept for a monitor of manufacturer "PVR": the EDID value under
// HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\PVR*\<instance>\Device Parameters. The Dream Air's own model key
// (PVR1019) is tried first, then any other PVR* key; the first instance with a valid EDID wins. It is there on
// machines where the display has been a monitor once (design 6.3), so it is only the fallback of Api::read_edid.
// False if there is none; `out` is empty then.
bool read_edid_from_registry(std::vector<uint8_t>& out);

// The same scan under another key, which is laid out like Enum\DISPLAY (model\instance\Device Parameters\EDID). The
// hook for tests, which build such a key under HKCU.
bool read_edid_from_display_key(HKEY display, std::vector<uint8_t>& out);

} // namespace nvdm
