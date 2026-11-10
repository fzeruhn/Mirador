// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace nvdm {

// A headset the layer drives (design 6.3): how its EDID names it, and the one mode it runs. A product of the tested
// table, or another headset of a direct-mode vendor whose EDID gives its mode (untested: Mirador.md section 4.11 item 52).
struct Product {
    uint16_t pnp_vendor;  // The EDID manufacturer id as the 16 bits the standard stores big-endian (0x42d2 is "PVR").
    uint16_t pnp_product; // The EDID product code (stored little-endian).
    const char* name;     // Valid for the life of the process, as VkDisplayPropertiesKHR needs.
    uint32_t width, height;
    uint32_t refresh_mhz; // 90000 is 90.000 Hz.
    bool tested = true;   // In the tested table; false for a mode taken from the EDID of a headset never tried.
};

// A vendor whose headsets NVIDIA's driver keeps in direct mode, and the layer looks for.
struct DirectModeVendor {
    uint32_t nvapi_id;   // The id the direct-mode calls take (0xd242 for PVR).
    uint16_t pnp_vendor; // The EDID manufacturer id (0x42d2 for PVR).
    const char* letters; // "PVR"
    const char* name;    // "Pimax"
};

// Pimax first. Another vendor is another line here, and needs a capture on its headset first (design: layer doc 14).
inline constexpr DirectModeVendor kDirectModeVendors[] = {
    {0xd242, 0x42d2, "PVR", "Pimax"},
};

// The vendor with this EDID manufacturer id, or nullptr.
const DirectModeVendor* vendor_for_pnp(uint16_t pnp_vendor);

// PVR 0x1019: 7104x3840 at 90.000 Hz, the mode struct of DreamAir_Windows.md 3.3.
extern const Product kDreamAir;

// The tested table's entry whose manufacturer and product code the EDID (a base block or more) names; else, for a
// headset of a direct-mode vendor, a product with the mode its EDID asks for (preferred_mode), marked untested. nullopt
// for anything that is not an EDID base block, a vendor the layer doesn't know, or an EDID without a mode.
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
