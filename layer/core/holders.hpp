// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <string>
#include <vector>

namespace nvdm {

// What another program can hold that the layer, the check tool and the start script need (design 4).
enum class Held {
    Display,  // The display in direct mode: pi_server.exe, vrcompositor.exe.
    Board,    // The board and eye tracker: Pimax's six programs (the launcher's list).
    Receiver, // The Lighthouse receiver: SteamVR's server and monitor, and the lighthouse manager.
    Monado,   // monado-service.exe, which holds all three.
};

// The executable names of every running process (Toolhelp32), as they are in the process list, e.g. "pi_server.exe".
// Empty if the snapshot fails.
std::vector<std::string> list_processes();

// The holders of `held` that appear in `processes`, matched without regard to case. The answer is the table's names
// (so "PI_SERVER.EXE" gives "pi_server.exe"), each once, in the table's order; empty if none runs.
std::vector<std::string> running_holders(Held held, const std::vector<std::string>& processes);

} // namespace nvdm
