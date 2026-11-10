// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace nvdm {

// A headset the layer knows (design 6.3): how its EDID names it, and the one mode it runs.
struct Product {
    uint16_t pnp_vendor;  // The EDID manufacturer id as the 16 bits the standard stores big-endian (0x42d2 is "PVR").
    uint16_t pnp_product; // The EDID product code (stored little-endian).
    const char* name;     // A string literal: valid for the life of the process, as VkDisplayPropertiesKHR needs.
    uint32_t width, height;
    uint32_t refresh_mhz; // 90000 is 90.000 Hz.
};

// PVR 0x1019: 7104x3840 at 90.000 Hz, the mode struct of DreamAir_Windows.md 3.3.
extern const Product kDreamAir;

// The table's entry whose manufacturer and product code the EDID (a base block or more) names. nullopt for anything
// that is not an EDID base block, or a product the table does not have.
std::optional<Product> find_product(const std::vector<uint8_t>& edid);

// The manufacturer and product code of an EDID, for the log line about a product the table does not know.
struct EdidIds {
    uint16_t pnp_vendor;
    uint16_t pnp_product;
};

// nullopt unless `edid` is at least a base block with the EDID header.
std::optional<EdidIds> parse_edid_ids(const std::vector<uint8_t>& edid);

// The three letters a manufacturer id spells: 0x42d2 is "PVR". "???" if a letter is out of range.
std::string pnp_letters(uint16_t pnp_vendor);

} // namespace nvdm
