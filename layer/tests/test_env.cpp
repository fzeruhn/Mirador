// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The layer's variables, renamed for Mirador (MIRADOR_DIRECT_MODE*); the Dream Air names are still read for one release.

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <string>

#include "layer/env.hpp"

namespace {

struct Variable {
    const wchar_t* name;
    explicit Variable(const wchar_t* n, const wchar_t* value) : name(n) { SetEnvironmentVariableW(name, value); }
    ~Variable() { SetEnvironmentVariableW(name, nullptr); }
};

} // namespace

TEST_CASE("env: a layer variable under its new name is read")
{
    Variable v(L"MIRADOR_DIRECT_MODE_LOG", L"debug");
    CHECK(dvl::env_get(L"MIRADOR_DIRECT_MODE_LOG").value_or("") == "debug");
}

TEST_CASE("env: the Dream Air names are no longer read")
{
    Variable old_log(L"DREAMAIR_VK_LAYER_LOG", L"warn");
    Variable old_disable(L"DISABLE_DREAMAIR_VK_LAYER", L"1");
    CHECK_FALSE(dvl::env_get(L"MIRADOR_DIRECT_MODE_LOG").has_value());
    CHECK_FALSE(dvl::env_get(L"DISABLE_MIRADOR_DIRECT_MODE").has_value());
    CHECK_FALSE(dvl::env_get(L"SOMETHING_UNSET_42").has_value());
}
