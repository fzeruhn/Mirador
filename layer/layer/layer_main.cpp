// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The layer DLL's entry points: negotiation with the loader, the proc-address functions it hands over, vkCreateInstance
// and vkDestroyInstance, and DllMain. The other intercepts live in their own files and come in through the lists of
// intercepts.hpp (device.cpp, display_intercepts.cpp, surface_intercepts.cpp; Tasks 10-11 add theirs); each is also in
// the intercepted list of handled_commands.cpp (Task 8), and a test checks that the two agree.

#include <windows.h>

#include <vulkan/vulkan.h>
#include <vulkan/vk_layer.h>

#include <atomic>
#include <cstring>
#include <memory>
#include <new>
#include <string>
#include <string_view>
#include <vector>

#include "layer/dispatch.hpp"
#include "layer/env.hpp"
#include "layer/exit.hpp"
#include "layer/guard.hpp"
#include "layer/handled_commands.hpp"
#include "layer/intercepts.hpp"
#include "layer/log.hpp"

#ifndef DVL_LAYER_VERSION
#define DVL_LAYER_VERSION "unknown"
#endif

// The one export. vk_layer.h declares the negotiation function without dllexport.
//
// vkGetInstanceProcAddr and vkGetDeviceProcAddr are NOT exported by name, on purpose. The loader asks the DLL for them
// by name only when negotiation fails, as the fallback of its older interface. With the names exported, a layer that
// declines (the process guard, a loader older than interface 2) would be put into the chain anyway and work from those
// exports. Without them the loader logs "Failed to find 'vkGetInstanceProcAddr' in layer" and skips the layer, which
// is what declining has to mean (checked with vulkaninfo and loader 1.4.357). Negotiation hands out the same functions.
#pragma comment(linker, "/EXPORT:vkNegotiateLoaderLayerInterfaceVersion")

namespace {

PFN_vkVoidFunction VKAPI_CALL layer_GetInstanceProcAddr(VkInstance instance, const char* name);
PFN_vkVoidFunction VKAPI_CALL layer_GetDeviceProcAddr(VkDevice device, const char* name);

// The loader/layer interface this layer implements (vkNegotiateLoaderLayerInterfaceVersion): 2 is the one that hands
// the proc-address functions over in negotiation, and the newest there is. The layer needs it, so a loader that offers
// less is refused (review focus 5).
constexpr uint32_t kInterfaceVersion = 2;

std::atomic<uint32_t> g_loader_offered{0};
std::atomic<bool> g_pinned{false};
std::atomic<bool> g_started{false};
std::atomic<bool> g_decline_logged{false};

std::string process_name()
{
    return dvl::to_utf8(dvl::current_process_basename());
}

// Keeps this DLL mapped until the process ends. The loader unloads a layer at every vkDestroyInstance (design 5), but
// the layer's state is per process by design: one log file, one start line, the console handler, and later the display
// session (design 6.10: "the DLL is pinned"). A process that declined never gets here, so the loader still unloads it.
void pin_this_dll()
{
    if (g_pinned.exchange(true)) {
        return;
    }
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                       reinterpret_cast<LPCWSTR>(&pin_this_dll), &self);
}

/*
 * Chain walking. The loader puts a VkLayerInstanceCreateInfo / VkLayerDeviceCreateInfo with function
 * VK_LAYER_LINK_INFO into the pNext chain; each layer reads its next layer from it and moves the link on by one for
 * the layer below. A layer that skips this breaks the chain for everything under it.
 */

VkLayerInstanceCreateInfo* find_instance_link(const VkInstanceCreateInfo* ci)
{
    for (auto* s = static_cast<const VkBaseInStructure*>(ci->pNext); s != nullptr; s = s->pNext) {
        if (s->sType == VK_STRUCTURE_TYPE_LOADER_INSTANCE_CREATE_INFO) {
            auto* info = const_cast<VkLayerInstanceCreateInfo*>(reinterpret_cast<const VkLayerInstanceCreateInfo*>(s));
            if (info->function == VK_LAYER_LINK_INFO) {
                return info;
            }
        }
    }
    return nullptr;
}

// The first vkCreateInstance of the process: the one start line (design 8) and the console handler (design 6.10).
void on_first_instance() noexcept
{
    if (g_started.exchange(true)) {
        return;
    }
    dvl::install_console_handler();
    try {
        dvl::log(dvl::Level::Info,
                 "VK_LAYER_DREAMAIR_nvapi_direct %s (implementation 1): loader interface offered %u, negotiated %u, "
                 "process %s (pid %lu)",
                 DVL_LAYER_VERSION, g_loader_offered.load(), kInterfaceVersion, process_name().c_str(),
                 GetCurrentProcessId());
    } catch (...) {
        // Only the line is lost; the instance exists and the call succeeds.
    }
}

/*
 * Instance and device creation and destruction.
 */

VKAPI_ATTR VkResult VKAPI_CALL layer_CreateInstance(const VkInstanceCreateInfo* ci, const VkAllocationCallbacks* alloc,
                                                    VkInstance* out)
{
    try {
        VkLayerInstanceCreateInfo* link = find_instance_link(ci);
        if (link == nullptr || link->u.pLayerInfo == nullptr) {
            dvl::log(dvl::Level::Error, "vkCreateInstance: the loader passed no layer link info");
            return VK_ERROR_INITIALIZATION_FAILED;
        }
        const PFN_vkGetInstanceProcAddr next_gipa = link->u.pLayerInfo->pfnNextGetInstanceProcAddr;
        const auto next_create = reinterpret_cast<PFN_vkCreateInstance>(next_gipa(VK_NULL_HANDLE, "vkCreateInstance"));
        if (next_create == nullptr) {
            dvl::log(dvl::Level::Error, "vkCreateInstance: the next layer has no vkCreateInstance");
            return VK_ERROR_INITIALIZATION_FAILED;
        }
        link->u.pLayerInfo = link->u.pLayerInfo->pNext; // the layer below finds its own link

        // VK_EXT_display_surface_counter is ours: the driver lacks it (the loader would drop it too), so it does not
        // go down.
        std::vector<const char*> names;
        names.reserve(ci->enabledExtensionCount);
        bool asked_counter = false;
        for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) {
            if (std::strcmp(ci->ppEnabledExtensionNames[i], VK_EXT_DISPLAY_SURFACE_COUNTER_EXTENSION_NAME) == 0) {
                asked_counter = true;
            } else {
                names.push_back(ci->ppEnabledExtensionNames[i]);
            }
        }
        VkInstanceCreateInfo down = *ci;
        down.enabledExtensionCount = static_cast<uint32_t>(names.size());
        down.ppEnabledExtensionNames = names.data();

        const VkResult result = next_create(&down, alloc, out);
        if (result != VK_SUCCESS) {
            dvl::log(dvl::Level::Debug, "vkCreateInstance: the next layer returned %d", static_cast<int>(result));
            return result;
        }
        const VkInstance instance = *out;
        try {
            dvl::add_instance(dvl::make_instance_data(instance, next_gipa));
        } catch (...) {
            dvl::log(dvl::Level::Error, "vkCreateInstance: out of memory registering the instance");
            const auto next_destroy = reinterpret_cast<PFN_vkDestroyInstance>(next_gipa(instance, "vkDestroyInstance"));
            if (next_destroy != nullptr) {
                next_destroy(instance, alloc);
            }
            *out = VK_NULL_HANDLE;
            return VK_ERROR_OUT_OF_HOST_MEMORY;
        }

        on_first_instance();
        // Design 6.3: an enabled extension with a command that takes our objects, and that the layer neither handles
        // nor lists as not applicable, is logged (a layer bug, not the application's).
        dvl::check_command_coverage(ci->ppEnabledExtensionNames, ci->enabledExtensionCount, "vkCreateInstance");
        const uint32_t api = ci->pApplicationInfo != nullptr ? ci->pApplicationInfo->apiVersion : 0;
        dvl::log(dvl::Level::Debug, "vkCreateInstance: API %u.%u, %u extensions, VK_EXT_display_surface_counter %s",
                 VK_API_VERSION_MAJOR(api), VK_API_VERSION_MINOR(api), ci->enabledExtensionCount,
                 asked_counter ? "asked for (not passed down)" : "not asked for");
        return VK_SUCCESS;
    } catch (const std::bad_alloc&) {
        return VK_ERROR_OUT_OF_HOST_MEMORY;
    } catch (...) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
}

VKAPI_ATTR void VKAPI_CALL layer_DestroyInstance(VkInstance instance, const VkAllocationCallbacks* alloc)
{
    if (instance == VK_NULL_HANDLE) {
        return;
    }
    // Out of the table first: the handle is gone once the call below returns.
    const std::unique_ptr<dvl::InstanceData> data = dvl::remove_instance(instance);
    if (!data || data->DestroyInstance == nullptr) {
        // Not ours to forget, but the loader's and the driver's objects must not leak: down through the one function
        // the layer below has, if there is one.
        const PFN_vkDestroyInstance fallback = dvl::fallback_destroy_instance();
        dvl::log(dvl::Level::Error, "vkDestroyInstance: instance %p is not one the layer knows; %s",
                 static_cast<void*>(instance), fallback != nullptr ? "passed down" : "it cannot be passed down");
        if (fallback != nullptr) {
            fallback(instance, alloc);
        }
        return;
    }
    dvl::log(dvl::Level::Debug, "vkDestroyInstance: instance %p", static_cast<void*>(instance));
    data->DestroyInstance(instance, alloc);
}

/*
 * The intercept tables: what the layer answers itself, by Vulkan name. Anything else goes to the layer below.
 */

struct Intercept {
    std::string_view name;
    PFN_vkVoidFunction function;
};

#define DVL_INTERCEPT(vk_name, function) Intercept{vk_name, reinterpret_cast<PFN_vkVoidFunction>(&function)}
#define DVL_LISTED_INTERCEPT(name) DVL_INTERCEPT("vk" #name, dvl::layer_##name),

PFN_vkVoidFunction find_device_intercept(std::string_view name)
{
    static const Intercept table[] = {
        DVL_INTERCEPT("vkGetDeviceProcAddr", layer_GetDeviceProcAddr),
        DVL_DEVICE_INTERCEPTS(DVL_LISTED_INTERCEPT)
    };
    for (const Intercept& entry : table) {
        if (entry.name == name) {
            return entry.function;
        }
    }
    return nullptr;
}

// vkGetInstanceProcAddr also hands out device commands (an application may ask for them at instance level), so the
// device table is searched too. The physical-device commands are here as well: the loader fills its instance dispatch
// table through this function for every command it knows (all of the layer's), so none needs
// pfnGetPhysicalDeviceProcAddr (see the negotiation).
PFN_vkVoidFunction find_instance_intercept(std::string_view name)
{
    static const Intercept table[] = {
        DVL_INTERCEPT("vkGetInstanceProcAddr", layer_GetInstanceProcAddr),
        DVL_INTERCEPT("vkCreateInstance", layer_CreateInstance),
        DVL_INTERCEPT("vkDestroyInstance", layer_DestroyInstance),
        DVL_INSTANCE_INTERCEPTS(DVL_LISTED_INTERCEPT)
    };
    for (const Intercept& entry : table) {
        if (entry.name == name) {
            return entry.function;
        }
    }
    return find_device_intercept(name);
}

#undef DVL_LISTED_INTERCEPT
#undef DVL_INTERCEPT

PFN_vkVoidFunction VKAPI_CALL layer_GetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (name == nullptr) {
        return nullptr;
    }
    if (const PFN_vkVoidFunction ours = find_instance_intercept(name)) {
        return ours;
    }
    // Not ours: the next layer's answer. Without an instance there is no next layer to ask.
    const dvl::InstanceData* data = dvl::find_instance_data(instance);
    return data != nullptr ? data->gipa(instance, name) : nullptr;
}

PFN_vkVoidFunction VKAPI_CALL layer_GetDeviceProcAddr(VkDevice device, const char* name)
{
    if (name == nullptr) {
        return nullptr;
    }
    if (const PFN_vkVoidFunction ours = find_device_intercept(name)) {
        return ours;
    }
    const dvl::DeviceData* data = dvl::find_device_data(device);
    return data != nullptr ? data->gdpa(device, name) : nullptr;
}

} // namespace

extern "C" VKAPI_ATTR VkResult VKAPI_CALL vkNegotiateLoaderLayerInterfaceVersion(VkNegotiateLayerInterface* negotiate)
{
    try {
        if (negotiate == nullptr || negotiate->sType != LAYER_NEGOTIATE_INTERFACE_STRUCT) {
            return VK_ERROR_INITIALIZATION_FAILED;
        }
        const uint32_t offered = negotiate->loaderLayerInterfaceVersion;

        // The process guard (design 5): a safety net for a machine-wide DREAMAIR_VK_LAYER=1. The loader skips a layer
        // that fails here (its DLL stays loaded until vkDestroyInstance, and the layer's extension names stay listed,
        // which is why the guard cannot keep goal 4 by itself). One line, and no new log file: see log_append.
        if (!dvl::current_process_allowed()) {
            if (!g_decline_logged.exchange(true)) {
                dvl::log_append(dvl::Level::Warn,
                                "declining in %s (pid %lu): DREAMAIR_VK_LAYER is set, but this program is not one the "
                                "layer is for (monado-service.exe, dreamair-display-check.exe, or the names in "
                                "DREAMAIR_VK_LAYER_PROCESSES). Do not set DREAMAIR_VK_LAYER machine-wide.",
                                process_name().c_str(), GetCurrentProcessId());
            }
            return VK_ERROR_INITIALIZATION_FAILED;
        }

        if (offered < kInterfaceVersion) {
            dvl::log(dvl::Level::Error,
                     "the loader in %s offers layer interface version %u; the layer needs %u or newer. Use a "
                     "Vulkan loader 1.1.77 or newer (Monado's bundled vulkan-1.dll).",
                     process_name().c_str(), offered, kInterfaceVersion);
            return VK_ERROR_INITIALIZATION_FAILED;
        }

        pin_this_dll();
        g_loader_offered = offered;
        negotiate->loaderLayerInterfaceVersion = kInterfaceVersion;
        negotiate->pfnGetInstanceProcAddr = layer_GetInstanceProcAddr;
        negotiate->pfnGetDeviceProcAddr = layer_GetDeviceProcAddr;
        // Optional in interface 2, and not needed: the loader asks it only for physical-device commands it has no
        // trampoline of its own for (loader_phys_dev_ext_gpa_*), and every one the layer intercepts is a core or known
        // extension command, which the loader's trampolines dispatch through the table it filled from
        // vkGetInstanceProcAddr (loader 1.4.309: loader_init_instance_extension_dispatch_table). M0 saw Monado's
        // vkGetPhysicalDeviceSurfaceCapabilities2EXT and display queries reach the layer this way.
        negotiate->pfnGetPhysicalDeviceProcAddr = nullptr;
        dvl::log(dvl::Level::Debug, "negotiated: the loader offers layer interface version %u, the layer uses %u",
                 offered, kInterfaceVersion);
        return VK_SUCCESS;
    } catch (...) {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(instance);
        break;
    case DLL_PROCESS_DETACH:
        // lpReserved non-NULL: the process is exiting, and nothing happens (design 6.10). NULL is FreeLibrary, which
        // only reaches a DLL that never accepted the process (see pin_this_dll).
        dvl::on_process_detach(reserved);
        break;
    default:
        break;
    }
    return TRUE;
}
