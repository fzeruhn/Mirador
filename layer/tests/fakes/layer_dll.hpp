// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <windows.h>

#include <filesystem>
#include <string>

// Vulkan's prototypes are off (the dvl_pure target defines VK_NO_PROTOTYPES): the tests reach the layer only through
// the DLL's exports, the way the loader does.
#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

namespace dvl_test {

// Sets an environment variable of this process for a scope and puts the old value back.
class ScopedEnv {
public:
    ScopedEnv(const wchar_t* name, const wchar_t* value) : name_(name)
    {
        DWORD n = GetEnvironmentVariableW(name, nullptr, 0);
        if (n != 0) {
            had_old_ = true;
            old_.resize(n);
            old_.resize(GetEnvironmentVariableW(name, old_.data(), n));
        }
        SetEnvironmentVariableW(name, value);
    }
    ~ScopedEnv() { SetEnvironmentVariableW(name_.c_str(), had_old_ ? old_.c_str() : nullptr); }
    ScopedEnv(const ScopedEnv&) = delete;
    ScopedEnv& operator=(const ScopedEnv&) = delete;

private:
    std::wstring name_;
    std::wstring old_;
    bool had_old_ = false;
};

// The built layer DLL (the CMake build gives its path), loaded the way the loader loads it. DVL_LAYER_TEST_DLL is the
// test build of the same objects, which also exports the display-session seam (layer/test_hooks.cpp).
class LayerDll {
public:
    explicit LayerDll(const char* path = DVL_LAYER_DLL)
        : module_(LoadLibraryW(std::filesystem::u8path(path).wstring().c_str()))
    {
    }
    ~LayerDll()
    {
        if (module_) {
            FreeLibrary(module_);
        }
    }
    LayerDll(const LayerDll&) = delete;
    LayerDll& operator=(const LayerDll&) = delete;

    bool loaded() const { return module_ != nullptr; }
    template <class Fn>
    Fn proc(const char* name) const
    {
        return reinterpret_cast<Fn>(GetProcAddress(module_, name));
    }

private:
    HMODULE module_;
};

// A negotiated layer: what vkNegotiateLoaderLayerInterfaceVersion gave back.
struct NegotiatedLayer {
    VkResult result = VK_ERROR_INITIALIZATION_FAILED;
    VkNegotiateLayerInterface iface{};
};

inline NegotiatedLayer negotiate(const LayerDll& dll, uint32_t offered_version)
{
    NegotiatedLayer out;
    auto fn = dll.proc<PFN_vkNegotiateLoaderLayerInterfaceVersion>("vkNegotiateLoaderLayerInterfaceVersion");
    if (!fn) {
        return out;
    }
    out.iface.sType = LAYER_NEGOTIATE_INTERFACE_STRUCT;
    out.iface.loaderLayerInterfaceVersion = offered_version;
    out.result = fn(&out.iface);
    return out;
}

} // namespace dvl_test
