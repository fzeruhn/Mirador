// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// Our own declarations for the NVAPI calls the Dream Air's direct mode needs: the ids that go to
// nvapi_QueryInterface, the vendor and status constants, and the three structs whose layout the driver sees.
// Nothing here comes from an NVIDIA header. The layouts and values were worked out from runtime captures of the
// Pimax and SteamVR compositors (docs\captures\windows-directmode-2026-10-29).

#pragma once

#include <cstdint>

namespace nvdm {

// Ids for nvapi_QueryInterface. The first group is what the direct-mode path calls (the seven private ones, and the
// public ones for the display's GPU, its adapter LUID and the driver version); the last two are public calls of the
// EDID route.
enum Id : uint32_t {
    Initialize = 0x0150e828,
    GetPhysicalGpuFromDisplayId = 0x9ea74659,
    GetAdapterIdFromPhysicalGpu = 0x0ff07fde,
    Enumerate = 0x602f60d8,
    EnableDirectMode = 0x1ac2b1e7,
    BindDevice = 0xc4e4efda,
    CreateBackBuffer = 0x86bdb518,
    SetDisplayMode = 0x56df9d51,
    Present = 0x8c84a92a,
    GetPresentStats = 0xace116bc,
    GetDriverAndBranchVersion = 0x2926aaad,
    GetGpuAndOutputIdFromDisplayId = 0x112ba1a5,
    GetEDID = 0x37d32e69,
};

struct IdInfo {
    Id id;
    const char* name; // The enumerator's name, as first_missing_id() reports it.
    bool is_private;  // Only the direct-mode path uses it (an undocumented call).
};

// Every id, in the order of a session's calls. first_missing_id() walks this.
inline constexpr IdInfo kAllIds[] = {
    {Initialize, "Initialize", false},
    {GetPhysicalGpuFromDisplayId, "GetPhysicalGpuFromDisplayId", false},
    {GetAdapterIdFromPhysicalGpu, "GetAdapterIdFromPhysicalGpu", false},
    {Enumerate, "Enumerate", true},
    {EnableDirectMode, "EnableDirectMode", true},
    {BindDevice, "BindDevice", true},
    {CreateBackBuffer, "CreateBackBuffer", true},
    {SetDisplayMode, "SetDisplayMode", true},
    {Present, "Present", true},
    {GetPresentStats, "GetPresentStats", true},
    {GetDriverAndBranchVersion, "GetDriverAndBranchVersion", false},
    {GetGpuAndOutputIdFromDisplayId, "GetGpuAndOutputIdFromDisplayId", false},
    {GetEDID, "GetEDID", false},
};

// EDID manufacturer id "PVR" (Pimax), as the direct-mode calls take it.
inline constexpr uint32_t kVendorPvr = 0xd242;

// What SetDisplayMode returns when it worked: every capture and every working run of ours got this.
inline constexpr int kSetModeOk = -208;

// SteamVR uses three back-buffers, Pimax two.
inline constexpr uint32_t kBackBufferCount = 3;

// Display mode, NVAPI struct version 2. The defaults are the Dream Air's mode: 7104x3840 at 90.000 Hz.
struct Mode {
    uint32_t version = 0x00020030;
    uint32_t width = 7104;
    uint32_t height = 3840;
    uint32_t refresh_num = 90000; // Millihertz with refresh_den 1000.
    uint32_t refresh_den = 1000;
    uint32_t bpp = 32;
    uint32_t flags = 1; // Always 1 in the captures.
    uint32_t pad[5] = {};
};
static_assert(sizeof(Mode) == 48, "the driver takes a 48-byte mode");

// Present stats, NVAPI struct version 2.
struct RawStats {
    uint32_t version = 0x00020018;
    uint32_t refresh_num = 0;
    uint32_t refresh_den = 0;
    uint32_t frame_counter = 0; // Counts flips, restarts at 0 when the link drops.
    uint64_t qpc = 0;           // QueryPerformanceCounter ticks of the last flip.

    // The driver writes past the 24 bytes the version gives (SteamVR's capture; a 24-byte struct crashed Monado
    // under /GS), so leave room.
    uint8_t spare[40] = {};
};
static_assert(sizeof(RawStats) >= 64, "keep room for what the driver writes past the version-2 fields");

// Filled by BindDevice; every later call takes a pointer to it.
struct Handle {
    uint32_t display_id = 0; // In.
    uint32_t pad = 0;
    void* nvapi = nullptr; // Out.

    // Room in case a driver writes more than the 16 bytes seen so far.
    uint8_t spare[48] = {};
};
static_assert(sizeof(Handle) == 64, "16 bytes the driver uses, plus 48 spare");

} // namespace nvdm
