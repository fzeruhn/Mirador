// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The process guard (design 5, review focus 3), the layer's negotiation with the loader (review focus 5) and the
// manifest the build generates next to the DLL.

#include <catch2/catch_test_macros.hpp>

#include <fstream>
#include <iterator>
#include <string>

#include "fakes/layer_dll.hpp"
#include "layer/guard.hpp"

TEST_CASE("guard_allows_monado_and_check_tool")
{
    CHECK(dvl::process_allowed(L"monado-service.exe", ""));
    CHECK(dvl::process_allowed(L"mirador-display-check.exe", ""));
    // Windows file names ignore case.
    CHECK(dvl::process_allowed(L"MONADO-SERVICE.EXE", ""));
    CHECK(dvl::process_allowed(L"Monado-Service.exe", ""));
    CHECK(dvl::process_allowed(L"Mirador-Display-Check.EXE", ""));
    // The check tool's former name is just another program now.
    CHECK_FALSE(dvl::process_allowed(L"dreamair-display-check.exe", ""));
}

TEST_CASE("guard_declines_unlisted_process")
{
    // A user sets MIRADOR_DIRECT_MODE=1 machine-wide: a game must be declined.
    CHECK_FALSE(dvl::process_allowed(L"game.exe", ""));
    // Only the whole name counts.
    CHECK_FALSE(dvl::process_allowed(L"monado-service", ""));
    CHECK_FALSE(dvl::process_allowed(L"monado-service.exe.bak", ""));
    CHECK_FALSE(dvl::process_allowed(L"xmonado-service.exe", ""));
    CHECK_FALSE(dvl::process_allowed(L"monado.exe", ""));
    CHECK_FALSE(dvl::process_allowed(L"", ""));
    // A game is declined when the extras name other programs, too.
    CHECK_FALSE(dvl::process_allowed(L"game.exe", "foo.exe;bar.exe"));
}

TEST_CASE("guard_extra_list")
{
    CHECK(dvl::process_allowed(L"bar.exe", "foo.exe;bar.exe"));
    CHECK(dvl::process_allowed(L"foo.exe", "foo.exe;bar.exe"));
    CHECK(dvl::process_allowed(L"BAR.EXE", "foo.exe;bar.exe"));
    CHECK_FALSE(dvl::process_allowed(L"baz.exe", "foo.exe;bar.exe"));
    CHECK_FALSE(dvl::process_allowed(L"foo", "foo.exe;bar.exe"));

    // Blanks around a name and empty entries do not matter, and an empty name is never allowed.
    CHECK(dvl::process_allowed(L"bar.exe", " foo.exe ; bar.exe ;"));
    CHECK(dvl::process_allowed(L"bar.exe", ";;bar.exe"));
    CHECK_FALSE(dvl::process_allowed(L"", ";;"));
    CHECK_FALSE(dvl::process_allowed(L"", "foo.exe;;bar.exe"));

    // The built-in names stay allowed with extras given.
    CHECK(dvl::process_allowed(L"monado-service.exe", "foo.exe"));
    // A name with non-ASCII letters, in UTF-8 in the list.
    CHECK(dvl::process_allowed(L"sp\u00e4ter.exe", "sp\xc3\xa4ter.exe"));
    CHECK(dvl::process_allowed(L"SP\u00c4TER.EXE", "sp\xc3\xa4ter.exe"));
}

TEST_CASE("guard_basename")
{
    CHECK(dvl::basename_of(L"C:\\Program Files\\Monado\\monado-service.exe") == L"monado-service.exe");
    CHECK(dvl::basename_of(L"C:/tools/check.exe") == L"check.exe");
    CHECK(dvl::basename_of(L"plain.exe") == L"plain.exe");
    CHECK(dvl::basename_of(L"") == L"");
    CHECK(dvl::basename_of(L"C:\\dir\\") == L"");
}

TEST_CASE("guard_current_process_follows_the_environment")
{
    // This test program is dvl_tests.exe.
    CHECK(dvl::current_process_basename() == L"dvl_tests.exe");
    {
        dvl_test::ScopedEnv none(L"MIRADOR_DIRECT_MODE_PROCESSES", nullptr);
        CHECK_FALSE(dvl::current_process_allowed());
    }
    {
        dvl_test::ScopedEnv listed(L"MIRADOR_DIRECT_MODE_PROCESSES", L"other.exe;DVL_TESTS.exe");
        CHECK(dvl::current_process_allowed());
    }
    {
        dvl_test::ScopedEnv other(L"MIRADOR_DIRECT_MODE_PROCESSES", L"other.exe");
        CHECK_FALSE(dvl::current_process_allowed());
    }
}

TEST_CASE("negotiate_accepts_interface_2_and_up")
{
    dvl_test::ScopedEnv processes(L"MIRADOR_DIRECT_MODE_PROCESSES", L"dvl_tests.exe");
    dvl_test::ScopedEnv quiet(L"MIRADOR_DIRECT_MODE_LOG", L"off");
    dvl_test::LayerDll dll;
    REQUIRE(dll.loaded());

    for (uint32_t offered : {2u, 3u, 9u}) {
        INFO("loader offers interface " << offered);
        auto n = dvl_test::negotiate(dll, offered);
        CHECK(n.result == VK_SUCCESS);
        CHECK(n.iface.loaderLayerInterfaceVersion == 2);
        CHECK(n.iface.pfnGetInstanceProcAddr != nullptr);
        CHECK(n.iface.pfnGetDeviceProcAddr != nullptr);
    }

    // An older loader (interface 1) is refused; the layer needs the negotiated entry points.
    for (uint32_t offered : {1u, 0u}) {
        INFO("loader offers interface " << offered);
        auto n = dvl_test::negotiate(dll, offered);
        CHECK(n.result == VK_ERROR_INITIALIZATION_FAILED);
    }

    // Not our structure type.
    VkNegotiateLayerInterface wrong{};
    wrong.sType = static_cast<VkNegotiateLayerStructType>(7);
    wrong.loaderLayerInterfaceVersion = 2;
    auto negotiate = dll.proc<PFN_vkNegotiateLoaderLayerInterfaceVersion>("vkNegotiateLoaderLayerInterfaceVersion");
    REQUIRE(negotiate != nullptr);
    CHECK(negotiate(&wrong) == VK_ERROR_INITIALIZATION_FAILED);
    CHECK(negotiate(nullptr) == VK_ERROR_INITIALIZATION_FAILED);
}

TEST_CASE("layer_exports_only_the_negotiation_function")
{
    dvl_test::LayerDll dll;
    REQUIRE(dll.loaded());
    CHECK(dll.proc<PFN_vkNegotiateLoaderLayerInterfaceVersion>("vkNegotiateLoaderLayerInterfaceVersion") != nullptr);
    // When negotiation fails, the loader falls back to looking these names up in the DLL (checked with loader
    // 1.4.357). With them exported, a layer that declines would be inserted into the chain anyway; without them the
    // loader skips it. So they are not exported; negotiation hands the same functions out.
    CHECK(dll.proc<PFN_vkGetInstanceProcAddr>("vkGetInstanceProcAddr") == nullptr);
    CHECK(dll.proc<PFN_vkGetDeviceProcAddr>("vkGetDeviceProcAddr") == nullptr);
}

TEST_CASE("negotiation_pins_the_dll")
{
    // The loader unloads a layer at vkDestroyInstance; the layer's state (log file, start line, console handler) is
    // per process, so an accepted process keeps the DLL mapped.
    dvl_test::ScopedEnv processes(L"MIRADOR_DIRECT_MODE_PROCESSES", L"dvl_tests.exe");
    dvl_test::ScopedEnv quiet(L"MIRADOR_DIRECT_MODE_LOG", L"off");
    {
        dvl_test::LayerDll dll;
        REQUIRE(dll.loaded());
        REQUIRE(dvl_test::negotiate(dll, 2).result == VK_SUCCESS);
    } // FreeLibrary
    CHECK(GetModuleHandleW(L"mirador_direct_mode.dll") != nullptr);
}

TEST_CASE("negotiate_declines_process_not_on_the_list")
{
    dvl_test::ScopedEnv quiet(L"MIRADOR_DIRECT_MODE_LOG", L"off");
    dvl_test::LayerDll dll;
    REQUIRE(dll.loaded());
    {
        dvl_test::ScopedEnv processes(L"MIRADOR_DIRECT_MODE_PROCESSES", L"game.exe");
        CHECK(dvl_test::negotiate(dll, 2).result == VK_ERROR_INITIALIZATION_FAILED);
    }
    {
        dvl_test::ScopedEnv processes(L"MIRADOR_DIRECT_MODE_PROCESSES", nullptr);
        CHECK(dvl_test::negotiate(dll, 2).result == VK_ERROR_INITIALIZATION_FAILED);
    }
    {
        // The guard looks at the environment on every call, not once.
        dvl_test::ScopedEnv processes(L"MIRADOR_DIRECT_MODE_PROCESSES", L"game.exe;dvl_tests.exe");
        CHECK(dvl_test::negotiate(dll, 2).result == VK_SUCCESS);
    }
}

TEST_CASE("manifest_declares_the_layer")
{
    std::ifstream in(std::filesystem::u8path(DVL_LAYER_MANIFEST), std::ios::binary);
    REQUIRE(in.good());
    const std::string json((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());

    const char* expected[] = {
        "\"file_format_version\": \"1.2.0\"",
        "\"name\": \"VK_LAYER_MIRADOR_direct_mode\"",
        "\"type\": \"GLOBAL\"",
        "\"library_path\": \".\\\\mirador_direct_mode.dll\"",
        "\"api_version\": \"1.4.309\"",
        "\"implementation_version\": \"1\"",
        "\"vkNegotiateLoaderLayerInterfaceVersion\": \"vkNegotiateLoaderLayerInterfaceVersion\"",
        "\"name\": \"VK_EXT_display_surface_counter\"",
        "\"name\": \"VK_EXT_display_control\"",
        "\"vkDisplayPowerControlEXT\"",
        "\"vkRegisterDeviceEventEXT\"",
        "\"vkRegisterDisplayEventEXT\"",
        "\"vkGetSwapchainCounterEXT\"",
        "\"enable_environment\"",
        "\"MIRADOR_DIRECT_MODE\": \"1\"",
        "\"disable_environment\"",
        "\"DISABLE_MIRADOR_DIRECT_MODE\": \"1\"",
    };
    for (const char* text : expected) {
        INFO("manifest lacks " << text);
        CHECK(json.find(text) != std::string::npos);
    }
    // Two extensions, each at revision 1, and nothing configured in by accident.
    CHECK(json.find("\"spec_version\": \"1\"") != std::string::npos);
    CHECK(json.find("@") == std::string::npos);
}
