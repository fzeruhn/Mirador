// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "display_session.hpp"

#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <iterator>
#include <memory>
#include <thread>
#include <utility>

#include "board.hpp"
#include "edid.hpp"

namespace nvdm {
namespace {

// The enumerate wait looks again this often.
constexpr int64_t kPollNs = 100'000'000;

bool same_luid(const LUID& a, const LUID& b)
{
    return a.LowPart == b.LowPart && a.HighPart == b.HighPart;
}

std::string luid_text(const LUID& luid)
{
    char text[32];
    snprintf(text, sizeof(text), "%08lx:%08lx", static_cast<unsigned long>(luid.HighPart),
             static_cast<unsigned long>(luid.LowPart));
    return text;
}

std::string hex(uint32_t value)
{
    char text[16];
    snprintf(text, sizeof(text), "0x%x", value);
    return text;
}

std::string tested_drivers_text()
{
    std::string text;
    for (const char* version : kTestedDrivers) {
        text += text.empty() ? "" : ", ";
        text += version;
    }
    return text;
}

} // namespace

bool is_tested_driver(const std::string& version)
{
    return std::any_of(std::begin(kTestedDrivers), std::end(kTestedDrivers),
                       [&](const char* tested) { return version == tested; });
}

const char* step_name(OpenStep step)
{
    switch (step) {
    case OpenStep::Holders: return "Holders";
    case OpenStep::LoadNvapi: return "LoadNvapi";
    case OpenStep::CheckIds: return "CheckIds";
    case OpenStep::Enumerate: return "Enumerate";
    case OpenStep::Luid: return "Luid";
    case OpenStep::Product: return "Product";
    case OpenStep::EnableDirectMode: return "EnableDirectMode";
    case OpenStep::D3D11: return "D3D11";
    case OpenStep::BindDevice: return "BindDevice";
    case OpenStep::CreateBackBuffers: return "CreateBackBuffers";
    case OpenStep::SetDisplayMode: return "SetDisplayMode";
    case OpenStep::Done: return "Done";
    }
    return "?";
}

DisplaySession::DisplaySession(SessionDeps deps) : deps_(std::move(deps))
{
    if (!deps_.sleep) {
        deps_.sleep = [](int64_t ns) { std::this_thread::sleep_for(std::chrono::nanoseconds(ns)); };
    }
    if (!deps_.processes) {
        deps_.processes = list_processes;
    }
    if (!deps_.pin_module) {
        deps_.pin_module = [] {};
    }
    if (!deps_.board_present) {
        deps_.board_present = board_present;
    }
    if (!deps_.log) {
        deps_.log = [](const std::string&) {};
    }
}

const BackBuffer& DisplaySession::back_buffer(uint32_t index) const
{
    return back_buffers_.at(index);
}

Mode DisplaySession::mode() const
{
    Mode m; // The version, bit depth and flags are the Dream Air's captured values.
    m.width = product_.width;
    m.height = product_.height;
    m.refresh_num = product_.refresh_mhz;
    m.refresh_den = 1000;
    return m;
}

void DisplaySession::log(const std::string& line) const
{
    deps_.log(line);
}

OpenResult DisplaySession::fail(OpenStep step, int code, const std::string& detail)
{
    std::string message = std::string(step_name(step)) + ": " + detail;
    if (code != 0) {
        message += " (code " + std::to_string(code) + ")";
    }
    log("error: " + message); // the layer logs it as an error (design 8)
    return OpenResult{false, step, code, std::move(message)};
}

OpenResult DisplaySession::open(const LUID& queried)
{
    if (!is_open()) {
        std::lock_guard<std::mutex> guard(open_mutex_);
        if (!is_open()) { // Not by another thread while this one waited.
            OpenResult result = run_steps(queried);
            if (!result.ok) {
                return result;
            }
        }
    }

    // Open, so the answer needs no Api call. A device on another adapter (an integrated GPU) is not the display's.
    if (!same_luid(queried, luid_)) {
        return fail(OpenStep::Luid, 0,
                    "the display is on adapter " + luid_text(luid_) + ", not the queried device's " +
                        luid_text(queried));
    }
    return OpenResult{true, OpenStep::Done, 0, done_message_};
}

OpenResult DisplaySession::run_steps(const LUID& queried)
{
    if (OpenResult r = check_holders(); !r.ok) {
        return r;
    }

    // From the first driver call on, the process holds state that must outlive a vkDestroyInstance: the loader
    // unloads layer DLLs there, and a session that failed half way is picked up again by the next instance. So pin the
    // DLL now, not only when the session is complete.
    if (!pinned_) {
        deps_.pin_module();
        pinned_ = true;
    }

    if (OpenResult r = load_nvapi(); !r.ok) {
        return r;
    }
    if (OpenResult r = check_ids(); !r.ok) {
        return r;
    }
    if (OpenResult r = find_display(); !r.ok) {
        return r;
    }
    if (OpenResult r = check_adapter(queried); !r.ok) {
        return r;
    }
    if (OpenResult r = identify_product(); !r.ok) {
        return r;
    }
    if (OpenResult r = enable_direct_mode(); !r.ok) {
        return r;
    }
    if (OpenResult r = make_device(); !r.ok) {
        return r;
    }
    if (OpenResult r = bind_device(); !r.ok) {
        return r;
    }
    if (OpenResult r = create_back_buffers(); !r.ok) {
        return r;
    }
    if (OpenResult r = set_display_mode(); !r.ok) {
        return r;
    }

    char refresh[32];
    snprintf(refresh, sizeof(refresh), "%u.%03u", product_.refresh_mhz / 1000, product_.refresh_mhz % 1000);
    done_message_ = std::string("display session open: ") + product_.name + ", " + std::to_string(product_.width) +
                    "x" + std::to_string(product_.height) + " at " + refresh + " Hz, display " + hex(display_id_) +
                    ", adapter " + luid_text(luid_);
    log(done_message_);
    open_.store(true, std::memory_order_release);
    return OpenResult{true, OpenStep::Done, 0, done_message_};
}

OpenResult DisplaySession::check_holders()
{
    if (deps_.ignore_holders) {
        return proceed(OpenStep::Holders);
    }
    const std::vector<std::string> held = running_holders(Held::Display, deps_.processes());
    if (held.empty()) {
        return proceed(OpenStep::Holders);
    }
    std::string names;
    for (const std::string& name : held) {
        names += names.empty() ? "" : ", ";
        names += name;
    }
    return fail(OpenStep::Holders, 0, "Pimax or SteamVR is running; stop it first (" + names + ")");
}

OpenResult DisplaySession::load_nvapi()
{
    if (nvapi_loaded_) {
        return proceed(OpenStep::LoadNvapi);
    }
    const int ret = deps_.api.initialize();
    if (ret != 0) {
        return fail(OpenStep::LoadNvapi, ret, "nvapi64.dll could not be loaded or initialised (not an NVIDIA driver?)");
    }
    nvapi_loaded_ = true;
    report_driver_version();
    return proceed(OpenStep::LoadNvapi);
}

void DisplaySession::report_driver_version()
{
    const std::string version = deps_.api.driver_version();
    if (version.empty()) {
        log("warning: the driver version could not be read; trying it anyway (tested: " + tested_drivers_text() + ")");
    } else if (is_tested_driver(version)) {
        log("driver version " + version);
    } else {
        log("warning: untested driver version " + version + "; trying it anyway (tested: " + tested_drivers_text() +
            ")");
    }
}

OpenResult DisplaySession::check_ids()
{
    if (ids_checked_) {
        return proceed(OpenStep::CheckIds);
    }
    if (const char* missing = deps_.api.first_missing_id()) {
        return fail(OpenStep::CheckIds, kNoEntry,
                    std::string("this driver has no NVAPI call '") + missing + "' (an unsupported driver version?)");
    }
    ids_checked_ = true;
    return proceed(OpenStep::CheckIds);
}

OpenResult DisplaySession::find_display()
{
    if (display_id_ != 0) {
        return proceed(OpenStep::Enumerate);
    }
    // The display only exists while the board is awake, which can take a few seconds after something starts sending
    // its heartbeat. Look every kPollNs until wait_ms has passed.
    const int64_t deadline = deps_.clock.now_ns() + static_cast<int64_t>(deps_.wait_ms) * 1'000'000;
    std::vector<uint32_t> ids;
    for (;;) {
        // Each direct-mode vendor in turn (Pimax first); the first with a display is the one.
        for (const DirectModeVendor& vendor : kDirectModeVendors) {
            const int ret = deps_.api.enumerate(vendor.nvapi_id, ids);
            if (ret != 0) {
                return fail(OpenStep::Enumerate, ret, "NVAPI could not list the direct-mode displays");
            }
            if (!ids.empty()) {
                vendor_ = &vendor;
                display_id_ = ids.front();
                display_count_ = ids.size();
                return proceed(OpenStep::Enumerate);
            }
        }
        const int64_t now = deps_.clock.now_ns();
        if (now >= deadline) {
            std::string vendors;
            for (const DirectModeVendor& vendor : kDirectModeVendors) {
                vendors += (vendors.empty() ? "" : ", ") + std::string(vendor.letters) + " (" + hex(vendor.nvapi_id) + ")";
            }
            return fail(OpenStep::Enumerate, 0,
                        "no direct-mode display of vendor " + vendors + " appeared within " +
                            std::to_string(deps_.wait_ms) + " ms (is the headset connected and its board awake?)");
        }
        deps_.sleep(std::min(kPollNs, deadline - now));
    }
}

OpenResult DisplaySession::check_adapter(const LUID& queried)
{
    if (!luid_known_) {
        LUID luid{};
        const int ret = deps_.api.adapter_luid(display_id_, luid);
        if (ret != 0) {
            return fail(OpenStep::Luid, ret, "NVAPI could not tell which adapter drives display " + hex(display_id_));
        }
        luid_ = luid;
        luid_known_ = true;
    }
    // Compared again at every attempt: a later query may come from another physical device.
    if (!same_luid(queried, luid_)) {
        return fail(OpenStep::Luid, 0,
                    "the display is on adapter " + luid_text(luid_) + ", not the queried device's " +
                        luid_text(queried));
    }
    return proceed(OpenStep::Luid);
}

OpenResult DisplaySession::identify_product()
{
    if (product_known_) {
        return proceed(OpenStep::Product);
    }
    // MIRADOR_DIRECT_MODE_TEST_NO_EDID (tests only): no EDID route is tried, so the last route below decides.
    if (deps_.skip_edid && !skip_edid_logged_) {
        log("warning: MIRADOR_DIRECT_MODE_TEST_NO_EDID is set: every EDID route is skipped (tests only)");
        skip_edid_logged_ = true;
    }
    std::vector<uint8_t> edid;
    EdidRoute route = deps_.skip_edid ? EdidRoute::None : deps_.api.read_edid(display_id_, edid);
    std::optional<Product> product;
    if (route == EdidRoute::None) {
        // The last route (design 6.3): no EDID, but the one PVR display is the Dream Air if its board is connected (the
        // display only appears while the board gets its heartbeat, design 7). Nothing is opened to find out.
        const std::string unread =
            deps_.skip_edid ? "the display's EDID was not read (MIRADOR_DIRECT_MODE_TEST_NO_EDID skips every route)"
                            : "the display's EDID could not be read (NVAPI, then the registry)";
        if (display_count_ != 1) {
            return fail(OpenStep::Product, kBadOutput,
                        unread + ", and with " + std::to_string(display_count_) +
                            " direct-mode displays of vendor PVR connected the Dream Air cannot be told apart");
        }
        if (!deps_.board_present()) {
            return fail(OpenStep::Product, kBadOutput,
                        unread + ", and the Dream Air's board (USB 34A4:0044) is not connected, so the display is not "
                                 "taken to be a Dream Air");
        }
        log("warning: " + unread + "; the one direct-mode display of vendor PVR is taken to be the " + kDreamAir.name +
            " because the Dream Air's board (USB 34A4:0044) is connected. A Pimax headset that is not a Dream Air "
            "would be given the Dream Air's mode: SetDisplayMode refuses it, or the headset stays black");
        product = kDreamAir;
        route = EdidRoute::Board;
    } else {
        product = find_product(edid);
        if (!product) {
            const std::optional<EdidIds> ids = parse_edid_ids(edid);
            const std::string who = ids ? pnp_letters(ids->pnp_vendor) + " " + hex(ids->pnp_product) : "(not an EDID)";
            return fail(OpenStep::Product, 0,
                        "unknown display product " + who + "; this layer knows " + kDreamAir.name + " (PVR " +
                            hex(kDreamAir.pnp_product) + "), and other headsets of a direct-mode vendor whose EDID "
                            "gives their mode");
        }
        if (!product->tested && route == EdidRoute::Registry) {
            // The registry keeps the EDID of every Pimax display Windows ever saw, and its route can't say which one
            // is connected: a mode from it could be another headset's. Only the tested table's product, as before.
            return fail(OpenStep::Product, kBadOutput,
                        std::string("the display's own EDID could not be read through NVAPI, and the one Windows kept "
                                    "in the registry names the ") +
                            product->name + ", which is not tested: its mode can't be trusted to be the connected "
                                            "headset's");
        }
        if (!product->tested) {
            // A headset Windows drives itself (its HMD route, WDDC) is left to it (Mirador.md section 4.11 item 52).
            // Never the tested Dream Air: its own EDID has no such block, and an override that adds one doesn't hand
            // it to Windows.
            if (has_microsoft_hmd_block(edid)) {
                return fail(OpenStep::Product, 0,
                            std::string("the ") + product->name + "'s EDID carries Microsoft's HMD block, so Windows "
                                "drives it through its own HMD route and this layer leaves it alone");
            }
            log(std::string("warning: the ") + product->name + " is not tested with this layer: it gets the mode its "
                "EDID asks for, " + std::to_string(product->width) + "x" + std::to_string(product->height) + " at " +
                std::to_string(product->refresh_mhz / 1000) + "." + std::to_string(product->refresh_mhz % 1000 / 100) +
                " Hz. If it stays black, report the layer's log");
        }
    }
    log(std::string("EDID route: ") + edid_route_name(route));
    product_ = *product;
    edid_route_ = route;
    product_known_ = true;
    return proceed(OpenStep::Product);
}

OpenResult DisplaySession::enable_direct_mode()
{
    if (direct_mode_enabled_) {
        return proceed(OpenStep::EnableDirectMode);
    }
    const int ret = deps_.api.enable_direct_mode(vendor_ ? vendor_->nvapi_id : kVendorPvr);
    if (ret != 0) {
        return fail(OpenStep::EnableDirectMode, ret,
                    std::string("NVAPI refused direct mode for vendor ") + (vendor_ ? vendor_->letters : "PVR"));
    }
    direct_mode_enabled_ = true;
    return proceed(OpenStep::EnableDirectMode);
}

OpenResult DisplaySession::make_device()
{
    if (device_ != nullptr) {
        return proceed(OpenStep::D3D11);
    }
    IUnknown* device = deps_.make_d3d11 ? deps_.make_d3d11(luid_) : nullptr;
    if (device == nullptr) {
        return fail(OpenStep::D3D11, 0, "no D3D11 device could be created on the display's adapter");
    }
    device_ = device;
    return proceed(OpenStep::D3D11);
}

OpenResult DisplaySession::bind_device()
{
    if (bound_) {
        return proceed(OpenStep::BindDevice);
    }
    const int ret = deps_.api.bind_device(display_id_, device_, vendor_ ? vendor_->nvapi_id : kVendorPvr);
    if (ret != 0) {
        return fail(OpenStep::BindDevice, ret, "NVAPI would not bind the display (is another program using it?)");
    }
    bound_ = true;
    return proceed(OpenStep::BindDevice);
}

OpenResult DisplaySession::create_back_buffers()
{
    // One at a time, keeping the ones made: a failure at the third is retried as the third.
    while (back_buffers_.size() < kBackBufferCount) {
        BackBuffer buffer{};
        const int ret = deps_.api.create_back_buffer(mode(), buffer);
        if (ret != 0) {
            return fail(OpenStep::CreateBackBuffers, ret,
                        "back-buffer " + std::to_string(back_buffers_.size() + 1) + " of " +
                            std::to_string(kBackBufferCount) + " could not be created");
        }
        back_buffers_.push_back(buffer);
    }
    return proceed(OpenStep::CreateBackBuffers);
}

OpenResult DisplaySession::set_display_mode()
{
    const int ret = deps_.api.set_display_mode(mode());
    if (ret != 0 && ret != kSetModeOk) { // -208 is how it says OK.
        return fail(OpenStep::SetDisplayMode, ret, "NVAPI would not set the display mode");
    }
    return proceed(OpenStep::SetDisplayMode);
}

// --- Settings and the process-wide session -----------------------------------------------------------------------

SessionSettings parse_session_settings(const char* wait_ms, const char* ignore_holders, const char* no_edid)
{
    SessionSettings settings;
    // Digits only: strtoull would take a sign or leading blanks, and wrap "-5" to a huge number.
    if (wait_ms != nullptr && wait_ms[0] >= '0' && wait_ms[0] <= '9') {
        char* end = nullptr;
        errno = 0;
        const unsigned long long value = strtoull(wait_ms, &end, 10);
        if (errno == 0 && *end == '\0' && value <= UINT32_MAX) {
            settings.wait_ms = static_cast<uint32_t>(value);
        }
    }
    settings.ignore_holders = ignore_holders != nullptr && strcmp(ignore_holders, "1") == 0;
    settings.skip_edid = no_edid != nullptr && strcmp(no_edid, "1") == 0;
    return settings;
}

namespace {

// Nothing here is destroyed at process exit (design 6.10: no destructors that could wait on a killed thread's lock).
std::mutex& sink_mutex()
{
    static std::mutex* const mutex = new std::mutex;
    return *mutex;
}

std::function<void(const std::string&)>& sink()
{
    static auto* const function = new std::function<void(const std::string&)>;
    return *function;
}

void process_log(const std::string& line)
{
    std::function<void(const std::string&)> target;
    {
        std::lock_guard<std::mutex> guard(sink_mutex());
        target = sink();
    }
    if (target) {
        target(line);
        return;
    }
    const std::string text = "[nvdm] " + line + "\n";
    fputs(text.c_str(), stderr);
    OutputDebugStringA(text.c_str());
}

void pin_this_module()
{
    // An address inside this module, so FROM_ADDRESS finds the DLL that holds this code (the layer, when it is the
    // layer that links nvdm), and PIN keeps it loaded until the process exits.
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            reinterpret_cast<LPCWSTR>(&pin_this_module), &module)) {
        process_log("warning: could not pin the layer module (error " + std::to_string(GetLastError()) + ")");
    }
}

// Port of nvdm_create_d3d11 (comp_window_nvapi_direct.c): the DXGI adapter with this LUID, and a D3D11 device on it at
// feature level 11_1. The device is returned with its one reference and never released: the NVAPI binding and the
// back-buffers need it for the life of the process.
IUnknown* create_d3d11_on(const LUID& luid)
{
    using Microsoft::WRL::ComPtr;

    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) {
        char text[64];
        snprintf(text, sizeof(text), "CreateDXGIFactory1: 0x%08lx", static_cast<unsigned long>(hr));
        process_log(text);
        return nullptr;
    }

    ComPtr<IDXGIAdapter1> adapter;
    for (UINT i = 0;; i++) {
        ComPtr<IDXGIAdapter1> candidate;
        if (factory->EnumAdapters1(i, &candidate) != S_OK) {
            break;
        }
        DXGI_ADAPTER_DESC1 desc{};
        candidate->GetDesc1(&desc);
        if (same_luid(desc.AdapterLuid, luid)) {
            adapter = candidate;
            break;
        }
    }
    if (!adapter) {
        process_log("no DXGI adapter with LUID " + luid_text(luid));
        return nullptr;
    }

    const D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_1;
    ComPtr<ID3D11Device> device;
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, &level, 1, D3D11_SDK_VERSION, &device,
                           nullptr, nullptr);
    if (FAILED(hr)) {
        char text[64];
        snprintf(text, sizeof(text), "D3D11CreateDevice: 0x%08lx", static_cast<unsigned long>(hr));
        process_log(text);
        return nullptr;
    }
    return device.Detach(); // The ID3D11Device* itself, upcast: what Api::bind_device wants.
}

std::string environment_one(const char* name)
{
    char value[64];
    const DWORD length = GetEnvironmentVariableA(name, value, static_cast<DWORD>(sizeof(value)));
    return length > 0 && length < sizeof(value) ? std::string(value, length) : std::string();
}

// MIRADOR_DIRECT_MODE_<x>, else (for one release) its Dream Air name DREAMAIR_VK_LAYER_<x>.
std::string environment(const char* name)
{
    std::string value = environment_one(name);
    constexpr const char* kNew = "MIRADOR_DIRECT_MODE";
    if (value.empty() && std::strncmp(name, kNew, std::strlen(kNew)) == 0) {
        value = environment_one(("DREAMAIR_VK_LAYER" + std::string(name + std::strlen(kNew))).c_str());
    }
    return value;
}

class ProcessSession {
public:
    ProcessSession() : api_(make_nvapi()), session_(make_deps()) {}
    DisplaySession& session() { return session_; }

private:
    SessionDeps make_deps()
    {
        const std::string wait_ms = environment("MIRADOR_DIRECT_MODE_WAIT_MS");
        const std::string ignore_holders = environment("MIRADOR_DIRECT_MODE_IGNORE_HOLDERS");
        const std::string no_edid = environment("MIRADOR_DIRECT_MODE_TEST_NO_EDID");
        const SessionSettings settings = parse_session_settings(wait_ms.c_str(), ignore_holders.c_str(), no_edid.c_str());

        SessionDeps deps{*api_, clock_};
        deps.make_d3d11 = create_d3d11_on;
        deps.processes = list_processes;
        deps.pin_module = pin_this_module;
        deps.board_present = board_present;
        deps.ignore_holders = settings.ignore_holders;
        deps.skip_edid = settings.skip_edid;
        deps.wait_ms = settings.wait_ms;
        deps.sleep = [](int64_t ns) {
            if (ns > 0) {
                Sleep(static_cast<DWORD>((ns + 999'999) / 1'000'000));
            }
        };
        deps.log = process_log;
        return deps;
    }

    std::unique_ptr<Api> api_;
    QpcClock clock_;
    DisplaySession session_; // Declared last: it holds references to the two above.
};

} // namespace

void set_process_session_log(std::function<void(const std::string&)> function)
{
    std::lock_guard<std::mutex> guard(sink_mutex());
    sink() = std::move(function);
}

DisplaySession& process_session()
{
    // Built once, thread-safely, and never destroyed.
    static ProcessSession* const instance = new ProcessSession;
    return instance->session();
}

} // namespace nvdm
