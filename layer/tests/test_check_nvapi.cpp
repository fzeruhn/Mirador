// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// mirador-display-check, default mode (design 7), run against a display session on a fake NVAPI whose clock runs at
// the real rate: the tool's frame loop drives the real frame model, display loop and display thread, and the fake's
// counter flips every 1/90 s. Nothing here reaches the real NVAPI, the real D3D11 device or the real display session
// (Pimax runs on this machine).

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include "check/modes.hpp"
#include "check/options.hpp"
#include "check/report.hpp"
#include "core/display_session.hpp"
#include "fakes/fake_api.hpp"
#include "fakes/fake_clock.hpp"

namespace {

struct NvapiRig {
    nvdm::RunningClock clock;
    nvdm::FakeApi api{clock};
    std::vector<std::string> running; // what the session's holder check sees
    bool board = false;               // what the session's board check sees (design 6.3's last route)
    std::vector<std::string> session_log;
    std::unique_ptr<nvdm::DisplaySession> session;
    dvl::CaptureOutput out;
    dvl::Options options;

    explicit NvapiRig(int seconds = 2)
    {
        api.strict_bind = true;
        nvdm::SessionDeps deps{api, clock};
        deps.make_d3d11 = [](const LUID&) { return reinterpret_cast<IUnknown*>(static_cast<uintptr_t>(0x7000)); };
        deps.processes = [this]() { return running; };
        deps.pin_module = []() {};
        deps.board_present = [this]() { return board; };
        deps.wait_ms = 300;
        deps.sleep = [this](int64_t ns) { clock.advance(ns); };
        deps.log = [this](const std::string& line) { session_log.push_back(line); };
        session = std::make_unique<nvdm::DisplaySession>(std::move(deps));
        options.seconds = seconds;
    }

    dvl::NvapiEnv env(std::vector<LUID> adapters = {LUID{0x1234, 0x56}})
    {
        dvl::NvapiEnv e;
        e.session = session.get();
        e.adapters = std::move(adapters);
        e.clear_back_buffers = false; // the fake's handles are not D3D11 textures
        e.session_lines = [this] { return session_log; };
        return e;
    }

    dvl::ModeResult run(std::vector<LUID> adapters = {LUID{0x1234, 0x56}})
    {
        return dvl::run_nvapi_check(env(std::move(adapters)), options, out);
    }

    bool printed(const std::string& part) const
    {
        const std::string text = out.text();
        return text.find(part) != std::string::npos;
    }
};

} // namespace

TEST_CASE("nvapi_mode_presents_and_counts_flips_against_the_fake")
{
    NvapiRig rig(2);
    const dvl::ModeResult result = rig.run();
    INFO(rig.out.text());
    REQUIRE(result.steps_ok);

    // 2 s at 90 Hz: 180 presents and flips, counted over the same window.
    const uint64_t expected = dvl::expected_flips(result.counts.seconds);
    CHECK(result.counts.seconds >= 1.99);
    CHECK(result.counts.presents >= expected * 9 / 10);
    CHECK(result.counts.presents <= expected * 11 / 10);
    CHECK(result.counts.flips >= expected * 9 / 10);
    CHECK(result.counts.flips <= expected * 11 / 10);
    CHECK(result.counts.failures == 0);
    const dvl::Verdict verdict = dvl::judge(result.steps_ok, result.failed_step, result.counts);
    INFO(verdict.line);
    CHECK(verdict.pass);

    // The fake NVAPI got the Presents, of the session's three back-buffers.
    CHECK(rig.api.count("present") >= expected * 9 / 10);
    for (const nvdm::FakeApi::Call& c : rig.api.calls()) {
        if (c.name == "present") {
            CHECK(c.arg >= 0x100);
            CHECK(c.arg <= 0x102);
            CHECK(c.status == 0);
        }
    }

    // What it printed: the driver version, every step, the EDID route, the display.
    CHECK(rig.printed("NVIDIA driver version: 621.01"));
    CHECK(rig.printed("EDID route: NVAPI"));
    CHECK(rig.printed("Pimax Dream Air"));
    CHECK(rig.printed("BindDevice"));
    CHECK(rig.printed("every private NVAPI id resolves"));
    CHECK_FALSE(rig.printed("FAILED"));
    CHECK(rig.printed("presenting for 2 s (180 presents at 90 Hz expected)"));
}

TEST_CASE("nvapi_mode_reports_the_registry_edid_route_when_that_is_the_one_used")
{
    NvapiRig rig(1);
    rig.api.edid_route = nvdm::EdidRoute::Registry;
    const dvl::ModeResult result = rig.run();
    REQUIRE(result.steps_ok);
    CHECK(rig.printed("EDID route: registry"));
}

TEST_CASE("nvapi_mode_reports_the_board_route_and_the_sessions_warning_when_no_edid_is_readable")
{
    NvapiRig rig(1);
    rig.api.edid.clear();
    rig.board = true;
    const dvl::ModeResult result = rig.run();
    INFO(rig.out.text());
    REQUIRE(result.steps_ok);
    CHECK(rig.printed("EDID route: board"));
    // The session's warning, which run_nvapi_mode's sink prints as it comes ("  session: warning: ...").
    CHECK(std::any_of(rig.session_log.begin(), rig.session_log.end(), [](const std::string& line) {
        return line.rfind("warning: ", 0) == 0 && line.find("34A4:0044") != std::string::npos;
    }));
}

TEST_CASE("nvapi_mode_explains_a_display_it_cannot_identify")
{
    NvapiRig rig(1);
    rig.api.edid.clear();
    rig.board = false;
    const dvl::ModeResult result = rig.run();
    INFO(rig.out.text());
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.rfind("Product: ", 0) == 0);
    CHECK(rig.printed("what it means: "));
    CHECK(rig.printed("what to do: "));
    CHECK(rig.api.count("present") == 0);
}

TEST_CASE("nvapi_mode_tries_the_next_adapter_when_the_first_does_not_drive_the_display")
{
    NvapiRig rig(1);
    const dvl::ModeResult result = rig.run({LUID{0x7777, 0}, LUID{0x1234, 0x56}});
    INFO(rig.out.text());
    REQUIRE(result.steps_ok);
    CHECK(rig.printed("Luid"));
    CHECK_FALSE(rig.printed("FAILED"));
}

TEST_CASE("nvapi_mode_fails_at_luid_when_no_adapter_drives_the_display")
{
    NvapiRig rig(1);
    const dvl::ModeResult result = rig.run({LUID{0x7777, 0}});
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("Luid") != std::string::npos);
    CHECK(result.failed_step.find("not the queried device's") != std::string::npos);
    CHECK(rig.api.count("present") == 0);

    // No NVIDIA adapter at all: one try that still names the display's adapter.
    NvapiRig none(1);
    const dvl::ModeResult nothing = none.run({});
    CHECK_FALSE(nothing.steps_ok);
    CHECK(nothing.failed_step.find("Luid") != std::string::npos);
}

TEST_CASE("nvapi_mode_stops_at_the_holder_check_of_the_session_without_touching_nvapi")
{
    NvapiRig rig(1);
    rig.running = {"pi_server.exe"};
    const dvl::ModeResult result = rig.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("Pimax or SteamVR is running") != std::string::npos);
    CHECK(rig.api.calls().empty()); // not one NVAPI call
    CHECK(rig.printed("Holders"));
    CHECK(rig.printed("FAILED"));
    const dvl::Verdict verdict = dvl::judge(result.steps_ok, result.failed_step, result.counts);
    CHECK_FALSE(verdict.pass);
    CHECK(verdict.line.rfind("FAIL: Holders", 0) == 0);
}

TEST_CASE("nvapi_mode_names_a_missing_private_id")
{
    NvapiRig rig(1);
    rig.api.missing_id = "Present";
    const dvl::ModeResult result = rig.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("CheckIds") != std::string::npos);
    CHECK(result.failed_step.find("'Present'") != std::string::npos);
    CHECK(rig.printed("NVIDIA driver version: 621.01")); // known by then, and printed
}

TEST_CASE("nvapi_mode_names_the_step_and_status_of_a_refused_bind")
{
    NvapiRig rig(1);
    rig.api.queue(nvdm::FakeApi::Fn::BindDevice, -5);
    const dvl::ModeResult result = rig.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("BindDevice") != std::string::npos);
    CHECK(result.failed_step.find("code -5") != std::string::npos);
    CHECK(rig.printed("BindDevice"));
    CHECK(rig.printed("FAILED"));
}

TEST_CASE("nvapi_mode_fails_when_the_display_does_not_appear")
{
    NvapiRig rig(1);
    rig.api.appears_at_ns = INT64_MAX / 2;
    const dvl::ModeResult result = rig.run();
    CHECK_FALSE(result.steps_ok);
    CHECK(result.failed_step.find("Enumerate") != std::string::npos);
    CHECK(result.failed_step.find("board awake") != std::string::npos);
}

TEST_CASE("nvapi_mode_gives_a_failing_verdict_instead_of_hanging_when_the_display_is_not_flipping")
{
    NvapiRig rig(1);
    rig.api.stop_counter();
    const dvl::ModeResult result = rig.run();
    INFO(rig.out.text());
    // Whether it ends in a failed step or in counts, it ends, and it is not a pass.
    const dvl::Verdict verdict = dvl::judge(result.steps_ok, result.failed_step, result.counts);
    CHECK_FALSE(verdict.pass);
    CHECK(result.counts.flips == 0);
    CHECK(result.counts.presents <= 2); // NVAPI is given one Present while flips are stopped, no more
}

// --- Closing the console ---------------------------------------------------------------------------------------------

TEST_CASE("closing_the_console_runs_the_stop_hook_and_waits_at_most_100_ms")
{
    using namespace std::chrono_literals;
    using Clock = std::chrono::steady_clock;
    struct Clean {
        ~Clean()
        {
            dvl::set_interrupt_stop_hook({});
            dvl::clear_interrupted_for_tests();
        }
    } clean;

    // The process ends when the handler returns: the hook runs first, and the loops see the flag.
    std::atomic<int> runs{0};
    dvl::set_interrupt_stop_hook([&] { runs++; });
    CHECK(dvl::on_interrupt_event(CTRL_CLOSE_EVENT) == FALSE); // the default handler then ends the process
    CHECK(runs == 1);
    CHECK(dvl::interrupted());
    dvl::clear_interrupted_for_tests();

    // A hook that does not come back (a display thread stuck inside NVAPI): the handler gives up after 100 ms.
    std::atomic<bool> release{false};
    dvl::set_interrupt_stop_hook([&] {
        while (!release) {
            Sleep(1);
        }
    });
    const Clock::time_point start = Clock::now();
    CHECK(dvl::on_interrupt_event(CTRL_CLOSE_EVENT) == FALSE);
    const Clock::duration took = Clock::now() - start;
    CHECK(took >= 95ms);
    CHECK(took < 400ms);
    release = true;
    dvl::set_interrupt_stop_hook({}); // waits for that run to end

    // Ctrl+C leaves the stopping to the loops (they wind down and join the thread) and keeps the process; a second one
    // ends it like closing the console does.
    runs = 0;
    dvl::clear_interrupted_for_tests();
    dvl::set_interrupt_stop_hook([&] { runs++; });
    CHECK(dvl::on_interrupt_event(CTRL_C_EVENT) == TRUE);
    CHECK(runs == 0);
    CHECK(dvl::interrupted());
    CHECK(dvl::on_interrupt_event(CTRL_C_EVENT) == FALSE);
    CHECK(runs == 1);
}

TEST_CASE("nvapi_mode_stops_the_display_thread_when_the_console_is_closed")
{
    // Closing the console ends the process as soon as the console handler returns. The NVAPI mode's display thread is
    // stopped by then (waited for up to 100 ms, as the layer does in Monado, design 6.10), so it is not killed inside
    // NVAPI, and the run winds down as on Ctrl+C.
    using namespace std::chrono_literals;
    using Clock = std::chrono::steady_clock;
    struct Clean {
        ~Clean() { dvl::clear_interrupted_for_tests(); }
    } clean;

    NvapiRig rig(3);
    dvl::ModeResult result;
    std::thread run([&] { result = rig.run(); });
    const Clock::time_point give_up = Clock::now() + 3s;
    while (rig.api.count("present") < 10 && Clock::now() < give_up) {
        std::this_thread::sleep_for(5ms);
    }
    const bool presenting = rig.api.count("present") >= 10;
    const Clock::time_point start = Clock::now();
    const BOOL handled = dvl::on_interrupt_event(CTRL_CLOSE_EVENT);
    const Clock::duration took = Clock::now() - start;
    const size_t calls = rig.api.calls().size();
    std::this_thread::sleep_for(60ms); // five refreshes
    const size_t calls_later = rig.api.calls().size();
    const bool flagged = dvl::interrupted();
    run.join();

    REQUIRE(presenting);
    CHECK(handled == FALSE);
    CHECK(took < 400ms);
    CHECK(calls_later == calls); // no NVAPI call once the handler returned: the display thread had stopped
    CHECK(flagged);
    CHECK(result.failed_step == "interrupted");
}
