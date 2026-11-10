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

namespace {

std::optional<std::string> env_get_one(const wchar_t* name);

// The Dream Air name a renamed variable had (MIRADOR_DIRECT_MODE* was DREAMAIR_VK_LAYER*), or empty.
std::wstring old_name(std::wstring_view name)
{
    constexpr std::wstring_view kNew = L"MIRADOR_DIRECT_MODE";
    constexpr std::wstring_view kDisableNew = L"DISABLE_MIRADOR_DIRECT_MODE";
    if (name.substr(0, kDisableNew.size()) == kDisableNew) {
        return L"DISABLE_DREAMAIR_VK_LAYER" + std::wstring(name.substr(kDisableNew.size()));
    }
    if (name.substr(0, kNew.size()) == kNew) {
        return L"DREAMAIR_VK_LAYER" + std::wstring(name.substr(kNew.size()));
    }
    return {};
}

} // namespace

std::optional<std::string> env_get(const wchar_t* name)
{
    if (std::optional<std::string> value = env_get_one(name)) {
        return value;
    }
    const std::wstring old = old_name(name);
    return old.empty() ? std::nullopt : env_get_one(old.c_str());
}

namespace {

std::optional<std::string> env_get_one(const wchar_t* name)
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

} // namespace

} // namespace dvl
