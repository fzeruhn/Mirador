// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The display session (design 6.3) against FakeApi in strict-bind mode. process_session() and make_nvapi() are never
// used here: they would talk to the real driver, and Pimax may be running.

#include <catch2/catch_test_macros.hpp>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "core/display_session.hpp"
#include "fakes/fake_api.hpp"
#include "fakes/fake_clock.hpp"

namespace {

using nvdm::FakeApi;
using nvdm::FakeClock;
using nvdm::OpenStep;
using Fn = nvdm::FakeApi::Fn;
using Names = std::vector<std::string>;

constexpr int64_t kMs = 1'000'000;

// A happy Dream Air on a strict fake, and everything the session is given. Every hook records what it saw.
struct Rig {
    FakeClock clock;
    FakeApi api{clock};
    Names running;      // what processes() reports
    Names log;          // what the session logged
    int pins = 0;
    int processes_listed = 0;
    int d3d11_made = 0;
    LUID d3d11_luid{};
    IUnknown* device = reinterpret_cast<IUnknown*>(0x7000);
    std::vector<int64_t> sleeps;
    size_t calls_at_pin = 0; // How many Api calls had been made when pin_module ran.
    bool ignore_holders = false;
    bool skip_edid = false;
    uint32_t wait_ms = 10000;
    bool board = true;    // what board_present() reports: the Dream Air's board is connected
    int board_checks = 0; // how often it was asked

    Rig() { api.strict_bind = true; }

    nvdm::SessionDeps deps()
    {
        nvdm::SessionDeps d{api, clock};
        d.make_d3d11 = [this](const LUID& luid) {
            api.note("make_d3d11");
            d3d11_made++;
            d3d11_luid = luid;
            return device;
        };
        d.processes = [this]() {
            processes_listed++;
            return running;
        };
        d.pin_module = [this]() {
            pins++;
            calls_at_pin = api.calls().size();
        };
        d.board_present = [this]() {
            board_checks++;
            return board;
        };
        d.ignore_holders = ignore_holders;
        d.skip_edid = skip_edid;
        d.wait_ms = wait_ms;
        d.sleep = [this](int64_t ns) {
            sleeps.push_back(ns);
            clock.advance(ns);
        };
        d.log = [this](const std::string& line) { log.push_back(line); };
        return d;
    }

    // The calls made after the first `skip`, by name, without driver_version.
    Names names_after(size_t skip) const
    {
        Names out;
        const std::vector<FakeApi::Call> all = api.calls();
        for (size_t i = skip; i < all.size(); i++) {
            if (all[i].name != "driver_version") {
                out.push_back(all[i].name);
            }
        }
        return out;
    }

    size_t warnings() const
    {
        size_t n = 0;
        for (const std::string& line : log) {
            n += line.rfind("warning", 0) == 0 ? 1 : 0;
        }
        return n;
    }

    size_t lines_containing(const std::string& text) const
    {
        size_t n = 0;
        for (const std::string& line : log) {
            n += line.find(text) != std::string::npos ? 1 : 0;
        }
        return n;
    }
};

bool contains(const std::string& text, const std::string& part)
{
    return text.find(part) != std::string::npos;
}

// Whether another thread can take the display-session lock right now.
bool lock_is_free(nvdm::DisplaySession& session)
{
    bool free = false;
    std::thread([&] {
        free = session.lock().try_lock();
        if (free) {
            session.lock().unlock();
        }
    }).join();
    return free;
}

} // namespace

TEST_CASE("open_runs_steps_in_order")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    CHECK_FALSE(session.is_open());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    REQUIRE(result.ok);
    CHECK(result.step == OpenStep::Done);
    CHECK(result.code == 0);
    CHECK(session.is_open());
    CHECK(rig.pins == 1);

    const Names expected = {
        // 1
        "initialize", "first_missing_id", "enumerate", "adapter_luid", "read_edid",
        // 2
        "enable_direct_mode", "make_d3d11", "bind_device",
        // 3
        "create_back_buffer", "create_back_buffer", "create_back_buffer", "set_display_mode",
    };
    CHECK(rig.api.names({"driver_version"}) == expected);

    // The arguments: the vendor to enumerate and enable, the display to ask about and bind, the adapter to build on.
    const std::vector<FakeApi::Call> calls = rig.api.calls();
    for (const FakeApi::Call& call : calls) {
        if (call.name == "enumerate" || call.name == "enable_direct_mode") {
            CHECK(call.arg == nvdm::kVendorPvr);
        }
        if (call.name == "adapter_luid" || call.name == "read_edid" || call.name == "bind_device") {
            CHECK(call.arg == rig.api.display_ids[0]);
        }
    }
    CHECK(rig.d3d11_luid.LowPart == rig.api.luid.LowPart);
    CHECK(rig.d3d11_luid.HighPart == rig.api.luid.HighPart);
    CHECK(rig.api.bound_device == rig.device);

    // SetDisplayMode got the Dream Air's mode and answered -208, which is success.
    REQUIRE(rig.api.modes_set.size() == 1);
    CHECK(rig.api.modes_set[0].width == 7104);
    CHECK(rig.api.modes_set[0].height == 3840);
    CHECK(rig.api.modes_set[0].refresh_num == 90000);
    CHECK(rig.api.modes_set[0].refresh_den == 1000);
    CHECK(calls.back().name == "set_display_mode");
    CHECK(calls.back().status == nvdm::kSetModeOk);
    CHECK(rig.api.modes_created.size() == 3);
    CHECK(rig.api.modes_created[0].width == 7104);
}

TEST_CASE("an open session answers for the display")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    REQUIRE(session.open(rig.api.luid).ok);

    CHECK(std::string(session.product().name) == "Pimax Dream Air");
    CHECK(session.product().pnp_product == 0x1019);
    CHECK(session.luid().LowPart == rig.api.luid.LowPart);
    CHECK(session.luid().HighPart == rig.api.luid.HighPart);
    CHECK(session.edid_route() == nvdm::EdidRoute::Nvapi);
    CHECK(&session.api() == &rig.api);

    const nvdm::Mode mode = session.mode();
    CHECK(mode.version == 0x00020030);
    CHECK(mode.width == 7104);
    CHECK(mode.height == 3840);
    CHECK(mode.refresh_num == 90000);
    CHECK(mode.refresh_den == 1000);
    CHECK(mode.bpp == 32);
    CHECK(mode.flags == 1);

    for (uint32_t i = 0; i < nvdm::kBackBufferCount; i++) {
        CHECK(session.back_buffer(i).tag == rig.api.back_buffers[i].tag);
        CHECK(session.back_buffer(i).shared == rig.api.back_buffers[i].shared);
    }
    CHECK_THROWS(session.back_buffer(nvdm::kBackBufferCount));

    // The display-session lock is the caller's: it can be taken and released.
    CHECK(lock_is_free(session));
}

TEST_CASE("open holds no display-session lock while it calls the driver")
{
    // Design invariant 7: no layer lock is held during an NVAPI call.
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    int calls = 0;
    bool always_free = true;
    rig.api.on_call = [&](const char*) {
        calls++;
        always_free = always_free && lock_is_free(session);
    };
    REQUIRE(session.open(rig.api.luid).ok);
    CHECK(calls > 10);
    CHECK(always_free);
}

TEST_CASE("the layer is pinned before the first driver call")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    REQUIRE(session.open(rig.api.luid).ok);
    // A session that fails half way keeps its state in the process too, so the DLL must already be pinned.
    CHECK(rig.pins == 1);
    CHECK(rig.calls_at_pin == 0);
}

TEST_CASE("holder_check_runs_first")
{
    Rig rig;
    rig.running = {"explorer.exe", "Pi_Server.exe"};
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Holders);
    CHECK(contains(result.message, "pi_server.exe"));
    CHECK(contains(result.message, "stop it first"));
    CHECK(rig.api.calls().empty());
    CHECK(rig.pins == 0);
    CHECK_FALSE(session.is_open());
    REQUIRE(rig.log.size() == 1);
    CHECK(rig.log[0] == "error: " + result.message); // the failure line is an error line (Level::Error in the layer)
}

TEST_CASE("holder_check_runs_first: vrcompositor holds the display too, the board's other programs do not")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());

    rig.running = {"vrcompositor.exe"};
    nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Holders);
    CHECK(contains(result.message, "vrcompositor.exe"));

    // Pimax's client and the gaze provider hold the board, not the display (the check tool and the start script
    // look at those).
    rig.running = {"PimaxClient.exe", "vrss_gaze_provider.exe", "vrserver.exe", "monado-service.exe"};
    CHECK(session.open(rig.api.luid).ok);
}

TEST_CASE("holder_check_runs_first: ignore_holders proceeds")
{
    Rig rig;
    rig.running = {"pi_server.exe"};
    rig.ignore_holders = true;
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK(result.ok);
    CHECK(rig.processes_listed == 0); // The list is not even taken.
    CHECK(rig.pins == 1);
}

TEST_CASE("the holder check runs on every attempt, not only the first")
{
    Rig rig;
    rig.api.appears_at_ns = 12'000 * kMs;
    nvdm::DisplaySession session(rig.deps());
    CHECK(session.open(rig.api.luid).step == OpenStep::Enumerate);

    // Pimax starts between the attempts.
    rig.running = {"pi_server.exe"};
    const size_t before = rig.api.calls().size();
    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Holders);
    CHECK(rig.api.calls().size() == before);

    rig.running.clear();
    CHECK(session.open(rig.api.luid).ok);
}

TEST_CASE("session_retries_after_board_wakes")
{
    Rig rig;
    rig.api.appears_at_ns = 12'000 * kMs; // The board wakes 12 s in; the wait is 10 s.
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::Enumerate);
    CHECK(rig.clock.now_ns() == 10'000 * kMs); // It waited wait_ms, no more.
    CHECK_FALSE(session.is_open());
    CHECK(rig.api.count("enable_direct_mode") == 0);

    const nvdm::OpenResult second = session.open(rig.api.luid);
    CHECK(second.ok);
    CHECK(rig.clock.now_ns() == 12'000 * kMs); // It found the display as soon as it was there.
    CHECK(session.is_open());

    // The first attempt's progress stood: NVAPI loaded once, the ids and the driver checked once.
    CHECK(rig.api.count("initialize") == 1);
    CHECK(rig.api.count("first_missing_id") == 1);
    CHECK(rig.api.count("driver_version") == 1);
    CHECK(rig.pins == 1);
}

TEST_CASE("the enumerate wait polls every 100 ms and stops at wait_ms")
{
    Rig rig;
    rig.api.appears_at_ns = 1'000'000 * kMs; // Never, in this test.
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Enumerate);
    CHECK(contains(result.message, "10000 ms"));

    int64_t total = 0;
    for (int64_t ns : rig.sleeps) {
        CHECK(ns > 0);
        CHECK(ns <= 100 * kMs);
        total += ns;
    }
    CHECK(total == 10'000 * kMs);
    CHECK(rig.sleeps.size() == 100);
    CHECK(rig.api.count("enumerate") == 101); // One at the start, one after each sleep: the last one at the deadline.
}

TEST_CASE("the enumerate wait ends on the deadline, with a short last sleep")
{
    Rig rig;
    rig.wait_ms = 250;
    rig.api.appears_at_ns = 1'000'000 * kMs;
    nvdm::DisplaySession session(rig.deps());

    CHECK(session.open(rig.api.luid).step == OpenStep::Enumerate);
    CHECK(rig.sleeps == std::vector<int64_t>{100 * kMs, 100 * kMs, 50 * kMs});
    CHECK(rig.clock.now_ns() == 250 * kMs);
    CHECK(rig.api.count("enumerate") == 4);
}

TEST_CASE("a wait of zero asks once and does not sleep")
{
    Rig rig;
    rig.wait_ms = 0;
    rig.api.appears_at_ns = 1'000'000 * kMs;
    nvdm::DisplaySession session(rig.deps());

    CHECK(session.open(rig.api.luid).step == OpenStep::Enumerate);
    CHECK(rig.sleeps.empty());
    CHECK(rig.api.count("enumerate") == 1);
}

TEST_CASE("a display that is already there needs no wait")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    REQUIRE(session.open(rig.api.luid).ok);
    CHECK(rig.sleeps.empty());
    CHECK(rig.api.count("enumerate") == 1);
}

TEST_CASE("an enumerate error is reported at once, with its code")
{
    Rig rig;
    rig.api.queue(Fn::Enumerate, -9);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Enumerate);
    CHECK(result.code == -9);
    CHECK(contains(result.message, "-9"));
    CHECK(rig.sleeps.empty());
    CHECK(rig.api.count("enumerate") == 1);
}

TEST_CASE("luid_mismatch_stops_before_enable")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    const LUID other{0x9999, 0x7};

    const nvdm::OpenResult result = session.open(other);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Luid);
    CHECK(rig.api.count("enable_direct_mode") == 0);
    CHECK(rig.api.count("read_edid") == 0);
    CHECK(rig.d3d11_made == 0);
    CHECK_FALSE(session.is_open());

    // The right device, asked later, goes on from there; the adapter is not asked again.
    const nvdm::OpenResult later = session.open(rig.api.luid);
    CHECK(later.ok);
    CHECK(rig.api.count("adapter_luid") == 1);
    CHECK(rig.api.count("enumerate") == 1);
}

TEST_CASE("an open session refuses another adapter without a driver call")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    REQUIRE(session.open(rig.api.luid).ok);

    const size_t before = rig.api.calls().size();
    const nvdm::OpenResult other = session.open(LUID{0x9999, 0x7});
    CHECK_FALSE(other.ok);
    CHECK(other.step == OpenStep::Luid);
    CHECK(session.is_open());
    CHECK(rig.api.calls().size() == before);

    CHECK(session.open(rig.api.luid).ok);
}

TEST_CASE("bind_refused_then_retried")
{
    Rig rig;
    rig.api.queue(Fn::BindDevice, -5);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::BindDevice);
    CHECK(first.code == -5);
    CHECK(contains(first.message, "-5"));
    CHECK(rig.api.count("create_back_buffer") == 0);
    const size_t mark = rig.api.calls().size();

    const nvdm::OpenResult second = session.open(rig.api.luid);
    CHECK(second.ok);
    // The second attempt starts at the bind: nothing before it is repeated.
    const Names rest = {"bind_device", "create_back_buffer", "create_back_buffer",
                        "create_back_buffer", "set_display_mode"};
    CHECK(rig.names_after(mark) == rest);
    CHECK(rig.api.count("enable_direct_mode") == 1);
    CHECK(rig.api.count("bind_device") == 2);
    CHECK(rig.api.count("initialize") == 1);
    CHECK(rig.api.count("enumerate") == 1);
    CHECK(rig.api.count("read_edid") == 1);
    CHECK(rig.d3d11_made == 1);
    CHECK(rig.pins == 1);
}

TEST_CASE("missing_id_named")
{
    Rig rig;
    rig.api.missing_id = "Present";
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::CheckIds);
    CHECK(contains(result.message, "Present"));
    CHECK(result.code == nvdm::kNoEntry);
    CHECK(rig.api.count("enumerate") == 0);
    CHECK(rig.api.count("initialize") == 1);

    // Checked again on the next attempt (the ids are not "checked" until they pass); NVAPI is not loaded again.
    rig.api.missing_id.clear();
    CHECK(session.open(rig.api.luid).ok);
    CHECK(rig.api.count("initialize") == 1);
    CHECK(rig.api.count("first_missing_id") == 2);
}

TEST_CASE("untested_driver_warns_but_continues")
{
    Rig rig;
    rig.api.driver = "630.00";
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK(result.ok);
    CHECK(rig.warnings() == 1);
    CHECK(rig.lines_containing("630.00") == 1);
    for (const std::string& line : rig.log) {
        if (contains(line, "630.00")) {
            CHECK(line.rfind("warning", 0) == 0);
            CHECK(contains(line, "621.01")); // Says what was tested.
        }
    }
}

TEST_CASE("a tested driver is logged without a warning, once")
{
    Rig rig;
    rig.api.appears_at_ns = 12'000 * kMs;
    nvdm::DisplaySession session(rig.deps());
    session.open(rig.api.luid);
    session.open(rig.api.luid);

    CHECK(rig.warnings() == 0);
    CHECK(rig.lines_containing("621.01") == 1);
}

TEST_CASE("a driver version that cannot be read is a warning too")
{
    Rig rig;
    rig.api.driver.clear();
    nvdm::DisplaySession session(rig.deps());
    CHECK(session.open(rig.api.luid).ok);
    CHECK(rig.warnings() == 1);
}

TEST_CASE("tested drivers")
{
    CHECK(nvdm::is_tested_driver("621.01"));
    CHECK_FALSE(nvdm::is_tested_driver("630.00"));
    CHECK_FALSE(nvdm::is_tested_driver(""));
}

TEST_CASE("open_after_success_is_free")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    REQUIRE(session.open(rig.api.luid).ok);
    const size_t calls = rig.api.calls().size();
    const size_t logged = rig.log.size();
    const int listed = rig.processes_listed;

    const nvdm::OpenResult again = session.open(rig.api.luid);
    CHECK(again.ok);
    CHECK(again.step == OpenStep::Done);
    CHECK(rig.api.calls().size() == calls);
    CHECK(rig.processes_listed == listed); // Not even the process list.
    CHECK(rig.log.size() == logged);
    CHECK(rig.pins == 1);
}

TEST_CASE("the first successful open says what it opened")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    const nvdm::OpenResult result = session.open(rig.api.luid);
    REQUIRE(result.ok);
    CHECK(contains(result.message, "Pimax Dream Air"));
    CHECK(contains(result.message, "7104x3840"));
    CHECK(contains(result.message, "90.000 Hz"));
    REQUIRE_FALSE(rig.log.empty());
    CHECK(rig.log.back() == result.message);
    CHECK(session.open(rig.api.luid).message == result.message);
}

TEST_CASE("every failing step is logged once, as its result's message")
{
    Rig rig;
    rig.api.queue(Fn::EnableDirectMode, -5);
    nvdm::DisplaySession session(rig.deps());
    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(rig.lines_containing(result.message) == 1);
    CHECK(rig.lines_containing("error: " + result.message) == 1);
    CHECK(rig.warnings() == 0);
}

TEST_CASE("NVAPI that does not load is retried from LoadNvapi")
{
    Rig rig;
    rig.api.queue(Fn::Initialize, nvdm::kNoNvapi);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::LoadNvapi);
    CHECK(first.code == nvdm::kNoNvapi);
    CHECK(rig.api.count("first_missing_id") == 0);
    CHECK(rig.api.count("driver_version") == 0);

    CHECK(session.open(rig.api.luid).ok);
    CHECK(rig.api.count("initialize") == 2);
}

TEST_CASE("an EDID that cannot be read fails at Product, and is read again")
{
    Rig rig;
    rig.board = false; // no last route either
    rig.api.queue(Fn::ReadEdid, -3);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::Product);
    CHECK(rig.api.count("enable_direct_mode") == 0);

    CHECK(session.open(rig.api.luid).ok);
    CHECK(rig.api.count("read_edid") == 2);
    CHECK(rig.api.count("adapter_luid") == 1);
}

TEST_CASE("an unknown product is rejected before direct mode, naming it")
{
    Rig rig;
    rig.api.edid = FakeApi::make_edid(0x42d2, 0x1020);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Product);
    CHECK(contains(result.message, "PVR"));
    CHECK(contains(result.message, "0x1020"));
    CHECK(rig.api.count("enable_direct_mode") == 0);
    CHECK(rig.d3d11_made == 0);
}

TEST_CASE("the EDID route that answered is logged once and kept")
{
    Rig rig;
    rig.api.edid_route = nvdm::EdidRoute::Registry;
    nvdm::DisplaySession session(rig.deps());
    REQUIRE(session.open(rig.api.luid).ok);
    session.open(rig.api.luid);

    CHECK(session.edid_route() == nvdm::EdidRoute::Registry);
    CHECK(rig.lines_containing("EDID route: ") == 1);
    CHECK(rig.lines_containing("EDID route: registry") == 1);

    Rig nvapi;
    nvdm::DisplaySession other(nvapi.deps());
    REQUIRE(other.open(nvapi.api.luid).ok);
    CHECK(other.edid_route() == nvdm::EdidRoute::Nvapi);
    CHECK(nvapi.lines_containing("EDID route: ") == 1);
    CHECK(nvapi.lines_containing("EDID route: NVAPI") == 1);
}

// --- Design 6.3's last route: no EDID, one PVR display, the Dream Air's board connected ---------------------------

TEST_CASE("no EDID, one PVR display and the board connected: the Dream Air is assumed, with a warning naming why")
{
    Rig rig;
    rig.api.edid.clear(); // no route reads one
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    REQUIRE(result.ok);
    CHECK(session.edid_route() == nvdm::EdidRoute::Board);
    CHECK(std::string(session.product().name) == "Pimax Dream Air");
    CHECK(session.mode().width == 7104);
    CHECK(rig.board_checks == 1);
    CHECK(rig.api.count("enable_direct_mode") == 1);

    // One warning: the assumption, and why it is made.
    REQUIRE(rig.warnings() == 1);
    std::string warning;
    for (const std::string& line : rig.log) {
        if (line.rfind("warning: ", 0) == 0) {
            warning = line;
        }
    }
    CHECK(contains(warning, "EDID"));
    CHECK(contains(warning, "NVAPI"));
    CHECK(contains(warning, "registry"));
    CHECK(contains(warning, "Pimax Dream Air"));
    CHECK(contains(warning, "34A4:0044"));
    CHECK(contains(warning, "SetDisplayMode")); // what happens if the assumption is wrong
    CHECK(rig.lines_containing("EDID route: board") == 1);
}

TEST_CASE("no EDID and no board: Product fails, naming the board, and nothing is assumed")
{
    Rig rig;
    rig.api.edid.clear();
    rig.board = false;
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Product);
    CHECK(result.code == nvdm::kBadOutput);
    CHECK(contains(result.message, "EDID could not be read"));
    CHECK(contains(result.message, "34A4:0044"));
    CHECK(rig.board_checks == 1);
    CHECK(rig.warnings() == 0);
    CHECK(rig.api.count("enable_direct_mode") == 0);
    CHECK(session.edid_route() == nvdm::EdidRoute::None);
}

TEST_CASE("no EDID and two PVR displays: Product fails, and the board is not even asked")
{
    Rig rig;
    rig.api.edid.clear();
    rig.api.display_ids = {0x80061086, 0x80061090};
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult result = session.open(rig.api.luid);
    CHECK_FALSE(result.ok);
    CHECK(result.step == OpenStep::Product);
    CHECK(result.code == nvdm::kBadOutput);
    CHECK(contains(result.message, "2 direct-mode displays"));
    CHECK(rig.board_checks == 0);
    CHECK(rig.warnings() == 0);
    CHECK(rig.api.count("enable_direct_mode") == 0);
}

TEST_CASE("an EDID that can be read is the answer: the board is never asked")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    REQUIRE(session.open(rig.api.luid).ok);
    CHECK(rig.board_checks == 0);
    CHECK(rig.warnings() == 0);

    // Another product's EDID is refused, board or not: the last route is for no EDID only.
    Rig other;
    other.api.edid = FakeApi::make_edid(0x42d2, 0x1020);
    nvdm::DisplaySession refused(other.deps());
    CHECK(refused.open(other.api.luid).step == OpenStep::Product);
    CHECK(other.board_checks == 0);
}

TEST_CASE("DREAMAIR_VK_LAYER_TEST_NO_EDID: no EDID route is tried, so the board identifies the product")
{
    // A machine where every route would read the EDID, run as if none could: the last route answers, and the lines
    // name the variable.
    Rig rig;
    rig.skip_edid = true;
    nvdm::DisplaySession session(rig.deps());

    REQUIRE(session.open(rig.api.luid).ok);
    CHECK(rig.api.count("read_edid") == 0);
    CHECK(session.edid_route() == nvdm::EdidRoute::Board);
    CHECK(rig.board_checks == 1);
    CHECK(rig.lines_containing("EDID route: board") == 1);
    CHECK(rig.warnings() == 2); // the variable is set; the product is assumed
    CHECK(rig.lines_containing("DREAMAIR_VK_LAYER_TEST_NO_EDID") == 2);

    // Without the board, the failure line says the routes were skipped rather than tried.
    Rig no_board;
    no_board.skip_edid = true;
    no_board.board = false;
    nvdm::DisplaySession failed(no_board.deps());
    const nvdm::OpenResult result = failed.open(no_board.api.luid);
    CHECK(result.step == OpenStep::Product);
    CHECK(no_board.api.count("read_edid") == 0);
    CHECK(contains(result.message, "DREAMAIR_VK_LAYER_TEST_NO_EDID"));
    CHECK(contains(result.message, "34A4:0044"));

    // A second attempt says the variable once only.
    failed.open(no_board.api.luid);
    CHECK(no_board.lines_containing("is set: every EDID route is skipped") == 1);
}

TEST_CASE("EnableDirectMode refused is retried from there")
{
    Rig rig;
    rig.api.queue(Fn::EnableDirectMode, -5);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::EnableDirectMode);
    CHECK(first.code == -5);
    CHECK(rig.d3d11_made == 0);

    CHECK(session.open(rig.api.luid).ok);
    CHECK(rig.api.count("enable_direct_mode") == 2);
    CHECK(rig.api.count("read_edid") == 1);
    CHECK(rig.d3d11_made == 1);
}

TEST_CASE("a D3D11 device that cannot be made is retried, and enable is not repeated")
{
    Rig rig;
    IUnknown* const good = rig.device;
    rig.device = nullptr;
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::D3D11);
    CHECK(rig.api.count("bind_device") == 0);

    rig.device = good;
    CHECK(session.open(rig.api.luid).ok);
    CHECK(rig.d3d11_made == 2);
    CHECK(rig.api.count("enable_direct_mode") == 1);
    CHECK(rig.api.bound_device == good);
}

TEST_CASE("back-buffers already made are kept when a later one fails")
{
    Rig rig;
    rig.api.queue(Fn::CreateBackBuffer, 0, 2);
    rig.api.queue(Fn::CreateBackBuffer, -7);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::CreateBackBuffers);
    CHECK(first.code == -7);
    CHECK(rig.api.count("set_display_mode") == 0);
    CHECK_FALSE(session.is_open());

    REQUIRE(session.open(rig.api.luid).ok);
    CHECK(rig.api.count("create_back_buffer") == 4); // 3 and one retry, not 6.
    CHECK(rig.api.count("bind_device") == 1);
    for (uint32_t i = 0; i < nvdm::kBackBufferCount; i++) {
        CHECK(session.back_buffer(i).tag == rig.api.back_buffers[i].tag);
    }
}

TEST_CASE("SetDisplayMode failing is retried alone; only -208 and 0 are success")
{
    Rig rig;
    rig.api.queue(Fn::SetDisplayMode, 7);
    nvdm::DisplaySession session(rig.deps());

    const nvdm::OpenResult first = session.open(rig.api.luid);
    CHECK_FALSE(first.ok);
    CHECK(first.step == OpenStep::SetDisplayMode);
    CHECK(first.code == 7);
    CHECK_FALSE(session.is_open());

    REQUIRE(session.open(rig.api.luid).ok);
    CHECK(rig.api.count("set_display_mode") == 2);
    CHECK(rig.api.count("create_back_buffer") == 3);

    Rig zero;
    zero.api.set_default_status(Fn::SetDisplayMode, 0);
    nvdm::DisplaySession other(zero.deps());
    CHECK(other.open(zero.api.luid).ok);
}

TEST_CASE("concurrent opens run the steps once")
{
    Rig rig;
    nvdm::DisplaySession session(rig.deps());
    std::atomic<int> ok{0};
    std::vector<std::thread> threads;
    for (int i = 0; i < 4; i++) {
        threads.emplace_back([&] {
            if (session.open(rig.api.luid).ok) {
                ok++;
            }
        });
    }
    for (std::thread& t : threads) {
        t.join();
    }
    CHECK(ok == 4);
    CHECK(rig.api.count("initialize") == 1);
    CHECK(rig.api.count("bind_device") == 1);
    CHECK(rig.api.count("set_display_mode") == 1);
    CHECK(rig.pins == 1);
}

TEST_CASE("session settings come from the layer's three variables")
{
    nvdm::SessionSettings defaults = nvdm::parse_session_settings(nullptr, nullptr);
    CHECK(defaults.wait_ms == 10000);
    CHECK_FALSE(defaults.ignore_holders);

    nvdm::SessionSettings set = nvdm::parse_session_settings("2500", "1");
    CHECK(set.wait_ms == 2500);
    CHECK(set.ignore_holders);

    CHECK(nvdm::parse_session_settings("0", nullptr).wait_ms == 0);
    // What is not a number, or does not fit, is the default; only "1" turns the holder checks off.
    CHECK(nvdm::parse_session_settings("abc", "0").wait_ms == 10000);
    CHECK(nvdm::parse_session_settings("", "").wait_ms == 10000);
    CHECK(nvdm::parse_session_settings("12ms", nullptr).wait_ms == 10000);
    CHECK(nvdm::parse_session_settings("-5", nullptr).wait_ms == 10000);
    CHECK(nvdm::parse_session_settings("99999999999", nullptr).wait_ms == 10000);
    CHECK_FALSE(nvdm::parse_session_settings(nullptr, "0").ignore_holders);
    CHECK_FALSE(nvdm::parse_session_settings(nullptr, "true").ignore_holders);
    CHECK_FALSE(nvdm::parse_session_settings(nullptr, "").ignore_holders);

    // DREAMAIR_VK_LAYER_TEST_NO_EDID, tests only: "1" skips every EDID route, anything else does not.
    CHECK_FALSE(defaults.skip_edid);
    CHECK(nvdm::parse_session_settings(nullptr, nullptr, "1").skip_edid);
    CHECK_FALSE(nvdm::parse_session_settings(nullptr, nullptr, "0").skip_edid);
    CHECK_FALSE(nvdm::parse_session_settings(nullptr, nullptr, "").skip_edid);
    CHECK_FALSE(nvdm::parse_session_settings(nullptr, nullptr, "yes").skip_edid);
    CHECK_FALSE(nvdm::parse_session_settings(nullptr, "1", nullptr).skip_edid);
}

TEST_CASE("open steps have names")
{
    CHECK(std::string(nvdm::step_name(OpenStep::Holders)) == "Holders");
    CHECK(std::string(nvdm::step_name(OpenStep::EnableDirectMode)) == "EnableDirectMode");
    CHECK(std::string(nvdm::step_name(OpenStep::Done)) == "Done");
}
