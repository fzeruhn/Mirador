// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "check/options.hpp"

#include <windows.h>

#include <cstdint>

namespace dvl {
namespace {

constexpr const char kSecondsPrefix[] = "--seconds=";

// A whole number from 1 to kMaxSeconds, digits only: no sign, no blanks, no fraction.
bool parse_seconds(const std::string& text, int& out)
{
    if (text.empty() || text.size() > 5) {
        return false;
    }
    int value = 0;
    for (const char c : text) {
        if (c < '0' || c > '9') {
            return false;
        }
        value = value * 10 + (c - '0');
    }
    if (value < 1 || value > kMaxSeconds) {
        return false;
    }
    out = value;
    return true;
}

void set_variable(const wchar_t* name, const wchar_t* value)
{
    SetEnvironmentVariableW(name, value); // a null value removes it
}

} // namespace

ParsedOptions parse_options(const std::vector<std::string>& args)
{
    ParsedOptions parsed;
    Options& o = parsed.options;
    for (const std::string& arg : args) {
        if (arg == "--no-heartbeat") {
            o.no_heartbeat = true;
        } else if (arg == "--ignore-holders") {
            o.ignore_holders = true;
        } else if (arg == "--help" || arg == "-h" || arg == "/?") {
            o.help = true;
        } else if (arg.rfind(kSecondsPrefix, 0) == 0) {
            if (!parse_seconds(arg.substr(sizeof(kSecondsPrefix) - 1), o.seconds)) {
                parsed.error = "'" + arg + "': --seconds takes a whole number from 1 to " +
                               std::to_string(kMaxSeconds) + ", as in --seconds=20";
                return parsed;
            }
        } else {
            parsed.error = "unknown argument '" + arg + "'";
            return parsed;
        }
    }
    return parsed;
}

std::string usage_text()
{
    return "usage: mirador-display-check [--no-heartbeat] [--ignore-holders] [--seconds=20]\n"
           "\n"
           "Checks the headset's direct mode on this machine: the NVIDIA driver version, that every NVAPI call the\n"
           "layer needs is there, that the display is found, bound and flipping, presenting solid colours for\n"
           "--seconds and counting the display's flips. It sends the board's heartbeat itself, so Pimax and Monado\n"
           "must not run.\n"
           "\n"
           "  --no-heartbeat    Do not send the board heartbeat (something else does).\n"
           "  --ignore-holders  Skip the holder checks (tests only).\n"
           "  --seconds=N       How long to present, 1 to 3600 (default 20; 20 s is 1800 presents at 90 Hz).\n"
           "  --help            This text.\n"
           "\n"
           "Exit code: 0 if the verdict is PASS, 1 if a check failed, 2 for a usage error (and --help).\n";
}

void apply_environment(const Options& options)
{
    set_variable(L"MIRADOR_DIRECT_MODE_IGNORE_HOLDERS", options.ignore_holders ? L"1" : nullptr);
}

} // namespace dvl
