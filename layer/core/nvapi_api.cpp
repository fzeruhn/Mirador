// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The real Api: NVAPI through nvapi64.dll's nvapi_QueryInterface. Ported from the nvdm_* functions of Monado's
// comp_window_nvapi_direct.c (nvdm_load to nvdm_poll_stats) and the standalone nvdm_probe. The target's
// nvdm_create_d3d11 is not here: the display session makes the D3D11 device.

#include "api.hpp"

#include <wchar.h>

#include <algorithm>
#include <cstdio>
#include <iterator>
#include <string>
#include <utility>

#include "clock.hpp"

namespace nvdm {
namespace {

using QueryInterfaceFn = void*(__cdecl*)(uint32_t id);
using InitializeFn = int(__cdecl*)();

// The private calls take at most six integer or pointer arguments; passing extra zeroes is harmless on x64.
using CallFn = int(__cdecl*)(uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t, uintptr_t);

template <class T>
uintptr_t arg(T* pointer)
{
    return reinterpret_cast<uintptr_t>(pointer);
}

// --- EDID ------------------------------------------------------------------------------------------------------

constexpr size_t kEdidBlock = 128;

bool looks_like_edid(const std::vector<uint8_t>& edid)
{
    static const uint8_t kHeader[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
    return edid.size() >= kEdidBlock && std::equal(std::begin(kHeader), std::end(kHeader), edid.begin());
}

// NvAPI_GPU_GetEDID takes one 256-byte page at a time. Our declaration of its version-3 struct; the version value
// carries the struct's size, so a driver that disagrees with this layout refuses the call instead of writing into it.
constexpr uint32_t kEdidPage = 256;
struct EdidPage {
    uint32_t version;
    uint8_t data[kEdidPage];
    uint32_t size;   // Of the whole EDID.
    uint32_t id;     // Changes when the display's EDID does; the same for every page of one read.
    uint32_t offset; // Of this page in the EDID, a multiple of 256.
};
static_assert(sizeof(EdidPage) == 272, "NV_EDID version 3 is 272 bytes");
constexpr uint32_t kEdidPageVersion = sizeof(EdidPage) | (3u << 16);

// More than the layout needs, in case a driver writes past it.
struct EdidBuffer {
    EdidPage page;
    uint8_t spare[256];
};

constexpr uint32_t kEdidMaxPages = 16; // 4 KiB, far more than the headset has.

// --- Registry --------------------------------------------------------------------------------------------------

struct RegKey {
    HKEY key = nullptr;
    RegKey() = default;
    RegKey(const RegKey&) = delete;
    RegKey& operator=(const RegKey&) = delete;
    ~RegKey()
    {
        if (key != nullptr) {
            RegCloseKey(key);
        }
    }
};

std::vector<uint8_t> read_binary_value(HKEY parent, const wchar_t* subkey, const wchar_t* name)
{
    constexpr DWORD kMaxBytes = 1u << 16;
    DWORD size = 0;
    if (RegGetValueW(parent, subkey, name, RRF_RT_REG_BINARY, nullptr, nullptr, &size) != ERROR_SUCCESS || size == 0 ||
        size > kMaxBytes) {
        return {};
    }
    std::vector<uint8_t> data(size);
    DWORD got = size;
    if (RegGetValueW(parent, subkey, name, RRF_RT_REG_BINARY, nullptr, data.data(), &got) != ERROR_SUCCESS) {
        return {};
    }
    data.resize(got);
    return data;
}

// --- The port --------------------------------------------------------------------------------------------------

class NvapiApi final : public Api {
public:
    NvapiApi()
    {
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        qpc_frequency_ = frequency.QuadPart;
    }

    int initialize() override
    {
        if (initialized_) {
            return 0;
        }
        if (dll_ == nullptr) {
            // nvapi64.dll sits in System32. Searching only there keeps a layer that is injected into any process
            // from loading a copy planted next to the executable.
            dll_ = LoadLibraryExW(L"nvapi64.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (dll_ == nullptr) {
                return kNoNvapi;
            }
        }
        if (query_ == nullptr) {
            query_ = reinterpret_cast<QueryInterfaceFn>(GetProcAddress(dll_, "nvapi_QueryInterface"));
            if (query_ == nullptr) {
                return kNoEntry;
            }
        }

        auto initialize_nvapi = reinterpret_cast<InitializeFn>(query_(Initialize));
        if (initialize_nvapi == nullptr) {
            return kNoEntry;
        }
        const int ret = initialize_nvapi();
        if (ret != 0) {
            return ret;
        }

        for (size_t i = 0; i < std::size(kAllIds); i++) {
            fns_[i] = query_(kAllIds[i].id);
        }
        initialized_ = true;
        return 0;
    }

    const char* first_missing_id() override
    {
        if (!initialized_) {
            return "Initialize";
        }
        for (size_t i = 0; i < std::size(kAllIds); i++) {
            if (kAllIds[i].is_private && fns_[i] == nullptr) {
                return kAllIds[i].name;
            }
        }
        return nullptr;
    }

    std::string driver_version() override
    {
        uint32_t version = 0;
        char branch[256] = {}; // NvAPI_ShortString is 64 chars, leave room.
        if (call(GetDriverAndBranchVersion, arg(&version), arg(branch)) != 0 || version == 0) {
            return {};
        }
        char text[32];
        snprintf(text, sizeof(text), "%u.%02u", version / 100, version % 100);
        return text;
    }

    int enumerate(uint32_t vendor, std::vector<uint32_t>& ids) override
    {
        ids.clear();
        uint32_t count = 8; // In: room for this many; out: how many there are.
        uint32_t buffer[32] = {};
        const int ret = call(Enumerate, vendor, arg(&count), arg(buffer), 1);
        if (ret != 0) {
            return ret;
        }
        count = std::min<uint32_t>(count, static_cast<uint32_t>(std::size(buffer)));
        for (uint32_t i = 0; i < count; i++) {
            if (buffer[i] != 0) {
                ids.push_back(buffer[i]);
            }
        }
        return 0;
    }

    int adapter_luid(uint32_t display_id, LUID& out) override
    {
        void* gpu = nullptr;
        int ret = call(GetPhysicalGpuFromDisplayId, display_id, arg(&gpu));
        if (ret != 0) {
            return ret;
        }
        if (gpu == nullptr) {
            return kBadOutput; // NVAPI said OK but named no GPU; the next call would be given NULL.
        }
        LUID luid{};
        ret = call(GetAdapterIdFromPhysicalGpu, arg(gpu), arg(&luid));
        if (ret != 0) {
            return ret;
        }
        out = luid;
        return 0;
    }

    EdidRoute read_edid(uint32_t display_id, std::vector<uint8_t>& out) override
    {
        out.clear();
        if (read_edid_of_display(display_id, out)) {
            return EdidRoute::Nvapi;
        }
        out.clear();
        if (read_edid_of_output(display_id, out)) {
            return EdidRoute::NvapiOutput;
        }
        out.clear();
        return read_edid_from_registry(out) ? EdidRoute::Registry : EdidRoute::None;
    }

    int enable_direct_mode(uint32_t vendor) override { return call(EnableDirectMode, vendor); }

    int bind_device(uint32_t display_id, IUnknown* d3d11) override
    {
        if (d3d11 == nullptr) {
            return kBadArgument; // No driver call, and an earlier bind stays as it is.
        }
        bound_ = false;
        handle_ = Handle{};
        handle_.display_id = display_id;
        const int ret = call(BindDevice, kVendorPvr, arg(d3d11), arg(&handle_));
        if (ret != 0) {
            return ret;
        }
        if (handle_.nvapi == nullptr) {
            return kBadOutput;
        }
        bound_ = true;
        return 0;
    }

    int create_back_buffer(const Mode& mode, BackBuffer& out) override
    {
        if (!bound_) {
            return kNotBound;
        }
        Mode request = mode; // The call takes a pointer to a writable mode.
        // Only the first dword of each output is known; Pimax passes room for more, so do we.
        uint64_t tag[4] = {};
        uint64_t shared[4] = {};
        const int ret = call(CreateBackBuffer, arg(&handle_), arg(&request), arg(tag), arg(shared));
        if (ret != 0) {
            return ret;
        }
        if (shared[0] == 0) {
            return kBadOutput;
        }
        out.tag = static_cast<uint32_t>(tag[0]);
        out.shared = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(static_cast<uint32_t>(shared[0])));
        return 0;
    }

    int set_display_mode(const Mode& mode) override
    {
        if (!bound_) {
            return kNotBound;
        }
        Mode request = mode;
        return call(SetDisplayMode, arg(&handle_), arg(&request));
    }

    int present(uint64_t tag) override
    {
        if (!bound_) {
            return kNotBound;
        }
        return call(Present, arg(&handle_), static_cast<uintptr_t>(tag), 1);
    }

    int present_stats(RefreshStats& out) override
    {
        if (!bound_) {
            return kNotBound;
        }
        RawStats stats; // Version set, and room for what the driver writes past its 24 bytes.
        const int ret = call(GetPresentStats, arg(&handle_), arg(&stats));
        if (ret != 0) {
            return ret;
        }
        out.refresh_num = stats.refresh_num;
        out.refresh_den = stats.refresh_den;
        out.counter = stats.frame_counter;
        out.qpc_ns = qpc_ticks_to_ns(static_cast<int64_t>(stats.qpc), qpc_frequency_);
        return 0;
    }

private:
    CallFn function(Id id) const
    {
        for (size_t i = 0; i < std::size(kAllIds); i++) {
            if (kAllIds[i].id == id) {
                return reinterpret_cast<CallFn>(fns_[i]);
            }
        }
        return nullptr;
    }

    int call(Id id, uintptr_t a = 0, uintptr_t b = 0, uintptr_t c = 0, uintptr_t d = 0, uintptr_t e = 0,
             uintptr_t f = 0) const
    {
        const CallFn fn = function(id);
        return fn != nullptr ? fn(a, b, c, d, e, f) : kNoEntry;
    }

    // The first route: NvAPI_GPU_GetEDID on the display's GPU with the display id itself as its output. It answers for
    // the direct-mode display: the probe of 2026-10-31 read the Dream Air's 384-byte EDID this way (versions 3 and 2 of
    // the struct; version 1 gave zeroes), with no registry value needed.
    bool read_edid_of_display(uint32_t display_id, std::vector<uint8_t>& out)
    {
        void* gpu = nullptr;
        if (call(GetPhysicalGpuFromDisplayId, display_id, arg(&gpu)) != 0 || gpu == nullptr) {
            return false;
        }
        return read_edid_pages(gpu, display_id, out);
    }

    // The second route: the GPU's output id for the display id. In direct mode GetEDID answers it with
    // NVAPI_DATA_NOT_FOUND (-121, M1 and the probe); kept for drivers that answer it.
    bool read_edid_of_output(uint32_t display_id, std::vector<uint8_t>& out)
    {
        void* gpu = nullptr;
        uint32_t output_id = 0;
        if (call(GetGpuAndOutputIdFromDisplayId, display_id, arg(&gpu), arg(&output_id)) != 0 || gpu == nullptr) {
            return false;
        }
        return read_edid_pages(gpu, output_id, out);
    }

    // The EDID page by page, from GetEDID with `output` (an output id, or a display id). Any surprise is a plain
    // failure, and the next route takes over.
    bool read_edid_pages(void* gpu, uint32_t output, std::vector<uint8_t>& out)
    {
        uint32_t total = 0;
        uint32_t edid_id = 0;
        for (uint32_t page = 0; page < kEdidMaxPages; page++) {
            EdidBuffer buffer{};
            buffer.page.version = kEdidPageVersion;
            buffer.page.offset = page * kEdidPage;
            if (call(GetEDID, arg(gpu), output, arg(&buffer)) != 0) {
                return false;
            }
            if (page == 0) {
                total = buffer.page.size;
                edid_id = buffer.page.id;
                if (total < kEdidBlock || total > kEdidMaxPages * kEdidPage) {
                    return false;
                }
            } else if (buffer.page.size != total || buffer.page.id != edid_id) {
                return false; // The EDID changed while it was being read.
            }
            const uint32_t take = std::min(kEdidPage, total - buffer.page.offset);
            out.insert(out.end(), buffer.page.data, buffer.page.data + take);
            if (out.size() >= total) {
                break;
            }
        }
        return out.size() == total && looks_like_edid(out);
    }

    HMODULE dll_ = nullptr;
    QueryInterfaceFn query_ = nullptr;
    bool initialized_ = false;
    void* fns_[std::size(kAllIds)] = {}; // nvapi_QueryInterface's answer for each id of kAllIds, NULL if missing.

    // Filled by bind_device; the driver keeps pointing into the calls that take it, so it stays in place.
    Handle handle_;
    bool bound_ = false;

    int64_t qpc_frequency_ = 1;
};

} // namespace

std::unique_ptr<Api> make_nvapi()
{
    return std::make_unique<NvapiApi>();
}

namespace {

// The Dream Air's model key: manufacturer "PVR", product 0x1019 (design 6.3).
constexpr wchar_t kDreamAirModelKey[] = L"PVR1019";

// The first instance under `model_name` (a key of Enum\DISPLAY) that has a valid EDID.
bool edid_from_model(HKEY display, const wchar_t* model_name, std::vector<uint8_t>& out)
{
    RegKey model;
    if (RegOpenKeyExW(display, model_name, 0, KEY_READ, &model.key) != ERROR_SUCCESS) {
        return false;
    }
    for (DWORD i = 0;; i++) {
        wchar_t instance_name[256];
        DWORD instance_length = static_cast<DWORD>(std::size(instance_name));
        const LONG status =
            RegEnumKeyExW(model.key, i, instance_name, &instance_length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_MORE_DATA) {
            continue; // A name that does not fit the buffer is no instance id; the next key may be fine.
        }
        if (status != ERROR_SUCCESS) {
            break; // ERROR_NO_MORE_ITEMS, or something that makes going on pointless.
        }
        const std::wstring subkey = std::wstring(instance_name) + L"\\Device Parameters";
        std::vector<uint8_t> edid = read_binary_value(model.key, subkey.c_str(), L"EDID");
        if (looks_like_edid(edid)) {
            out = std::move(edid);
            return true;
        }
    }
    return false;
}

} // namespace

bool read_edid_from_display_key(HKEY display, std::vector<uint8_t>& out)
{
    out.clear();

    // The Dream Air's own key first: it is the one that is right, whatever else Pimax ships under the manufacturer id.
    if (edid_from_model(display, kDreamAirModelKey, out)) {
        return true;
    }

    for (DWORD m = 0;; m++) {
        wchar_t model_name[256];
        DWORD model_length = static_cast<DWORD>(std::size(model_name));
        const LONG status = RegEnumKeyExW(display, m, model_name, &model_length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_MORE_DATA) {
            continue;
        }
        if (status != ERROR_SUCCESS) {
            break;
        }
        // Any other monitor of manufacturer "PVR".
        if (_wcsnicmp(model_name, L"PVR", 3) != 0 || _wcsicmp(model_name, kDreamAirModelKey) == 0) {
            continue;
        }
        if (edid_from_model(display, model_name, out)) {
            return true;
        }
    }
    return false;
}

bool read_edid_from_registry(std::vector<uint8_t>& out)
{
    out.clear();
    RegKey display;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SYSTEM\\CurrentControlSet\\Enum\\DISPLAY", 0, KEY_READ, &display.key) !=
        ERROR_SUCCESS) {
        return false;
    }
    return read_edid_from_display_key(display.key, out);
}

} // namespace nvdm
