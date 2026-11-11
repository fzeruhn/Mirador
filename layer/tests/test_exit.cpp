// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// Process exit (design 6.10): the console control handler and DllMain's process-exit rule.

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <atomic>
#include <chrono>
#include <memory>

#include "layer/exit.hpp"

namespace {

// Clears the stop hook and the handler when a test ends, however it ends.
struct ExitCleanup {
    ~ExitCleanup()
    {
        dvl::set_console_stop_hook({});
        dvl::remove_console_handler();
    }
};

// What a stop hook did. Shared, so a hook that outlives its test (a hung one) touches nothing that is gone.
struct HookState {
    std::atomic<int> calls{0};
    std::atomic<DWORD> thread{0};
    HANDLE release = CreateEventW(nullptr, TRUE, FALSE, nullptr); // a hook that waits for it
    ~HookState() { CloseHandle(release); }
};

double elapsed_ms(std::chrono::steady_clock::time_point since)
{
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - since).count();
}

} // namespace

TEST_CASE("console_handler_stops_the_display_thread_and_lets_the_default_exit_run")
{
    ExitCleanup cleanup;
    auto state = std::make_shared<HookState>();
    dvl::set_console_stop_hook([state] {
        state->calls++;
        state->thread = GetCurrentThreadId();
    });

    for (DWORD event : {CTRL_C_EVENT, CTRL_BREAK_EVENT, CTRL_CLOSE_EVENT}) {
        INFO("console event " << event);
        const int before = state->calls;
        CHECK(dvl::handle_console_event(event) == FALSE); // FALSE: the next handler, the default, still runs
        CHECK(state->calls == before + 1);
    }
}

TEST_CASE("console_handler_ignores_other_events")
{
    ExitCleanup cleanup;
    auto state = std::make_shared<HookState>();
    dvl::set_console_stop_hook([state] { state->calls++; });
    CHECK(dvl::handle_console_event(CTRL_LOGOFF_EVENT) == FALSE);
    CHECK(dvl::handle_console_event(CTRL_SHUTDOWN_EVENT) == FALSE);
    CHECK(state->calls == 0);
}

TEST_CASE("console_handler_without_a_hook_returns_at_once")
{
    ExitCleanup cleanup;
    dvl::set_console_stop_hook({});
    const auto t0 = std::chrono::steady_clock::now();
    CHECK(dvl::handle_console_event(CTRL_C_EVENT) == FALSE);
    CHECK(elapsed_ms(t0) < 50.0);
}

TEST_CASE("console_handler_waits_at_most_100_ms_for_the_hook")
{
    ExitCleanup cleanup;
    auto state = std::make_shared<HookState>();
    dvl::set_console_stop_hook([state] {
        state->calls++;
        WaitForSingleObject(state->release, INFINITE); // a hook that hangs
    });

    const auto t0 = std::chrono::steady_clock::now();
    CHECK(dvl::handle_console_event(CTRL_C_EVENT) == FALSE);
    const double ms = elapsed_ms(t0);
    CHECK(state->calls == 1);
    CHECK(ms >= 90.0);  // it did wait for the hook ...
    CHECK(ms < 500.0);  // ... but only about 100 ms
    SetEvent(state->release);
}

TEST_CASE("console_handler_returns_when_the_hook_is_done")
{
    ExitCleanup cleanup;
    auto state = std::make_shared<HookState>();
    dvl::set_console_stop_hook([state] {
        state->calls++;
        Sleep(20);
    });
    const auto t0 = std::chrono::steady_clock::now();
    CHECK(dvl::handle_console_event(CTRL_CLOSE_EVENT) == FALSE);
    const double ms = elapsed_ms(t0);
    CHECK(state->calls == 1);
    CHECK(ms >= 15.0);
    CHECK(ms < 90.0); // it did not wait out the 100 ms
}

TEST_CASE("console_handler_is_registered_once")
{
    ExitCleanup cleanup;
    CHECK_FALSE(dvl::console_handler_installed());
    CHECK(dvl::install_console_handler());       // this call registered it
    CHECK(dvl::console_handler_installed());
    CHECK_FALSE(dvl::install_console_handler()); // already there
    CHECK(dvl::console_handler_installed());
    dvl::remove_console_handler();
    CHECK_FALSE(dvl::console_handler_installed());
}

TEST_CASE("process_exit_detach_does_nothing")
{
    ExitCleanup cleanup;
    auto state = std::make_shared<HookState>();
    dvl::set_console_stop_hook([state] { state->calls++; });
    REQUIRE(dvl::install_console_handler());

    // DLL_PROCESS_DETACH with lpReserved non-NULL: the process is exiting. Nothing is touched.
    int exiting = 0;
    dvl::on_process_detach(&exiting);
    CHECK(dvl::console_handler_installed());
    CHECK(state->calls == 0);

    // lpReserved NULL: FreeLibrary. The handler must not outlive the DLL's code.
    dvl::on_process_detach(nullptr);
    CHECK_FALSE(dvl::console_handler_installed());
    CHECK(state->calls == 0);
}
