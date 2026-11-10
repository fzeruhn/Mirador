// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// What a headset's EDID says beyond its name: the mode it asks for, and whether Windows' own HMD route claims it.

#pragma once

#include <cstdint>
#include <optional>
#include <vector>

namespace nvdm {

struct EdidMode {
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t refresh_mhz = 0; // 90000 is 90.000 Hz.
};

// The mode the EDID asks for: the DisplayID timing marked preferred (a Type I or Type VII detailed timing in a
// DisplayID extension block; a headset wider than 4095 pixels can only say so there), else the base block's first
// detailed timing (EDID's own preferred one), else the first DisplayID timing. nullopt when the EDID has none. Blocks
// and data blocks are read only within their bounds: a length that lies ends the reading of that block.
std::optional<EdidMode> preferred_mode(const std::vector<uint8_t>& edid);

// Whether a CTA-861 extension block carries Microsoft's HMD vendor-specific data block (IEEE OUI 5C-12-CA), by which
// Windows drives a headset itself (its Windows Display Driver Model route for HMDs, "WDDC").
bool has_microsoft_hmd_block(const std::vector<uint8_t>& edid);

} // namespace nvdm
