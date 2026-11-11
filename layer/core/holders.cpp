// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "holders.hpp"

#include <windows.h>
#include <tlhelp32.h>

#include <algorithm>
#include <cstddef>

namespace nvdm {
namespace {

// Design 4, "Holder checks". The start script carries its own copy of these lists (PowerShell cannot call this code).
const char* const kDisplay[] = {"pi_server.exe", "vrcompositor.exe"};
const char* const kBoard[] = {"pi_server.exe",   "PimaxClient.exe",   "PiPlayService.exe",
                              "PiPlatformService_64.exe", "pi_overlay.exe", "vrss_gaze_provider.exe"};
const char* const kReceiver[] = {"vrserver.exe", "vrmonitor.exe", "OVRLighthouseManager.exe"};
const char* const kMonado[] = {"monado-service.exe"};

char ascii_lower(char c)
{
    return c >= 'A' && c <= 'Z' ? static_cast<char>(c - 'A' + 'a') : c;
}

bool same_name(const std::string& a, const char* b)
{
    size_t i = 0;
    for (; i < a.size() && b[i] != '\0'; i++) {
        if (ascii_lower(a[i]) != ascii_lower(b[i])) {
            return false;
        }
    }
    return i == a.size() && b[i] == '\0';
}

template <size_t N>
std::vector<std::string> match(const char* const (&names)[N], const std::vector<std::string>& processes)
{
    std::vector<std::string> running;
    for (const char* name : names) {
        const bool found = std::any_of(processes.begin(), processes.end(),
                                       [name](const std::string& process) { return same_name(process, name); });
        if (found) {
            running.emplace_back(name);
        }
    }
    return running;
}

// UTF-16 to UTF-8, for the process list. Process names are ASCII in practice; anything else is kept as it is.
std::string to_utf8(const wchar_t* wide)
{
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 1) {
        return {};
    }
    std::string text(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, &text[0], size, nullptr, nullptr);
    text.resize(static_cast<size_t>(size) - 1); // Without the terminator the call wrote.
    return text;
}

} // namespace

std::vector<std::string> list_processes()
{
    std::vector<std::string> names;
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) {
        return names;
    }
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    for (BOOL more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry)) {
        names.push_back(to_utf8(entry.szExeFile));
    }
    CloseHandle(snapshot);
    return names;
}

std::vector<std::string> running_holders(Held held, const std::vector<std::string>& processes)
{
    switch (held) {
    case Held::Display: return match(kDisplay, processes);
    case Held::Board: return match(kBoard, processes);
    case Held::Receiver: return match(kReceiver, processes);
    case Held::Monado: return match(kMonado, processes);
    }
    return {};
}

} // namespace nvdm
