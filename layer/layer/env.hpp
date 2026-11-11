// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <optional>
#include <string>
#include <string_view>

namespace dvl {

// UTF-8 and UTF-16 conversions. Invalid input gives an empty string.
std::string to_utf8(std::wstring_view text);
std::wstring from_utf8(std::string_view text);

// The value of an environment variable of this process as UTF-8; nullopt if it is not set. Read when called, never
// cached: the tests change the environment between calls.
std::optional<std::string> env_get(const wchar_t* name);

} // namespace dvl
