// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include <windows.h>

#include <atomic>
#include <mutex>

#include "check/modes.hpp"

namespace dvl {
namespace {

// The console handler's wait for the stop hook, as the layer's in Monado (design 6.10, constraints).
constexpr DWORD kStopHookWaitMs = 100;

std::atomic<bool> g_interrupted{false};

// The stop hook. Made at first use and never destroyed: a hook left running past the handler's wait still holds the
// mutex when the process ends. The mutex is held while the hook runs, so replacing the hook waits for a run to end
// (whatever it refers to stays alive until then).
struct HookSlot {
    std::mutex mutex;
    std::function<void()> hook;
};

HookSlot& slot()
{
    static HookSlot* const s = new HookSlot;
    return *s;
}

DWORD WINAPI run_stop_hook(LPVOID)
{
    try {
        HookSlot& s = slot();
        std::lock_guard<std::mutex> hold(s.mutex);
        if (s.hook) {
            s.hook();
        }
    } catch (...) {
        // Nothing may leave a thread procedure; the process ends anyway.
    }
    return 0;
}

// The process ends when the handler returns: the display thread is stopped first, on a thread of its own so that the
// handler gives up after kStopHookWaitMs if it does not come back (stuck inside NVAPI).
void stop_before_exit()
{
    HANDLE thread = CreateThread(nullptr, 0, run_stop_hook, nullptr, 0, nullptr);
    if (thread == nullptr) {
        return; // no thread to be had: nothing bounds the hook's wait here, so it is not run
    }
    WaitForSingleObject(thread, kStopHookWaitMs);
    CloseHandle(thread);
}

BOOL WINAPI on_console_event(DWORD event)
{
    return on_interrupt_event(event);
}

} // namespace

BOOL on_interrupt_event(DWORD event)
{
    if (event == CTRL_CLOSE_EVENT) {
        // Closing the console: the default handler ends the process once this returns (Windows gives it a few
        // seconds at most). Like Ctrl+C the loops see the flag, but there is no time to wind down: the NVAPI mode's
        // display thread is stopped here, so it is not killed inside NVAPI.
        g_interrupted = true;
        stop_before_exit();
        return FALSE;
    }
    if (event != CTRL_C_EVENT && event != CTRL_BREAK_EVENT) {
        return FALSE; // logoff, shutdown: the default handler ends the process
    }
    if (g_interrupted.exchange(true)) {
        // A second one: the user wants out now, and the default handler ends the process.
        stop_before_exit();
        return FALSE;
    }
    return TRUE; // the loops see the flag and wind down; the display thread leaves NVAPI first
}

void set_interrupt_stop_hook(std::function<void()> hook)
{
    HookSlot& s = slot();
    std::lock_guard<std::mutex> hold(s.mutex);
    s.hook = std::move(hook);
}

void clear_interrupted_for_tests()
{
    g_interrupted = false;
}

void install_interrupt_handler()
{
    SetConsoleCtrlHandler(on_console_event, TRUE);
}

bool interrupted()
{
    return g_interrupted.load();
}

} // namespace dvl
