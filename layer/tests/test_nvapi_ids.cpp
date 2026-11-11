// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The NVAPI declarations: ids, constants and the three structs whose layout the driver sees.

#include <catch2/catch_test_macros.hpp>

#include <cstddef>
#include <cstdint>
#include <iterator>
#include <set>
#include <string>

#include "core/nvapi_ids.hpp"

TEST_CASE("Mode is 48 bytes and defaults to the Dream Air mode")
{
    REQUIRE(sizeof(nvdm::Mode) == 48);

    const nvdm::Mode mode{};
    CHECK(mode.version == 0x00020030u);
    CHECK(mode.width == 7104u);
    CHECK(mode.height == 3840u);
    CHECK(mode.refresh_num == 90000u);
    CHECK(mode.refresh_den == 1000u);
    CHECK(mode.bpp == 32u);
    CHECK(mode.flags == 1u);
    for (uint32_t pad : mode.pad) {
        CHECK(pad == 0u);
    }
    CHECK(offsetof(nvdm::Mode, pad) == 28);
}

TEST_CASE("RawStats has version 2, then room for what the driver writes past it")
{
    const nvdm::RawStats stats{};
    CHECK(stats.version == 0x00020018u);
    CHECK(stats.refresh_num == 0u);
    CHECK(stats.refresh_den == 0u);
    CHECK(stats.frame_counter == 0u);
    CHECK(stats.qpc == 0u);

    REQUIRE(sizeof(nvdm::RawStats) >= 64);
    CHECK(offsetof(nvdm::RawStats, qpc) == 16);
    CHECK(sizeof(stats.spare) == 40);
    for (uint8_t b : stats.spare) {
        CHECK(b == 0);
    }
}

TEST_CASE("Handle is 64 bytes: display id, pad, the driver's pointer, 48 spare")
{
    REQUIRE(sizeof(nvdm::Handle) == 64);
    CHECK(offsetof(nvdm::Handle, display_id) == 0);
    CHECK(offsetof(nvdm::Handle, nvapi) == 8);
    CHECK(sizeof(nvdm::Handle::spare) == 48);

    const nvdm::Handle handle{};
    CHECK(handle.display_id == 0u);
    CHECK(handle.nvapi == nullptr);
}

TEST_CASE("NVAPI ids")
{
    CHECK(nvdm::Initialize == 0x0150e828u);
    CHECK(nvdm::GetPhysicalGpuFromDisplayId == 0x9ea74659u);
    CHECK(nvdm::GetAdapterIdFromPhysicalGpu == 0x0ff07fdeu);
    CHECK(nvdm::Enumerate == 0x602f60d8u);
    CHECK(nvdm::EnableDirectMode == 0x1ac2b1e7u);
    CHECK(nvdm::BindDevice == 0xc4e4efdau);
    CHECK(nvdm::CreateBackBuffer == 0x86bdb518u);
    CHECK(nvdm::SetDisplayMode == 0x56df9d51u);
    CHECK(nvdm::Present == 0x8c84a92au);
    CHECK(nvdm::GetPresentStats == 0xace116bcu);
    CHECK(nvdm::GetDriverAndBranchVersion == 0x2926aaadu);
    // The public calls of the EDID route.
    CHECK(nvdm::GetGpuAndOutputIdFromDisplayId == 0x112ba1a5u);
    CHECK(nvdm::GetEDID == 0x37d32e69u);
}

TEST_CASE("NVAPI constants")
{
    CHECK(nvdm::kVendorPvr == 0xd242u);
    CHECK(nvdm::kSetModeOk == -208);
    CHECK(nvdm::kBackBufferCount == 3u);
}

TEST_CASE("The id table lists every id once, with a name, in call order")
{
    std::set<uint32_t> ids;
    std::set<std::string> names;
    std::string private_ids;
    for (const nvdm::IdInfo& info : nvdm::kAllIds) {
        CHECK(info.id != 0u);
        REQUIRE(info.name != nullptr);
        CHECK(*info.name != '\0');
        ids.insert(info.id);
        names.insert(info.name);
        if (info.is_private) {
            private_ids += info.name;
            private_ids += ' ';
        }
    }
    CHECK(ids.size() == std::size(nvdm::kAllIds));
    CHECK(names.size() == std::size(nvdm::kAllIds));
    CHECK(std::size(nvdm::kAllIds) == 13);

    // The seven calls only the direct-mode path uses; first_missing_id() names the first of these that is missing.
    CHECK(private_ids ==
          "Enumerate EnableDirectMode BindDevice CreateBackBuffer SetDisplayMode Present GetPresentStats ");
}
