// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <functional>

namespace dvl {

// Process exit (design 6.10). Monado quits cleanly only on a key press in its console. Ctrl+C or closing the console
// runs the default handler, which calls ExitProcess: no teardown, and every other thread is killed wherever it is,
// maybe the display thread inside NVAPI. So the layer registers a console control handler that stops the display
// thread first, waits a little, and lets the default exit go on.

// How long the console handler waits for the stop hook (design 6.10).
inline constexpr DWORD kConsoleHookWaitMs = 100;

// The hook the console handler calls: "stop the display thread and wait for it to leave NVAPI" (Task 11 sets it once
// the thread exists). The hook is expected to bound its own wait; the handler runs it on a helper thread and gives it
// kConsoleHookWaitMs in all, so a hook that hangs cannot hold the exit. Any thread; an empty function clears it.
void set_console_stop_hook(std::function<void()> hook);

// The console control handler's body. CTRL_C_EVENT, CTRL_BREAK_EVENT and CTRL_CLOSE_EVENT (each ends the process by
// default) call the hook, if set, and wait up to kConsoleHookWaitMs for it. Always returns FALSE, so the next
// handler, and in the end the default one, still runs.
BOOL handle_console_event(DWORD event);

// Registers the handler with SetConsoleCtrlHandler, once per process. True if this call registered it, false if it
// was registered already or the system refused.
bool install_console_handler();
// Takes it out again. Safe when it is not registered.
void remove_console_handler();
bool console_handler_installed();

// DllMain's DLL_PROCESS_DETACH. `reserved` non-NULL means the process is exiting: it returns at once, touching
// nothing (no joins, no waits, no locks a killed thread may hold). NULL means FreeLibrary: the DLL's code is about
// to go, so the console handler must not stay registered.
void on_process_detach(void* reserved);

} // namespace dvl
