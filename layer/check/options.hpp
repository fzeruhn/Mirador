// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <string>
#include <vector>

namespace dvl {

// The check tool's exit codes (design 7): 0 only for a passing verdict, 1 when a check failed, 2 for a usage error
// (and for --help, which gives no verdict).
inline constexpr int kExitPass = 0;
inline constexpr int kExitFail = 1;
inline constexpr int kExitUsage = 2;

inline constexpr int kDefaultSeconds = 20;
inline constexpr int kMaxSeconds = 3600;

// mirador-display-check [--vulkan] [--no-heartbeat] [--ignore-holders] [--seconds=20]
struct Options {
    bool vulkan = false;         // Through the layer, the way Monado uses it, instead of NVAPI alone.
    bool no_heartbeat = false;   // Do not send the board heartbeat.
    bool ignore_holders = false; // Skip the holder checks (tests only, design 4); with --vulkan also the layer's.
    bool help = false;
    int seconds = kDefaultSeconds;
};

struct ParsedOptions {
    Options options;
    std::string error; // Empty if the arguments are fine.
    bool ok() const { return error.empty(); }
};

// `args` are the arguments after the program name. Strict: an unknown argument, a value on a flag, or a --seconds that
// is not a whole number from 1 to kMaxSeconds is an error that names it.
ParsedOptions parse_options(const std::vector<std::string>& args);

// The text of --help and of a usage error: the options, the exit codes, and what --vulkan needs (the layer installed,
// or VK_IMPLICIT_LAYER_PATH).
std::string usage_text();

// Sets the variables the mode needs in this process, before anything loads Vulkan or NVAPI:
//  - MIRADOR_DIRECT_MODE_IGNORE_HOLDERS=1 with --ignore-holders (read by the display session in either mode, and by the
//    layer); removed otherwise, so a stray value cannot skip the session's own holder check;
//  - with --vulkan, MIRADOR_DIRECT_MODE=1 (enables the layer) and DISABLE_RTSS_LAYER=1 (keeps RTSS's overlay layer out,
//    design 5).
void apply_environment(const Options& options);

} // namespace dvl
