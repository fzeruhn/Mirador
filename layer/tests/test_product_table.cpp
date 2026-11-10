// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The built-in product table: which headset an EDID names.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <string>
#include <vector>

#include "core/product_table.hpp"
#include "fakes/fake_api.hpp"

namespace {

// The EDID value Windows keeps for the Dream Air (HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\PVR1019\...\Device
// Parameters\EDID, design 6.3): three blocks, 384 bytes, read from Finn's machine. It starts 00FFFFFFFFFFFF0042D21910.
constexpr const char* kDreamAirEdidHex =
    "00FFFFFFFFFFFF0042D2191001000000FF230104A50000780030FDAE503BB2230C474A00000001010101010101010101"
    "010101010101000000100000000000000000000000000000000000100000000000000000000000000000000000FC0050"
    "696D61780A20202020202020000000FD083C64E97BFF010A20202020202002C2020325C02309070783010000E2004A75"
    "5C12CA03276F1D3C529A7E4B8DA1C42E5B7D9F0A13000000000000000000000000000000000000000000000000000000"
    "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
    "0000000000000000000000000000002470122B000003012843080488BF1BBF01C7802F00FF0E2F001C800000CF390308"
    "BF1BBF01C7802F00FF0E2F001C800000DD00000000000000000000000000000000000000000000000000000000000000"
    "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000090";

uint8_t nibble(char c)
{
    return static_cast<uint8_t>(c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
}

std::vector<uint8_t> from_hex(const std::string& hex)
{
    std::vector<uint8_t> bytes;
    for (size_t i = 0; i + 1 < hex.size(); i += 2) {
        bytes.push_back(static_cast<uint8_t>(nibble(hex[i]) << 4 | nibble(hex[i + 1])));
    }
    return bytes;
}

} // namespace

TEST_CASE("dream_air_edid_found")
{
    const std::vector<uint8_t> edid = from_hex(kDreamAirEdidHex);
    REQUIRE(edid.size() == 384);
    REQUIRE(edid[8] == 0x42);
    REQUIRE(edid[11] == 0x10);

    const std::optional<nvdm::Product> product = nvdm::find_product(edid);
    REQUIRE(product.has_value());
    CHECK(product->pnp_vendor == 0x42d2);
    CHECK(product->pnp_product == 0x1019);
    CHECK(product->width == 7104);
    CHECK(product->height == 3840);
    CHECK(product->refresh_mhz == 90000);
    CHECK(std::string(product->name) == "Pimax Dream Air");

    // It is the table's entry: kDreamAir itself, with a name that lives as long as the process.
    CHECK(product->name == nvdm::kDreamAir.name);
    CHECK(nvdm::kDreamAir.pnp_vendor == 0x42d2);
    CHECK(nvdm::kDreamAir.pnp_product == 0x1019);
    CHECK(nvdm::kDreamAir.width == 7104);
    CHECK(nvdm::kDreamAir.height == 3840);
    CHECK(nvdm::kDreamAir.refresh_mhz == 90000);
}

TEST_CASE("the base block alone is enough")
{
    const std::vector<uint8_t> base = nvdm::FakeApi::make_edid(0x42d2, 0x1019);
    REQUIRE(nvdm::find_product(base).has_value());
}

TEST_CASE("unknown_product_rejected")
{
    CHECK_FALSE(nvdm::find_product(nvdm::FakeApi::make_edid(0x42d2, 0x1020)).has_value());
    // The product code alone is not the key: another manufacturer's 0x1019 is not a Dream Air.
    CHECK_FALSE(nvdm::find_product(nvdm::FakeApi::make_edid(0x4c2d, 0x1019)).has_value());
}

TEST_CASE("find_product rejects what is not an EDID")
{
    CHECK_FALSE(nvdm::find_product({}).has_value());

    std::vector<uint8_t> truncated = nvdm::FakeApi::make_edid(0x42d2, 0x1019);
    truncated.resize(127);
    CHECK_FALSE(nvdm::find_product(truncated).has_value());

    std::vector<uint8_t> no_header = nvdm::FakeApi::make_edid(0x42d2, 0x1019);
    no_header[1] = 0x00;
    CHECK_FALSE(nvdm::find_product(no_header).has_value());
}

TEST_CASE("parse_edid_ids reads the manufacturer and product code")
{
    const std::optional<nvdm::EdidIds> ids = nvdm::parse_edid_ids(nvdm::FakeApi::make_edid(0x42d2, 0x1020));
    REQUIRE(ids.has_value());
    CHECK(ids->pnp_vendor == 0x42d2);
    CHECK(ids->pnp_product == 0x1020);

    CHECK_FALSE(nvdm::parse_edid_ids({0x00, 0xff, 0xff}).has_value());
    CHECK(nvdm::pnp_letters(0x42d2) == "PVR");
    CHECK(nvdm::pnp_letters(0x4c2d) == "SAM");
}
