// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// mirador-display-check [--vulkan] [--no-heartbeat] [--ignore-holders] [--seconds=20]  (design 7)
//
// The order: arguments, then the holder checks (before anything is touched: no NVAPI, no HID, no Vulkan if Pimax or
// Monado runs), then the board heartbeat, then the mode, then the verdict. Exit code: 0 PASS, 1 a check failed, 2 usage.

#include <windows.h>

#include <cstdio>
#include <exception>
#include <string>
#include <vector>

#include "check/heartbeat.hpp"
#include "check/modes.hpp"
#include "check/options.hpp"
#include "check/report.hpp"
#include "core/holders.hpp"

int main(int argc, char** argv)
{
    using namespace dvl;

    const std::vector<std::string> args(argv + 1, argv + argc);
    const ParsedOptions parsed = parse_options(args);
    if (!parsed.ok()) {
        std::fprintf(stderr, "mirador-display-check: %s\n\n%s", parsed.error.c_str(), usage_text().c_str());
        return kExitUsage;
    }
    const Options& options = parsed.options;
    if (options.help) {
        std::fputs(usage_text().c_str(), stdout);
        return kExitUsage;
    }

    StdoutOutput out;
    say(out, "mirador-display-check %s: %s, %d s%s%s", DVL_CHECK_VERSION,
        options.vulkan ? "--vulkan (through the layer)" : "NVAPI only", options.seconds,
        options.no_heartbeat ? ", no heartbeat" : "", options.ignore_holders ? ", holder checks skipped" : "");

    // The environment first: the display session reads its holder switch, and the loader its layer switches, when they
    // are first used.
    apply_environment(options);
    install_interrupt_handler();

    // Design 7: the holder checks come first, naming what runs. Nothing else is touched until they pass.
    const HolderFindings findings = holder_findings(nvdm::list_processes());
    for (const std::string& warning : findings.warnings) {
        say(out, "warning: %s", warning.c_str());
    }
    if (options.ignore_holders) {
        say(out, "holder checks: skipped (--ignore-holders)%s", findings.blocked() ? "; they would have stopped the run:" : "");
        for (const std::string& line : findings.blockers) {
            say(out, "  ignored: %s", line.c_str());
        }
    } else if (findings.blocked()) {
        say(out, "holder checks: FAILED");
        for (const std::string& line : findings.blockers) {
            say(out, "  %s", line.c_str());
        }
        say(out, "Stop Pimax and Monado first, then run this again. Nothing was touched: no NVAPI call, no HID write, no "
                 "Vulkan. (--ignore-holders skips the check; for tests only.)");
        const Verdict verdict = judge(false, "holder check: " + findings.blockers.front(), RunCounts{});
        say(out, "%s", verdict.line.c_str());
        return kExitFail;
    } else {
        say(out, "holder checks: nothing holds the display, the board or the eye tracker");
    }

    // heartbeat.py refuses to run beside Pimax: two hosts on the board's command channel is untested. --ignore-holders
    // lets it through, so say so (the M1 runs beside Pimax pass --no-heartbeat: Pimax keeps the board awake).
    if (options.ignore_holders && !options.no_heartbeat && (findings.board_held || findings.monado_running)) {
        say(out, "warning: a program that holds the board is running and --no-heartbeat is not set, so this tool sends "
                 "the heartbeat as a second host on the board's command channel, which is untested; pass "
                 "--no-heartbeat to leave the board to the program that has it");
    }

    // Without Monado nothing keeps the board awake, and the display only appears while it gets its heartbeat.
    Heartbeat heartbeat;
    if (options.no_heartbeat) {
        say(out, "heartbeat: off (--no-heartbeat)");
    } else if (heartbeat.start()) {
        say(out, "heartbeat: sending the report every %lld ms to %s", static_cast<long long>(kHeartbeatPeriod.count()),
            heartbeat.detail().c_str());
    } else {
        say(out, "warning: the heartbeat did not start: %s (the display appears only while the board gets one)",
            heartbeat.detail().c_str());
    }

    ModeResult mode;
    try {
        mode = options.vulkan ? run_vulkan_mode(options, out) : run_nvapi_mode(options, out);
    } catch (const std::exception& e) {
        mode.steps_ok = false;
        mode.failed_step = std::string("exception: ") + e.what();
    }

    if (!options.no_heartbeat) {
        heartbeat.stop();
        say(out, "heartbeat: %llu reports sent, %llu failed", static_cast<unsigned long long>(heartbeat.sent()),
            static_cast<unsigned long long>(heartbeat.failed()));
    }
    const Verdict verdict = judge(mode.steps_ok, mode.failed_step, mode.counts);
    say(out, "%s", verdict.line.c_str());
    return verdict.pass ? kExitPass : verdict.not_used ? kExitNotUsed : kExitFail;
}
