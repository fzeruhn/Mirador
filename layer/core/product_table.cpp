// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "product_table.hpp"

#include <algorithm>
#include <cstdio>
#include <deque>
#include <iterator>
#include <mutex>
#include <string>

#include "edid.hpp"

namespace nvdm {

const Product kDreamAir = {0x42d2, 0x1019, "Pimax Dream Air", 7104, 3840, 90000};

namespace {

constexpr size_t kEdidBlock = 128;

// The tested table: one headset. Another product is another line here, once tried on its headset.
const Product* const kProducts[] = {&kDreamAir};

// The name of an untested headset: made once per product and kept for the process (VkDisplayPropertiesKHR's names
// must stay valid).
const char* untested_name(const DirectModeVendor& vendor, uint16_t pnp_product)
{
    // Never destroyed (as the layer's other names, design 6.10): a display's properties may be read during exit.
    static std::mutex& mutex = *new std::mutex;
    static auto& names = *new std::deque<std::pair<uint32_t, std::string>>;
    const uint32_t key = static_cast<uint32_t>(vendor.pnp_vendor) << 16 | pnp_product;
    std::lock_guard<std::mutex> lock(mutex);
    for (const auto& [k, name] : names) {
        if (k == key) {
            return name.c_str();
        }
    }
    char code[8];
    std::snprintf(code, sizeof(code), "0x%04x", pnp_product);
    names.emplace_back(key, std::string(vendor.name) + " headset (" + vendor.letters + " " + code + ")");
    return names.back().second.c_str();
}

// Whether an EDID's mode is the product's: the same size, and the rate within half a hertz (an EDID's timing gives the
// rate only to its pixel clock's precision).
bool same_mode(const EdidMode& mode, const Product& product)
{
    const uint32_t diff = mode.refresh_mhz > product.refresh_mhz ? mode.refresh_mhz - product.refresh_mhz
                                                                 : product.refresh_mhz - mode.refresh_mhz;
    return mode.width == product.width && mode.height == product.height && diff <= 500;
}

} // namespace

const DirectModeVendor* vendor_for_pnp(uint16_t pnp_vendor)
{
    for (const DirectModeVendor& vendor : kDirectModeVendors) {
        if (vendor.pnp_vendor == pnp_vendor) {
            return &vendor;
        }
    }
    return nullptr;
}

std::optional<EdidIds> parse_edid_ids(const std::vector<uint8_t>& edid)
{
    static const uint8_t kHeader[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
    if (edid.size() < kEdidBlock || !std::equal(std::begin(kHeader), std::end(kHeader), edid.begin())) {
        return std::nullopt;
    }
    return EdidIds{static_cast<uint16_t>(edid[8] << 8 | edid[9]), static_cast<uint16_t>(edid[10] | edid[11] << 8)};
}

std::optional<Product> find_product(const std::vector<uint8_t>& edid)
{
    const std::optional<EdidIds> ids = parse_edid_ids(edid);
    if (!ids) {
        return std::nullopt;
    }
    const std::optional<EdidMode> mode = preferred_mode(edid);
    for (const Product* product : kProducts) {
        // The ids alone don't name a product: Pimax gives other headsets the Dream Air's PVR 0x1019 (the P2 8K Plus,
        // Linux reference 2.1). The table's entry only when the EDID asks for its mode, or for none (a base block
        // alone, or an EDID cut short); another mode is another headset, and runs untested with its own mode below.
        if (product->pnp_vendor == ids->pnp_vendor && product->pnp_product == ids->pnp_product &&
            (!mode || same_mode(*mode, *product))) {
            return *product;
        }
    }
    const DirectModeVendor* vendor = vendor_for_pnp(ids->pnp_vendor);
    if (!vendor || !mode) {
        return std::nullopt;
    }
    return Product{ids->pnp_vendor, ids->pnp_product, untested_name(*vendor, ids->pnp_product), mode->width, mode->height,
                   mode->refresh_mhz, false};
}

std::string pnp_letters(uint16_t pnp_vendor)
{
    std::string letters;
    for (int shift : {10, 5, 0}) {
        const int value = (pnp_vendor >> shift) & 0x1f; // 1 is 'A'.
        letters.push_back(value >= 1 && value <= 26 ? static_cast<char>('A' + value - 1) : '?');
    }
    return letters;
}

} // namespace nvdm
