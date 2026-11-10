// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "product_table.hpp"

#include <algorithm>
#include <iterator>

namespace nvdm {

const Product kDreamAir = {0x42d2, 0x1019, "Pimax Dream Air", 7104, 3840, 90000};

namespace {

constexpr size_t kEdidBlock = 128;

// v1 has one headset. Another product is another line here.
const Product* const kProducts[] = {&kDreamAir};

} // namespace

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
    for (const Product* product : kProducts) {
        if (product->pnp_vendor == ids->pnp_vendor && product->pnp_product == ids->pnp_product) {
            return *product;
        }
    }
    return std::nullopt;
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
