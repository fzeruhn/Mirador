// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/env.hpp"

#include <windows.h>

#include <vector>

namespace dvl {

std::string to_utf8(std::wstring_view text)
{
    if (text.empty()) {
        return {};
    }
    const int in = static_cast<int>(text.size());
    const int n = WideCharToMultiByte(CP_UTF8, 0, text.data(), in, nullptr, 0, nullptr, nullptr);
    if (n <= 0) {
        return {};
    }
    std::string out(static_cast<size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), in, out.data(), n, nullptr, nullptr);
    return out;
}

std::wstring from_utf8(std::string_view text)
{
    if (text.empty()) {
        return {};
    }
    const int in = static_cast<int>(text.size());
    const int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), in, nullptr, 0);
    if (n <= 0) {
        return {};
    }
    std::wstring out(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), in, out.data(), n);
    return out;
}

std::optional<std::string> env_get(const wchar_t* name)
{
    // The size can change between two calls (another thread sets the variable): loop until it fits.
    std::vector<wchar_t> buffer(256);
    for (;;) {
        SetLastError(ERROR_SUCCESS);
        const DWORD n = GetEnvironmentVariableW(name, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (n == 0) {
            if (GetLastError() == ERROR_ENVVAR_NOT_FOUND) {
                return std::nullopt;
            }
            return std::string(); // set, and empty
        }
        if (n < buffer.size()) {
            return to_utf8(std::wstring_view(buffer.data(), n));
        }
        buffer.resize(static_cast<size_t>(n) + 1);
    }
}

} // namespace dvl
