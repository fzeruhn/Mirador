// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <string>
#include <string_view>

namespace dvl {

// The process guard (design 5). It is a safety net, not what keeps the layer out of games: the layer is enabled by
// the MIRADOR_DIRECT_MODE variable, and only the start script, the launcher and the check tool set it. If someone sets
// it machine-wide anyway, negotiation declines in every process that is not on this list.

// True for the programs the layer is made for (monado-service.exe, mirador-display-check.exe) and for the names in
// `extra_list`: UTF-8, separated by ';', blanks around a name ignored (MIRADOR_DIRECT_MODE_PROCESSES, for tests).
// `exe_basename` is the executable's file name with no folder. Names compare whole and without regard to case.
bool process_allowed(std::wstring_view exe_basename, std::string_view extra_list);

// The part of `path` after the last '\' or '/'.
std::wstring basename_of(std::wstring_view path);

// The file name of this process's executable (GetModuleFileNameW of the process, not of the DLL).
std::wstring current_process_basename();

// process_allowed for this process, with MIRADOR_DIRECT_MODE_PROCESSES read now.
bool current_process_allowed();

} // namespace dvl
