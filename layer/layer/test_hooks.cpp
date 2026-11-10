// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// Built into dvl_layer_for_tests.dll only, never into mirador_direct_mode.dll: the seam through which the tests put a
// display session on a fake NVAPI in place of the process's (the controller's ruling for Task 9), so that no test
// reaches the real driver.

#include "layer/exit.hpp"
#include "layer/import.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"
#include "layer/swapchain.hpp"

// Every session put in place (or taken out, with null) gets a presentation state of its own: the display thread of the
// one before is stopped first, so it never runs on a fake NVAPI a test has destroyed (Task 11).
extern "C" __declspec(dllexport) void dvl_test_set_display_session(nvdm::DisplaySession* session)
{
    dvl::reset_presenter_for_tests();
    dvl::set_display_session_for_tests(session);
}

// Every line of the layer's log, at any level, goes to `sink` (on the thread that logs it) instead of the log file,
// stderr and the debugger while one is set; null puts the logger back (log.hpp, set_log_sink_for_tests). Task H.
extern "C" __declspec(dllexport) void dvl_test_set_log_sink(dvl::LogSink sink, void* context)
{
    dvl::set_log_sink_for_tests(sink, context);
}

// A line among the presenter's pending lines, where the display thread's frame model and the warnings leave theirs
// (made under the display-session lock, written once it is let go): 1, or 0 if there is no presenter yet. Task H.
extern "C" __declspec(dllexport) int dvl_test_add_pending_line(const char* line)
{
    dvl::Presenter* const p = dvl::presenter_if_any();
    if (p == nullptr || line == nullptr) {
        return 0;
    }
    p->pending.add(dvl::Level::Warn, line);
    return 1;
}

// What the console control handler does for Ctrl+C, Ctrl+Break or closing the console (`event` is the CTRL_..._EVENT
// number): in this DLL's own copy of exit.cpp, where the display thread's stop hook is set (the test executable has a
// copy of its own). Returns the handler's answer, always 0 (FALSE: the default handler still runs).
extern "C" __declspec(dllexport) int dvl_test_console_event(unsigned long event)
{
    return dvl::handle_console_event(event) ? 1 : 0;
}

// Fills the presenter's trace (MIRADOR_DIRECT_MODE_TRACE) until a record does not fit, which is when its array-full notice
// is made: 1, or 0 if there is no presenter or it has no trace.
extern "C" __declspec(dllexport) int dvl_test_fill_trace()
{
    dvl::Presenter* const p = dvl::presenter_if_any();
    if (p == nullptr || !p->trace) {
        return 0;
    }
    while (p->trace->dropped() == 0) {
        p->note(nvdm::TraceKind::Counter);
    }
    return 1;
}

namespace {

// 1 or 0 for `trial`'s answer on the device, -1 for a device the layer does not know.
template <class Trial>
int on_device(VkDevice device, const char* name, Trial&& trial)
{
    int answer = -1;
    dvl::guarded_void(name, [&] {
        if (dvl::DeviceData* data = dvl::find_device_data(device)) {
            answer = trial(*data) ? 1 : 0;
        }
    });
    return answer;
}

} // namespace

// What Task 11's vkCreateSwapchainKHR on our surface asks first: the trial import of a device that did not exist at
// the display query (design 6.3).
extern "C" __declspec(dllexport) int dvl_test_trial_import_at_swapchain(VkDevice device)
{
    return on_device(device, "dvl_test_trial_import_at_swapchain",
                     [](dvl::DeviceData& data) { return dvl::trial_import_at_swapchain(data); });
}

// The trial import itself, so that its own once-per-device answer can be checked.
extern "C" __declspec(dllexport) int dvl_test_trial_import(VkDevice device)
{
    return on_device(device, "dvl_test_trial_import", [](dvl::DeviceData& data) { return dvl::trial_import(data); });
}
