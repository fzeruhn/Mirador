// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/guard.hpp"

#include <windows.h>

#include <string_view>

#include "layer/env.hpp"

namespace dvl {
namespace {

constexpr std::wstring_view kBuiltInProcesses[] = {
    L"monado-service.exe",
    L"mirador-display-check.exe",
};

bool same_name(std::wstring_view a, std::wstring_view b)
{
    if (a.empty() || a.size() != b.size()) {
        return false;
    }
    // Ordinal and case-insensitive, as the file system compares names.
    return CompareStringOrdinal(a.data(), static_cast<int>(a.size()), b.data(), static_cast<int>(b.size()), TRUE) ==
           CSTR_EQUAL;
}

std::string_view trim(std::string_view s)
{
    constexpr std::string_view blanks = " \t\r\n";
    const size_t first = s.find_first_not_of(blanks);
    if (first == std::string_view::npos) {
        return {};
    }
    return s.substr(first, s.find_last_not_of(blanks) - first + 1);
}

} // namespace

bool process_allowed(std::wstring_view exe_basename, std::string_view extra_list)
{
    if (exe_basename.empty()) {
        return false;
    }
    for (std::wstring_view name : kBuiltInProcesses) {
        if (same_name(exe_basename, name)) {
            return true;
        }
    }
    while (!extra_list.empty()) {
        const size_t end = extra_list.find(';');
        const std::string_view entry = trim(extra_list.substr(0, end));
        if (!entry.empty() && same_name(exe_basename, from_utf8(entry))) {
            return true;
        }
        if (end == std::string_view::npos) {
            break;
        }
        extra_list.remove_prefix(end + 1);
    }
    return false;
}

std::wstring basename_of(std::wstring_view path)
{
    const size_t slash = path.find_last_of(L"\\/");
    return std::wstring(slash == std::wstring_view::npos ? path : path.substr(slash + 1));
}

std::wstring current_process_basename()
{
    wchar_t buffer[MAX_PATH * 4];
    const DWORD n = GetModuleFileNameW(nullptr, buffer, static_cast<DWORD>(sizeof(buffer) / sizeof(buffer[0])));
    if (n == 0 || n >= sizeof(buffer) / sizeof(buffer[0])) {
        return {};
    }
    return basename_of(std::wstring_view(buffer, n));
}

bool current_process_allowed()
{
    return process_allowed(current_process_basename(), env_get(L"MIRADOR_DIRECT_MODE_PROCESSES").value_or(std::string()));
}

} // namespace dvl
