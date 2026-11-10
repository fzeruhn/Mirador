// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The check tool's pure parts (design 7): its arguments, the holder findings, the verdict, the lines it prints for the
// display session's result, and what it takes from the layer's log. Nothing here touches NVAPI, the board or Vulkan.

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <algorithm>
#include <string>
#include <vector>

#include "check/options.hpp"
#include "check/report.hpp"
#include "core/display_session.hpp"
#include "core/edid.hpp"
#include "fakes/layer_dll.hpp"

namespace {

using Args = std::vector<std::string>;
using Names = std::vector<std::string>;

bool contains(const std::string& text, const std::string& part)
{
    return text.find(part) != std::string::npos;
}

bool any_line_contains(const std::vector<std::string>& lines, const std::string& part)
{
    return std::any_of(lines.begin(), lines.end(), [&](const std::string& l) { return contains(l, part); });
}

} // namespace

// --- Arguments -------------------------------------------------------------------------------------------------------

TEST_CASE("options_default_to_the_nvapi_check_of_20_seconds")
{
    const dvl::ParsedOptions parsed = dvl::parse_options({});
    REQUIRE(parsed.ok());
    CHECK_FALSE(parsed.options.vulkan);
    CHECK_FALSE(parsed.options.no_heartbeat);
    CHECK_FALSE(parsed.options.ignore_holders);
    CHECK_FALSE(parsed.options.help);
    CHECK(parsed.options.seconds == 20);
}

TEST_CASE("options_take_every_flag_in_any_order")
{
    const dvl::ParsedOptions parsed =
        dvl::parse_options({"--seconds=5", "--ignore-holders", "--vulkan", "--no-heartbeat"});
    REQUIRE(parsed.ok());
    CHECK(parsed.options.vulkan);
    CHECK(parsed.options.no_heartbeat);
    CHECK(parsed.options.ignore_holders);
    CHECK(parsed.options.seconds == 5);
}

TEST_CASE("options_reject_what_they_do_not_know")
{
    for (const Args& args : {Args{"--bogus"}, Args{"vulkan"}, Args{"-v"}, Args{"--Vulkan"}, Args{"--vulkan=1"},
                             Args{"--seconds"}, Args{"--seconds", "5"}, Args{"--no-heartbeat=1"}, Args{""}}) {
        INFO("first argument: '" << args[0] << "'");
        const dvl::ParsedOptions parsed = dvl::parse_options(args);
        CHECK_FALSE(parsed.ok());
        CHECK_FALSE(parsed.error.empty());
    }
    // The message names the argument.
    CHECK(contains(dvl::parse_options({"--bogus"}).error, "--bogus"));
}

TEST_CASE("options_seconds_must_be_a_whole_number_in_range")
{
    CHECK(dvl::parse_options({"--seconds=1"}).ok());
    CHECK(dvl::parse_options({"--seconds=3600"}).ok());
    CHECK(dvl::parse_options({"--seconds=3600"}).options.seconds == 3600);
    for (const char* bad : {"--seconds=", "--seconds=0", "--seconds=-5", "--seconds=3601", "--seconds=abc",
                            "--seconds=5s", "--seconds=2.5", "--seconds= 5", "--seconds=+5",
                            "--seconds=99999999999999999999"}) {
        INFO(bad);
        const dvl::ParsedOptions parsed = dvl::parse_options({bad});
        CHECK_FALSE(parsed.ok());
        CHECK(contains(parsed.error, "--seconds"));
    }
}

TEST_CASE("options_help_is_recognised_and_wins_over_nothing_else")
{
    for (const char* flag : {"--help", "-h", "/?"}) {
        const dvl::ParsedOptions parsed = dvl::parse_options({flag});
        CHECK(parsed.ok());
        CHECK(parsed.options.help);
    }
    // A usage error is still an error beside --help.
    CHECK_FALSE(dvl::parse_options({"--help", "--bogus"}).ok());
}

TEST_CASE("the usage text names every option, the exit codes and the layer's need")
{
    const std::string usage = dvl::usage_text();
    for (const char* part : {"--vulkan", "--no-heartbeat", "--ignore-holders", "--seconds=20", "VK_IMPLICIT_LAYER_PATH",
                             "Exit code", "mirador-display-check"}) {
        INFO(part);
        CHECK(contains(usage, part));
    }
    CHECK(dvl::kExitPass == 0);
    CHECK(dvl::kExitFail == 1);
    CHECK(dvl::kExitUsage == 2);
}

// --- The environment the modes set ----------------------------------------------------------------------------------

TEST_CASE("the environment follows design 7 for each mode")
{
    dvl_test::ScopedEnv layer(L"MIRADOR_DIRECT_MODE", nullptr);
    dvl_test::ScopedEnv rtss(L"DISABLE_RTSS_LAYER", nullptr);
    dvl_test::ScopedEnv ignore(L"MIRADOR_DIRECT_MODE_IGNORE_HOLDERS", L"1"); // left over from somewhere else
    auto get = [](const wchar_t* name) {
        wchar_t buffer[8];
        const DWORD n = GetEnvironmentVariableW(name, buffer, 8);
        return n == 0 ? std::wstring(L"<unset>") : std::wstring(buffer, n);
    };

    // NVAPI mode: only the holder switch matters, and it is off unless asked for (a stray variable must not skip the
    // session's own check behind the user's back).
    dvl::Options nvapi;
    dvl::apply_environment(nvapi);
    CHECK(get(L"MIRADOR_DIRECT_MODE_IGNORE_HOLDERS") == L"<unset>");
    CHECK(get(L"MIRADOR_DIRECT_MODE") == L"<unset>");
    CHECK(get(L"DISABLE_RTSS_LAYER") == L"<unset>");

    dvl::Options nvapi_ignore;
    nvapi_ignore.ignore_holders = true;
    dvl::apply_environment(nvapi_ignore);
    CHECK(get(L"MIRADOR_DIRECT_MODE_IGNORE_HOLDERS") == L"1");
    CHECK(get(L"MIRADOR_DIRECT_MODE") == L"<unset>");

    // --vulkan: the layer on and RTSS's layer off, in this process.
    dvl::Options vulkan;
    vulkan.vulkan = true;
    dvl::apply_environment(vulkan);
    CHECK(get(L"MIRADOR_DIRECT_MODE") == L"1");
    CHECK(get(L"DISABLE_RTSS_LAYER") == L"1");
    CHECK(get(L"MIRADOR_DIRECT_MODE_IGNORE_HOLDERS") == L"<unset>");

    dvl::Options vulkan_ignore;
    vulkan_ignore.vulkan = true;
    vulkan_ignore.ignore_holders = true;
    dvl::apply_environment(vulkan_ignore);
    CHECK(get(L"MIRADOR_DIRECT_MODE") == L"1");
    CHECK(get(L"DISABLE_RTSS_LAYER") == L"1");
    CHECK(get(L"MIRADOR_DIRECT_MODE_IGNORE_HOLDERS") == L"1");
}

// --- Holder checks ---------------------------------------------------------------------------------------------------

TEST_CASE("nothing running means nothing held")
{
    const dvl::HolderFindings f = dvl::holder_findings({"explorer.exe", "notepad.exe", "chrome.exe"});
    CHECK_FALSE(f.blocked());
    CHECK_FALSE(f.list_unreadable);
    CHECK(f.blockers.empty());
    CHECK(f.warnings.empty());
}

TEST_CASE("pi_server holds the display and the board, and both are named")
{
    const dvl::HolderFindings f = dvl::holder_findings({"explorer.exe", "PI_SERVER.EXE", "vrss_gaze_provider.exe"});
    CHECK(f.blocked());
    REQUIRE(f.blockers.size() == 2);
    // The display line (design 4: pi_server.exe, vrcompositor.exe) and the board line (the launcher's six).
    CHECK(any_line_contains(f.blockers, "display"));
    CHECK(any_line_contains(f.blockers, "board"));
    for (const std::string& line : f.blockers) {
        CHECK(contains(line, "pi_server.exe")); // the table's spelling
    }
    CHECK(any_line_contains(f.blockers, "vrss_gaze_provider.exe"));
}

TEST_CASE("SteamVR's compositor holds the display but not the board")
{
    const dvl::HolderFindings f = dvl::holder_findings({"vrcompositor.exe"});
    CHECK(f.blocked());
    REQUIRE(f.blockers.size() == 1);
    CHECK(contains(f.blockers[0], "display"));
    CHECK(contains(f.blockers[0], "vrcompositor.exe"));
}

TEST_CASE("Pimax's client alone holds the board")
{
    const dvl::HolderFindings f = dvl::holder_findings({"PimaxClient.exe"});
    CHECK(f.blocked());
    REQUIRE(f.blockers.size() == 1);
    CHECK(contains(f.blockers[0], "board"));
    CHECK(contains(f.blockers[0], "PimaxClient.exe"));
}

TEST_CASE("the findings say which of the three is held")
{
    dvl::HolderFindings f = dvl::holder_findings({"pi_server.exe"});
    CHECK(f.display_held);
    CHECK(f.board_held);
    CHECK_FALSE(f.monado_running);

    f = dvl::holder_findings({"vrcompositor.exe"});
    CHECK(f.display_held);
    CHECK_FALSE(f.board_held);

    f = dvl::holder_findings({"vrss_gaze_provider.exe"});
    CHECK_FALSE(f.display_held);
    CHECK(f.board_held);

    f = dvl::holder_findings({"monado-service.exe"});
    CHECK(f.monado_running);
    CHECK_FALSE(f.display_held);
    CHECK_FALSE(f.board_held);

    f = dvl::holder_findings({"explorer.exe"});
    CHECK_FALSE(f.display_held);
    CHECK_FALSE(f.board_held);
    CHECK_FALSE(f.monado_running);
}

TEST_CASE("Monado holds all three")
{
    const dvl::HolderFindings f = dvl::holder_findings({"monado-service.exe"});
    CHECK(f.blocked());
    REQUIRE(f.blockers.size() == 1);
    CHECK(contains(f.blockers[0], "Monado"));
    CHECK(contains(f.blockers[0], "monado-service.exe"));
}

TEST_CASE("the Lighthouse receiver's holders do not block this tool")
{
    // Only the start script needs the receiver (design 4).
    CHECK_FALSE(dvl::holder_findings({"vrserver.exe", "vrmonitor.exe", "OVRLighthouseManager.exe"}).blocked());
}

TEST_CASE("an unreadable process list blocks, because Pimax cannot be ruled out")
{
    const dvl::HolderFindings f = dvl::holder_findings({});
    CHECK(f.list_unreadable);
    CHECK(f.blocked());
    REQUIRE(f.blockers.size() == 1);
    CHECK(contains(f.blockers[0], "process list"));
}

TEST_CASE("RTSS only warns")
{
    dvl::HolderFindings f = dvl::holder_findings({"explorer.exe", "RTSS.exe"});
    CHECK_FALSE(f.blocked());
    REQUIRE(f.warnings.size() == 1);
    CHECK(contains(f.warnings[0], "RTSS"));
    CHECK(contains(f.warnings[0], "profile"));

    f = dvl::holder_findings({"rtss.EXE", "pi_server.exe"});
    CHECK(f.blocked());
    CHECK(f.warnings.size() == 1);

    CHECK(dvl::holder_findings({"RTSSHooksLoader64.exe", "explorer.exe"}).warnings.empty());
}

// --- The verdict -----------------------------------------------------------------------------------------------------

namespace {

dvl::RunCounts counts(uint64_t presents, uint64_t flips, double seconds = 20.0, double requested = 20.0)
{
    dvl::RunCounts c;
    c.presents = presents;
    c.flips = flips;
    c.seconds = seconds;
    c.requested_seconds = requested;
    return c;
}

} // namespace

TEST_CASE("verdict_passes_1800_presents_and_1800_flips_in_20_seconds")
{
    const dvl::Verdict v = dvl::judge(true, "", counts(1800, 1800));
    CHECK(v.pass);
    CHECK(v.line.rfind("PASS", 0) == 0);
    CHECK(contains(v.line, "1800 presents"));
    CHECK(contains(v.line, "1800 flips"));
    CHECK(contains(v.line, "20.0 s"));
    CHECK(dvl::expected_flips(20.0) == 1800);
}

TEST_CASE("verdict_counts_on_the_rate_of_the_mode_that_was_opened")
{
    // An untested headset at 64 Hz (its EDID's mode): 1280 flips in 20 s is all of them, not a miss against 90 Hz.
    dvl::RunCounts c = counts(1280, 1280);
    c.refresh_hz = 64.0;
    const dvl::Verdict v = dvl::judge(true, "", c);
    CHECK(v.pass);
    CHECK(dvl::expected_flips(20.0, 64.0) == 1280);

    // Without a rate the Dream Air's 90 Hz stands.
    CHECK(dvl::RunCounts{}.refresh_hz == 90.0);
    CHECK_FALSE(dvl::judge(true, "", counts(1280, 1280)).pass);
}

TEST_CASE("verdict_tolerates_the_run_starting_and_ending_between_two_refreshes")
{
    CHECK(dvl::judge(true, "", counts(1799, 1801)).pass);
    CHECK(dvl::judge(true, "", counts(1797, 1803)).pass);
    // Measured time, not the request: 20.05 s is 1804.5 refreshes.
    CHECK(dvl::judge(true, "", counts(1804, 1805, 20.05)).pass);
}

TEST_CASE("verdict_fails_missed_flips_and_missed_presents")
{
    dvl::Verdict v = dvl::judge(true, "", counts(1800, 1700));
    CHECK_FALSE(v.pass);
    CHECK(v.line.rfind("FAIL", 0) == 0);
    CHECK(contains(v.line, "flips"));
    CHECK(contains(v.line, "1700"));

    v = dvl::judge(true, "", counts(1500, 1800)); // the loop could not keep up with the refreshes
    CHECK_FALSE(v.pass);
    CHECK(contains(v.line, "presents"));

    // More than the display can show is wrong too (a miscounted counter).
    CHECK_FALSE(dvl::judge(true, "", counts(1800, 2000)).pass);
    CHECK_FALSE(dvl::judge(true, "", counts(2000, 1800)).pass);
    CHECK_FALSE(dvl::judge(true, "", counts(0, 0)).pass);
}

TEST_CASE("verdict_fails_failed_presents_even_when_the_counts_are_right")
{
    dvl::RunCounts c = counts(1800, 1800);
    c.failures = 3;
    const dvl::Verdict v = dvl::judge(true, "", c);
    CHECK_FALSE(v.pass);
    CHECK(contains(v.line, "3"));
    CHECK(contains(v.line, "failed"));
}

TEST_CASE("verdict_names_the_step_that_failed_and_nothing_else")
{
    const dvl::Verdict v = dvl::judge(false, "BindDevice: NVAPI would not bind the display (code -5)", counts(0, 0));
    CHECK_FALSE(v.pass);
    CHECK(v.line.rfind("FAIL", 0) == 0);
    CHECK(contains(v.line, "BindDevice"));
    CHECK(contains(v.line, "code -5"));
}

TEST_CASE("verdict_of_a_headset_left_to_windows_is_neither_a_pass_nor_a_fail")
{
    const dvl::Verdict v = dvl::judge(
        false,
        std::string("Product: the Pimax headset (PVR 0x1030)'s EDID ") + nvdm::kWindowsDrivesIt +
            " through its own HMD route and this layer leaves it alone (code 0)",
        counts(0, 0));
    CHECK_FALSE(v.pass);
    CHECK(v.not_used);
    CHECK(v.line.rfind("NOT USED: ", 0) == 0);

    // Any other failing step is a fail.
    CHECK_FALSE(dvl::judge(false, "Product: unknown display product PVR 0x1020", counts(0, 0)).not_used);
}

TEST_CASE("verdict_fails_a_run_that_stopped_early")
{
    const dvl::Verdict v = dvl::judge(true, "", counts(288, 290, 3.2, 20.0));
    CHECK_FALSE(v.pass);
    CHECK(contains(v.line, "3.2"));
    CHECK(contains(v.line, "20"));
}

TEST_CASE("verdict_counts_display_event_timeouts_when_the_mode_has_them")
{
    dvl::RunCounts c = counts(1800, 1800);
    c.have_events = true;
    c.events_ok = 1799;
    CHECK(dvl::judge(true, "", c).pass);
    CHECK(contains(dvl::judge(true, "", c).line, "display events"));

    c.events_ok = 1700;
    c.events_timeout = 99; // 5 %: the vblank thread would starve the pacer
    const dvl::Verdict v = dvl::judge(true, "", c);
    CHECK_FALSE(v.pass);
    CHECK(contains(v.line, "display event"));

    // A mode without events (NVAPI) says nothing of them.
    CHECK_FALSE(contains(dvl::judge(true, "", counts(1800, 1800)).line, "display events"));
}

TEST_CASE("verdict_scales_with_the_requested_time")
{
    CHECK(dvl::expected_flips(1.0) == 90);
    CHECK(dvl::expected_flips(5.0) == 450);
    CHECK(dvl::judge(true, "", counts(450, 450, 5.0, 5.0)).pass);
    CHECK_FALSE(dvl::judge(true, "", counts(450, 400, 5.0, 5.0)).pass);
    // A one-second run is judged on 90 +- 3.
    CHECK(dvl::judge(true, "", counts(88, 91, 1.0, 1.0)).pass);
    CHECK_FALSE(dvl::judge(true, "", counts(80, 91, 1.0, 1.0)).pass);
}

// --- The display session's result ------------------------------------------------------------------------------------

TEST_CASE("an open session prints every step as ok")
{
    const nvdm::OpenResult ok{true, nvdm::OpenStep::Done, 0, "display session open: Pimax Dream Air"};
    const std::vector<std::string> lines = dvl::describe_open_result(ok);
    REQUIRE(lines.size() == 11); // Holders through SetDisplayMode
    for (const std::string& line : lines) {
        CHECK(contains(line, "ok"));
        CHECK_FALSE(contains(line, "FAILED"));
    }
    CHECK(contains(lines.front(), "Holders"));
    CHECK(contains(lines.back(), "SetDisplayMode"));
}

TEST_CASE("a failed step is named with its message, and later steps are left out")
{
    const nvdm::OpenResult failed{false, nvdm::OpenStep::BindDevice, -5,
                                  "BindDevice: NVAPI would not bind the display (is another program using it?) (code -5)"};
    const std::vector<std::string> lines = dvl::describe_open_result(failed);
    REQUIRE(lines.size() == 9); // Holders, LoadNvapi, CheckIds, Enumerate, Luid, Product, EnableDirectMode, D3D11, BindDevice
    CHECK(contains(lines[7], "D3D11"));
    CHECK(contains(lines[7], "ok"));
    CHECK(contains(lines[8], "BindDevice"));
    CHECK(contains(lines[8], "FAILED"));
    CHECK(contains(lines[8], "code -5"));
    CHECK_FALSE(any_line_contains(lines, "CreateBackBuffers"));
}

TEST_CASE("a failure at the first step leaves just that line")
{
    const nvdm::OpenResult failed{false, nvdm::OpenStep::Holders, 0, "Holders: Pimax or SteamVR is running; stop it first"};
    const std::vector<std::string> lines = dvl::describe_open_result(failed);
    REQUIRE(lines.size() == 1);
    CHECK(contains(lines[0], "FAILED"));
    CHECK(contains(lines[0], "stop it first"));
}

TEST_CASE("a display that cannot be identified is explained: what the line means and what to do")
{
    const std::vector<std::string> lines = dvl::explain_failure(
        "Product: the display's EDID could not be read (NVAPI, then the registry), and the Dream Air's board (USB "
        "34A4:0044) is not connected, so the display is not taken to be a Dream Air (code -10003)");
    REQUIRE(lines.size() == 2);
    CHECK(lines[0].rfind("what it means: ", 0) == 0);
    CHECK(contains(lines[0], "which headset"));
    CHECK(lines[1].rfind("what to do: ", 0) == 0);
    CHECK(contains(lines[1], "DisplayPort and USB"));
    CHECK(contains(lines[1], "34A4:0044"));
    CHECK(contains(lines[1], "maintainer"));
}

TEST_CASE("a display left unidentified by MIRADOR_DIRECT_MODE_TEST_NO_EDID is explained as an unreadable EDID")
{
    const std::vector<std::string> lines = dvl::explain_failure(
        "Product: the display's EDID was not read (MIRADOR_DIRECT_MODE_TEST_NO_EDID skips every route), and the Dream "
        "Air's board (USB 34A4:0044) is not connected, so the display is not taken to be a Dream Air (code -10003)");
    REQUIRE(lines.size() == 2);
    CHECK(contains(lines[0], "which headset"));
}

TEST_CASE("an unknown product is explained apart from an unreadable EDID")
{
    const std::vector<std::string> lines = dvl::explain_failure(
        "Product: unknown display product PVR 0x1020; this layer knows Pimax Dream Air (PVR 0x1019)");
    REQUIRE(lines.size() == 2);
    CHECK(contains(lines[0], "does not know"));
    CHECK(contains(lines[0], "Dream Air"));
    CHECK(lines[1].rfind("what to do: ", 0) == 0);
}

TEST_CASE("a headset left to Windows, and an untested EDID from the registry, are explained as such")
{
    const std::vector<std::string> hmd = dvl::explain_failure(
        "Product: the Pimax headset (PVR 0x1030)'s EDID carries Microsoft's HMD block, so Windows drives it through its "
        "own HMD route and this layer leaves it alone (code 0)");
    REQUIRE(hmd.size() == 2);
    CHECK(contains(hmd[0], "Windows drives this headset itself"));
    CHECK_FALSE(contains(hmd[1], "34A4:0044"));

    const std::vector<std::string> registry = dvl::explain_failure(
        "Product: the display's own EDID could not be read through NVAPI, and the one Windows kept in the registry "
        "names the Pimax headset (PVR 0x101b), which is not tested: its mode can't be trusted to be the connected "
        "headset's (code -10003)");
    REQUIRE(registry.size() == 2);
    CHECK(contains(registry[0], "earlier connection"));
    CHECK_FALSE(contains(registry[1], "34A4:0044"));
}

TEST_CASE("any other failing step points to the README's troubleshooting table, by its name")
{
    const std::vector<std::string> lines =
        dvl::explain_failure("BindDevice: NVAPI would not bind the display (is another program using it?) (code -5)");
    REQUIRE(lines.size() == 1);
    CHECK(lines[0].rfind("what to do: ", 0) == 0);
    CHECK(contains(lines[0], "Troubleshooting"));
    CHECK(contains(lines[0], "BindDevice:"));

    CHECK(dvl::explain_failure("not a failure line").empty());
    CHECK(dvl::explain_failure("").empty());
}

TEST_CASE("the layer's log gives its last error line, as the session worded it")
{
    const std::string log =
        "2026-10-31 09:00:00.000 [pid 77 tid 5] error Enumerate: no direct-mode display appeared\r\n"
        "2026-10-31 09:00:01.000 [pid 77 tid 5] warn  warning: untested driver version 700.12\r\n"
        "2026-10-31 09:00:02.000 [pid 77 tid 5] error Product: the display's EDID could not be read (code -10003)\r\n"
        "2026-10-31 09:00:03.000 [pid 78 tid 5] error Holders: another process's line\r\n"
        "2026-10-31 09:00:04.000 [pid 77 tid 5] info  display query on X: the layer's display is not listed\r\n";
    CHECK(dvl::layer_log_facts(log, 77).failure == "Product: the display's EDID could not be read (code -10003)");
    CHECK(dvl::layer_log_facts(log, 78).failure == "Holders: another process's line");
    CHECK_FALSE(dvl::layer_log_facts(log, 79).failure.has_value());
}

TEST_CASE("the driver version is taken from the session's lines")
{
    CHECK(dvl::driver_version_from_lines({"hello", "driver version 621.01", "more"}) == "621.01");
    CHECK(dvl::driver_version_from_lines(
              {"warning: untested driver version 700.12; trying it anyway (tested: 621.01)"}) == "700.12");
    CHECK(dvl::driver_version_from_lines({"warning: the driver version could not be read; trying it anyway"}).empty());
    CHECK(dvl::driver_version_from_lines({}).empty());
}

TEST_CASE("the EDID route is the last one the layer's log names")
{
    CHECK(dvl::edid_route_from_text("12:00:01 info EDID route: NVAPI\n") == "NVAPI");
    CHECK(dvl::edid_route_from_text("a\nEDID route: registry\r\nb\n") == "registry");
    // An older line of a log that was added to loses to the newest.
    CHECK(dvl::edid_route_from_text("EDID route: NVAPI\nlater\nEDID route: registry\n") == "registry");
    CHECK_FALSE(dvl::edid_route_from_text("nothing about it\n").has_value());
    CHECK_FALSE(dvl::edid_route_from_text("").has_value());
    CHECK_FALSE(dvl::edid_route_from_text("EDID route: \n").has_value());
}

TEST_CASE("the layer's log gives the EDID route, the trial import and the memory-type fallback")
{
    // The layer's line stamp (layer/log.cpp): date, time, "[pid N tid M]", level.
    const std::string log =
        "2026-10-31 09:59:00.000 [pid 77 tid 5] info  vkCreateInstance: ...\r\n"
        "2026-10-31 09:59:01.000 [pid 77 tid 5] info  trial import on device 0001: the 3 back-buffers import as A and "
        "as B (imported and released)\r\n"
        "2026-10-31 09:59:02.000 [pid 77 tid 5] info  EDID route: NVAPI\r\n"
        "2026-10-31 09:59:03.000 [pid 77 tid 5] info  trial import on device 0002: the 3 back-buffers import as A and "
        "as B (imported and released)\r\n"
        "2026-10-31 09:59:04.000 [pid 77 tid 6] warn  import on device 0002: vkGetMemoryWin32HandlePropertiesKHR gave "
        "no memory types for back-buffer 0003, so the memory type comes from the image's memoryTypeBits (0x7) alone, "
        "as Monado's target did\r\n"
        "2026-10-31 09:59:05.000 [pid 77 tid 5] info  vkCreateSwapchainKHR: ...";
    const dvl::LayerLogFacts facts = dvl::layer_log_facts(log, 77);
    CHECK(facts.edid_route == "NVAPI");
    REQUIRE(facts.trial_import.has_value());
    CHECK(facts.trial_import->rfind("2026-10-31 09:59:03.000 [pid 77 tid 5] info  trial import on device 0002", 0) ==
          0); // the last one
    CHECK(facts.trial_import->find('\r') == std::string::npos);
    REQUIRE(facts.memory_fallback.has_value());
    CHECK(facts.memory_fallback->rfind("2026-10-31 09:59:04.000 [pid 77 tid 6] warn", 0) == 0);

    const dvl::LayerLogFacts none = dvl::layer_log_facts("[pid 77 tid 1] one line\n[pid 77 tid 1] another", 77);
    CHECK_FALSE(none.edid_route.has_value());
    CHECK_FALSE(none.trial_import.has_value());
    CHECK_FALSE(none.memory_fallback.has_value());
    // A last line without a line ending is a line.
    CHECK(dvl::layer_log_facts("a\n[pid 77 tid 1] EDID route: registry", 77).edid_route == "registry");
    CHECK(dvl::layer_log_facts("a\n[pid 77 tid 1] trial import on device 9: ok", 77).trial_import ==
          "[pid 77 tid 1] trial import on device 9: ok");
}

TEST_CASE("only this process's lines of the layer's log count")
{
    // layer.log can hold another process's lines: an older run's file when this run's layer wrote none, or a line a
    // declined process added. They must never be printed as this run's (Task 12 review). The layer stamps each line
    // "[pid N tid M]"; a pid that only begins like ours is another one.
    const std::string log =
        "2026-10-31 09:00:00.000 [pid 4242 tid 7] info  EDID route: registry\r\n"
        "2026-10-31 09:00:01.000 [pid 4242 tid 7] info  trial import on device 0001: ok\r\n"
        "2026-10-31 09:00:02.000 [pid 4242 tid 8] warn  import on device 0001: ... so the memory type comes from the "
        "image's memoryTypeBits (0x7) alone\r\n"
        "2026-10-31 10:00:00.000 [pid 424 tid 9] info  EDID route: NVAPI\r\n"
        "2026-10-31 10:00:01.000 [pid 42 tid 9] info  trial import on device 0042: ok\r\n"
        "EDID route: registry, in a line with no stamp\r\n";

    const dvl::LayerLogFacts old_run = dvl::layer_log_facts(log, 4242);
    CHECK(old_run.edid_route == "registry");
    CHECK(old_run.trial_import == "2026-10-31 09:00:01.000 [pid 4242 tid 7] info  trial import on device 0001: ok");
    CHECK(old_run.memory_fallback.has_value());

    const dvl::LayerLogFacts this_run = dvl::layer_log_facts(log, 424);
    CHECK(this_run.edid_route == "NVAPI");
    CHECK_FALSE(this_run.trial_import.has_value());
    CHECK_FALSE(this_run.memory_fallback.has_value());

    const dvl::LayerLogFacts other = dvl::layer_log_facts(log, 42);
    CHECK_FALSE(other.edid_route.has_value());
    CHECK(other.trial_import == "2026-10-31 10:00:01.000 [pid 42 tid 9] info  trial import on device 0042: ok");

    const dvl::LayerLogFacts nothing = dvl::layer_log_facts(log, 1);
    CHECK_FALSE(nothing.edid_route.has_value());
    CHECK_FALSE(nothing.trial_import.has_value());
    CHECK_FALSE(nothing.memory_fallback.has_value());
}

TEST_CASE("an NVIDIA driver version is printed the way NVIDIA numbers it")
{
    // 621.01: major in the top 10 bits, minor in the next 8.
    const uint32_t v621_01 = (621u << 22) | (1u << 14);
    CHECK(dvl::nvidia_driver_text(v621_01) == "621.01");
    CHECK(dvl::nvidia_driver_text((572u << 22) | (83u << 14)) == "572.83");
    CHECK(dvl::nvidia_driver_text(0) == "0.00");
}

TEST_CASE("a missing layer is explained by what the loader says about it")
{
    // Not registered at all.
    dvl::LayerFacts facts;
    facts.layer_listed = false;
    std::vector<std::string> advice = dvl::diagnose_missing_layer(facts);
    CHECK(any_line_contains(advice, "VK_LAYER_MIRADOR_direct_mode"));
    CHECK(any_line_contains(advice, "VK_IMPLICIT_LAYER_PATH"));
    CHECK_FALSE(any_line_contains(advice, "elevated"));

    // An elevated process ignores the layer-path variables.
    facts.elevated = true;
    advice = dvl::diagnose_missing_layer(facts);
    CHECK(any_line_contains(advice, "elevated"));

    // Listed but off.
    facts = dvl::LayerFacts{};
    facts.layer_listed = true;
    facts.disable_var_set = true;
    advice = dvl::diagnose_missing_layer(facts);
    CHECK(any_line_contains(advice, "DISABLE_MIRADOR_DIRECT_MODE"));

    // Listed, not disabled, still no extension: the manifest and the loader disagree.
    facts.disable_var_set = false;
    advice = dvl::diagnose_missing_layer(facts);
    CHECK(any_line_contains(advice, "VK_EXT_display_surface_counter"));
}
