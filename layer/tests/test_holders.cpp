// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The holder lists (design 4): who can hold the display, the board, the Lighthouse receiver.

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <algorithm>
#include <cctype>
#include <string>
#include <vector>

#include "core/holders.hpp"

namespace {

using nvdm::Held;
using Names = std::vector<std::string>;

std::string lower(std::string text)
{
    for (char& c : text) {
        c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    }
    return text;
}

} // namespace

TEST_CASE("holders_match_case_insensitively")
{
    // The answer is the table's spelling, not the process list's.
    CHECK(nvdm::running_holders(Held::Display, {"PI_SERVER.EXE", "notepad.exe"}) == Names{"pi_server.exe"});
    CHECK(nvdm::running_holders(Held::Display, {"Pi_Server.Exe"}) == Names{"pi_server.exe"});
    CHECK(nvdm::running_holders(Held::Display, {"VRCOMPOSITOR.exe", "pi_server.exe"}) ==
          Names{"pi_server.exe", "vrcompositor.exe"});

    // Nothing running, nothing held; a program that only looks similar is not a holder.
    CHECK(nvdm::running_holders(Held::Display, {}).empty());
    CHECK(nvdm::running_holders(Held::Display, {"notepad.exe", "pi_server", "xpi_server.exe", "pi_server.exe.bak"})
              .empty());
}

TEST_CASE("each running holder is named once, in the table's order")
{
    CHECK(nvdm::running_holders(Held::Display, {"pi_server.exe", "PI_SERVER.EXE", "pi_server.exe"}) ==
          Names{"pi_server.exe"});
    CHECK(nvdm::running_holders(Held::Display, {"vrcompositor.exe", "pi_server.exe"}) ==
          nvdm::running_holders(Held::Display, {"pi_server.exe", "vrcompositor.exe"}));
}

TEST_CASE("the display is held by pi_server and vrcompositor only")
{
    CHECK(nvdm::running_holders(Held::Display, {"vrserver.exe", "PimaxClient.exe", "monado-service.exe"}).empty());
    CHECK(nvdm::running_holders(Held::Display, {"vrcompositor.exe"}) == Names{"vrcompositor.exe"});
}

TEST_CASE("the receiver is held by SteamVR's server, monitor and the lighthouse manager")
{
    const Names running = {"vrserver.exe", "VRMONITOR.EXE", "ovrlighthousemanager.exe", "notepad.exe"};
    CHECK(nvdm::running_holders(Held::Receiver, running) ==
          Names{"vrserver.exe", "vrmonitor.exe", "OVRLighthouseManager.exe"});
    CHECK(nvdm::running_holders(Held::Receiver, {"pi_server.exe", "vrcompositor.exe"}).empty());
}

TEST_CASE("the board is held by the launcher's six Pimax programs")
{
    const Names six = {"pi_server.exe",         "PimaxClient.exe",        "PiPlayService.exe",
                       "PiPlatformService_64.exe", "pi_overlay.exe", "vrss_gaze_provider.exe"};
    CHECK(nvdm::running_holders(Held::Board, six) == six);

    // In any spelling, and with others around them.
    Names shouted;
    for (const std::string& name : six) {
        std::string upper = name;
        for (char& c : upper) {
            c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
        }
        shouted.push_back(upper);
    }
    shouted.push_back("explorer.exe");
    CHECK(nvdm::running_holders(Held::Board, shouted) == six);

    CHECK(nvdm::running_holders(Held::Board, {"vrserver.exe", "monado-service.exe", "vrcompositor.exe"}).empty());
}

TEST_CASE("monado-service holds all three")
{
    CHECK(nvdm::running_holders(Held::Monado, {"Monado-Service.exe", "notepad.exe"}) == Names{"monado-service.exe"});
    CHECK(nvdm::running_holders(Held::Monado, {"pi_server.exe"}).empty());
}

TEST_CASE("list_processes sees this test program")
{
    wchar_t path[MAX_PATH] = {};
    REQUIRE(GetModuleFileNameW(nullptr, path, MAX_PATH) > 0);
    std::wstring wide(path);
    const std::wstring wide_name = wide.substr(wide.find_last_of(L"\\/") + 1);
    std::string name;
    for (wchar_t c : wide_name) {
        name.push_back(static_cast<char>(c)); // The test executable's name is ASCII.
    }

    const Names processes = nvdm::list_processes();
    REQUIRE_FALSE(processes.empty());
    const bool found = std::any_of(processes.begin(), processes.end(),
                                   [&](const std::string& p) { return lower(p) == lower(name); });
    CHECK(found);
}
