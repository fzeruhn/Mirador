// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/exit.hpp"

#include <atomic>
#include <memory>
#include <mutex>

namespace dvl {
namespace {

using Hook = std::function<void()>;

// The hook, shared so the helper thread keeps its copy alive if the hook is replaced meanwhile. Made at first use
// and never destroyed: nothing here runs at process exit (design 6.10).
struct HookSlot {
    std::mutex mutex;
    std::shared_ptr<const Hook> hook;
};

HookSlot& slot()
{
    static HookSlot* const s = new HookSlot;
    return *s;
}

std::atomic<bool> g_installed{false};

BOOL WINAPI console_handler(DWORD event)
{
    return handle_console_event(event);
}

DWORD WINAPI run_hook(LPVOID param)
{
    std::unique_ptr<std::shared_ptr<const Hook>> hook(static_cast<std::shared_ptr<const Hook>*>(param));
    try {
        (**hook)();
    } catch (...) {
        // Nothing may leave a thread procedure; the exit goes on.
    }
    return 0;
}

} // namespace

void set_console_stop_hook(std::function<void()> hook)
{
    HookSlot& s = slot();
    std::shared_ptr<const Hook> next;
    if (hook) {
        next = std::make_shared<const Hook>(std::move(hook));
    }
    std::lock_guard<std::mutex> lock(s.mutex);
    s.hook = std::move(next);
}

BOOL handle_console_event(DWORD event)
{
    if (event != CTRL_C_EVENT && event != CTRL_BREAK_EVENT && event != CTRL_CLOSE_EVENT) {
        return FALSE;
    }
    std::shared_ptr<const Hook> hook;
    {
        HookSlot& s = slot();
        std::lock_guard<std::mutex> lock(s.mutex);
        hook = s.hook;
    }
    if (!hook) {
        return FALSE;
    }
    // The hook runs on its own thread so that this one can give up after kConsoleHookWaitMs. The thread is left
    // running if it hangs: the default handler ends the process right after.
    auto handed = std::make_unique<std::shared_ptr<const Hook>>(std::move(hook));
    HANDLE thread = CreateThread(nullptr, 0, run_hook, handed.get(), 0, nullptr);
    if (thread == nullptr) {
        // No thread to be had: run it here; the hook bounds its own wait.
        try {
            (**handed)();
        } catch (...) {
        }
        return FALSE;
    }
    handed.release(); // the thread owns it now
    WaitForSingleObject(thread, kConsoleHookWaitMs);
    CloseHandle(thread);
    return FALSE;
}

bool install_console_handler()
{
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) {
        return false;
    }
    if (!SetConsoleCtrlHandler(console_handler, TRUE)) {
        g_installed = false;
        return false;
    }
    return true;
}

void remove_console_handler()
{
    bool expected = true;
    if (g_installed.compare_exchange_strong(expected, false)) {
        SetConsoleCtrlHandler(console_handler, FALSE);
    }
}

bool console_handler_installed()
{
    return g_installed.load();
}

void on_process_detach(void* reserved)
{
    if (reserved != nullptr) {
        return; // process exit
    }
    remove_console_handler();
}

} // namespace dvl
