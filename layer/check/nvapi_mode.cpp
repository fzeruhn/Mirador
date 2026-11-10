// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The default mode of mirador-display-check (design 7): NVAPI only. The display session opens exactly as it does in
// the layer; then this file plays an application that presents one solid colour per frame, through the same frame
// model, display loop and display thread the layer runs, and counts the display's flips. There is no Vulkan here, so
// the frame fences are always signalled and the back-buffers are cleared through a D3D11 device of our own.

#include <windows.h>

#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "check/modes.hpp"
#include "core/display_loop.hpp"
#include "core/display_session.hpp"
#include "core/display_thread.hpp"
#include "core/fence_pool.hpp"
#include "core/frame_model.hpp"
#include "core/stats.hpp"

namespace dvl {
namespace {

using Microsoft::WRL::ComPtr;

constexpr int64_t kMs = 1'000'000;
constexpr int64_t kPresentWaitNs = 500 * kMs; // a present is credited within a refresh; this is generous
constexpr int64_t kAcquireGiveUpNs = 2'000'000'000;
constexpr int64_t kCloseStopWaitNs = 100 * kMs; // the console handler's wait (design 6.10, constraints)

// Lines from the display thread, the model and the session, printed by the main loop (never from under the
// display-session lock: a console write can take milliseconds).
class LineQueue {
public:
    void add(const std::string& line)
    {
        std::lock_guard<std::mutex> hold(mutex_);
        lines_.push_back(line);
    }
    void drain(Output& out)
    {
        std::vector<std::string> lines;
        {
            std::lock_guard<std::mutex> hold(mutex_);
            lines.swap(lines_);
        }
        for (const std::string& line : lines) {
            out.line(line);
        }
    }

private:
    std::mutex mutex_;
    std::vector<std::string> lines_;
};

// No Vulkan: a frame never has a fence to wait for (queue() is given kNoFence), and if the loop asks anyway the answer
// is that it has signalled.
class AlwaysSignalled final : public nvdm::FenceOps {
public:
    bool wait(nvdm::FenceSlot, int64_t) override { return true; }
    bool status(nvdm::FenceSlot) override { return true; }
};

// The DXGI adapters of NVIDIA, as LUIDs, in enumeration order: the display session finds out which one drives the
// headset (a laptop has an integrated GPU too).
std::vector<LUID> nvidia_adapter_luids()
{
    std::vector<LUID> luids;
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
        return luids;
    }
    for (UINT i = 0;; i++) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(i, &adapter) != S_OK) {
            break;
        }
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        if (desc.VendorId == 0x10de) {
            luids.push_back(desc.AdapterLuid);
        }
    }
    return luids;
}

// Clears the back-buffers: a D3D11 device of our own on the display's adapter opens the three shared handles NVAPI made
// (as nvdm_probe did on the bound device) and clears one at a time, waiting until the GPU has finished, so the Present
// that follows shows the colour.
class Clearer {
public:
    bool create(const LUID& luid, const std::vector<HANDLE>& shared, std::string& why)
    {
        ComPtr<IDXGIFactory1> factory;
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
            why = "CreateDXGIFactory1 failed";
            return false;
        }
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT i = 0;; i++) {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory->EnumAdapters1(i, &candidate) != S_OK) {
                break;
            }
            DXGI_ADAPTER_DESC1 desc{};
            candidate->GetDesc1(&desc);
            if (desc.AdapterLuid.LowPart == luid.LowPart && desc.AdapterLuid.HighPart == luid.HighPart) {
                adapter = candidate;
                break;
            }
        }
        if (!adapter) {
            why = "no DXGI adapter with the display's LUID";
            return false;
        }
        const D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_1;
        HRESULT hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, &level, 1, D3D11_SDK_VERSION,
                                       &device_, nullptr, &context_);
        if (FAILED(hr)) {
            why = hresult_text("D3D11CreateDevice", hr);
            return false;
        }
        for (HANDLE handle : shared) {
            ComPtr<ID3D11Texture2D> texture;
            hr = device_->OpenSharedResource(handle, IID_PPV_ARGS(&texture));
            if (FAILED(hr)) {
                why = hresult_text("OpenSharedResource", hr);
                return false;
            }
            // The back-buffer is R8G8B8A8_TYPELESS, so the view needs a typed format.
            D3D11_RENDER_TARGET_VIEW_DESC view{};
            view.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            view.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
            ComPtr<ID3D11RenderTargetView> rtv;
            hr = device_->CreateRenderTargetView(texture.Get(), &view, &rtv);
            if (FAILED(hr)) {
                why = hresult_text("CreateRenderTargetView", hr);
                return false;
            }
            views_.push_back(rtv);
        }
        D3D11_QUERY_DESC query{D3D11_QUERY_EVENT, 0};
        hr = device_->CreateQuery(&query, &done_);
        if (FAILED(hr)) {
            why = hresult_text("CreateQuery", hr);
            return false;
        }
        return true;
    }

    // False if the clear did not finish within half a second (the frame is presented anyway).
    bool clear(uint32_t index, const float colour[4])
    {
        if (index >= views_.size()) {
            return false;
        }
        context_->ClearRenderTargetView(views_[index].Get(), colour);
        context_->End(done_.Get());
        context_->Flush();
        const auto give_up = std::chrono::steady_clock::now() + std::chrono::milliseconds(500);
        while (context_->GetData(done_.Get(), nullptr, 0, 0) == S_FALSE) {
            if (std::chrono::steady_clock::now() > give_up) {
                return false;
            }
            YieldProcessor();
        }
        return true;
    }

private:
    static std::string hresult_text(const char* what, HRESULT hr)
    {
        char text[96];
        std::snprintf(text, sizeof(text), "%s: 0x%08lx", what, static_cast<unsigned long>(hr));
        return text;
    }

    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<ID3D11Query> done_;
    std::vector<ComPtr<ID3D11RenderTargetView>> views_;
};

// Everything the display thread touches. Heap-allocated and left alone if the thread cannot be joined (it may still be
// inside an NVAPI call), so nothing it uses is ever destroyed under it.
struct Rig {
    Rig(nvdm::DisplaySession& s, int64_t refresh_ns)
        : session(s), clock(s.clock()), lock(s.lock()), model(model_config(refresh_ns), &pool), waiter(clock),
          thread([this](const std::string& line) { lines.add("  display thread: " + line); })
    {
        model.set_log([this](const std::string& line) { lines.add("  model: " + line); });
    }

    static nvdm::FrameModelConfig model_config(int64_t refresh_ns)
    {
        nvdm::FrameModelConfig config;
        config.refresh_ns = refresh_ns;
        return config;
    }

    nvdm::DisplaySession& session;
    nvdm::Clock& clock;
    std::mutex& lock;
    nvdm::FencePool pool;
    nvdm::FrameModel model;
    nvdm::StatsWindow stats;
    nvdm::Win32Waiter waiter;
    AlwaysSignalled fences;
    LineQueue lines;
    std::condition_variable changed; // notified under the lock at every counter increment
    nvdm::DisplayThread thread;
};

nvdm::FrameModel::Counters counters_of(Rig& rig)
{
    std::lock_guard<std::mutex> hold(rig.lock);
    return rig.model.counters();
}

// The oldest free back-buffer for a new frame (FrameModel::acquire), waiting for the display thread's credits to free
// one. The model never returns nothing for long; the bound here is only so that a broken display cannot hang the tool.
std::optional<nvdm::Acquired> acquire_frame(Rig& rig, nvdm::SwapchainGen gen)
{
    std::unique_lock<std::mutex> hold(rig.lock);
    const int64_t give_up = rig.clock.now_ns() + kAcquireGiveUpNs;
    for (;;) {
        if (std::optional<nvdm::Acquired> frame = rig.model.acquire(gen, rig.clock.now_ns())) {
            return frame;
        }
        if (rig.clock.now_ns() > give_up || interrupted()) {
            return std::nullopt;
        }
        rig.changed.wait_for(hold, std::chrono::milliseconds(2));
    }
}

// vkWaitForPresentKHR: until the display thread has credited the frame (its back-buffer is on screen).
bool wait_present(Rig& rig, nvdm::SwapchainGen gen, uint64_t id)
{
    std::unique_lock<std::mutex> hold(rig.lock);
    const int64_t give_up = rig.clock.now_ns() + kPresentWaitNs;
    while (rig.model.completed_present_id(gen) < id) {
        const int64_t now = rig.clock.now_ns();
        if (now >= give_up || interrupted()) {
            return false;
        }
        rig.changed.wait_for(hold, std::chrono::nanoseconds(std::min<int64_t>(give_up - now, 5 * kMs)));
    }
    return true;
}

const float kColours[4][4] = {{1, 0, 0, 1}, {0, 1, 0, 1}, {0, 0, 1, 1}, {1, 1, 1, 1}};

} // namespace

ModeResult run_nvapi_check(const NvapiEnv& env, const Options& options, Output& out)
{
    ModeResult result;
    result.counts.requested_seconds = options.seconds;
    nvdm::DisplaySession& session = *env.session;

    // 1. The display session, on whichever NVIDIA adapter drives the headset.
    say(out, "opening the display session (NVAPI, EnableDirectMode, BindDevice; waits for the display to appear)");
    nvdm::OpenResult opened{false, nvdm::OpenStep::Luid, 0, "no NVIDIA adapter found"};
    if (env.adapters.empty()) {
        opened = session.open(LUID{}); // fails at Luid and names the display's adapter
    }
    for (const LUID& luid : env.adapters) {
        opened = session.open(luid);
        // Only "this adapter does not drive the display" goes on to the next adapter; any other failure is the answer.
        if (opened.ok || opened.step != nvdm::OpenStep::Luid || opened.code != 0) {
            break;
        }
    }
    say(out, "display session steps:");
    for (const std::string& line : describe_open_result(opened)) {
        say(out, "  %s", line.c_str());
    }
    if (!opened.ok) {
        for (const std::string& line : explain_failure(opened.message)) {
            say(out, "  %s", line.c_str());
        }
    }
    const std::string driver = env.session_lines ? driver_version_from_lines(env.session_lines()) : std::string();
    say(out, "NVIDIA driver version: %s", driver.empty() ? "unknown" : driver.c_str());
    if (!opened.ok) {
        result.steps_ok = false;
        result.failed_step = opened.message;
        return result;
    }

    const nvdm::Product& product = session.product();
    say(out, "every private NVAPI id resolves; display: %s, %ux%u at %u.%03u Hz", product.name, product.width,
        product.height, product.refresh_mhz / 1000, product.refresh_mhz % 1000);
    say(out, "EDID route: %s", nvdm::edid_route_name(session.edid_route()));
    const int64_t refresh_ns = product.refresh_mhz != 0 ? 1'000'000'000'000LL / product.refresh_mhz : 11'111'111;

    // 2. A way to put colours into the back-buffers.
    std::vector<HANDLE> shared;
    for (uint32_t i = 0; i < nvdm::kBackBufferCount; i++) {
        shared.push_back(session.back_buffer(i).shared);
    }
    Clearer clearer;
    std::string why = "not asked for";
    const bool can_clear = env.clear_back_buffers && clearer.create(session.luid(), shared, why);
    if (can_clear) {
        say(out, "back-buffers: cleared through a D3D11 device on the display's adapter, a new colour every second");
    } else if (env.clear_back_buffers) {
        say(out, "warning: the back-buffers cannot be cleared (%s); the flips are counted all the same, the picture is "
                 "whatever they hold", why.c_str());
    }

    // 3. The layer's own presenting parts, driven by this thread as an application would.
    auto rig = std::make_unique<Rig>(session, refresh_ns);
    Rig* const r = rig.get();
    nvdm::DisplayLoopConfig config;
    for (uint32_t i = 0; i < nvdm::kBackBufferCount; i++) {
        config.tags.push_back(session.back_buffer(i).tag);
    }
    config.refresh_ns = refresh_ns;
    config.log = [r](const std::string& line) { r->lines.add("  stats: " + line); };
    config.refresh_seen = [r](int64_t) { r->changed.notify_all(); };
    r->thread.start([r, config] {
        return std::make_unique<nvdm::DisplayLoop>(r->session.api(), r->model, r->lock, r->clock, r->waiter, r->fences,
                                                   r->stats, config);
    });
    // Closing the console ends the process once the console handler returns: the handler stops the thread first,
    // waiting up to 100 ms for it to leave NVAPI (Task 12 review). The hook is replaced before the rig can go (it is
    // declared after `rig`, so it is destroyed first), and replacing it waits for a run in progress.
    set_interrupt_stop_hook([r] { r->thread.stop_and_join(kCloseStopWaitNs); });
    struct ClearStopHook {
        ~ClearStopHook() { set_interrupt_stop_hook({}); }
    } clear_stop_hook;
    {
        // The first pass reads the counter and starts the epoch (both clocks set to now): give it a moment.
        const int64_t give_up = r->clock.now_ns() + 2'000'000'000;
        for (;;) {
            {
                std::lock_guard<std::mutex> hold(r->lock);
                if (r->model.last_refresh_ns() != 0) {
                    break;
                }
            }
            if (r->clock.now_ns() > give_up) {
                break;
            }
            Sleep(1);
        }
    }

    nvdm::SwapchainGen gen = 0;
    {
        std::lock_guard<std::mutex> hold(r->lock);
        gen = r->model.add_swapchain();
    }
    const double refresh_hz = product.refresh_mhz != 0 ? product.refresh_mhz / 1000.0 : kRefreshHz;
    result.counts.refresh_hz = refresh_hz;
    say(out, "presenting for %d s (%llu presents at %.0f Hz expected)", options.seconds,
        static_cast<unsigned long long>(expected_flips(options.seconds, refresh_hz)), refresh_hz);

    const nvdm::FrameModel::Counters before = counters_of(*r);
    const int64_t t0 = r->clock.now_ns();
    const int64_t deadline = t0 + static_cast<int64_t>(options.seconds) * 1'000'000'000;
    int64_t next_progress = t0 + 5'000'000'000;
    uint64_t id = 0;
    uint64_t wait_timeouts = 0;
    uint64_t clear_timeouts = 0;
    while (r->clock.now_ns() < deadline && !interrupted()) {
        const std::optional<nvdm::Acquired> frame = acquire_frame(*r, gen);
        if (!frame) {
            if (!interrupted()) {
                result.steps_ok = false;
                result.failed_step = "acquire: no back-buffer became free within 2 s (the display is not flipping)";
            }
            break;
        }
        if (can_clear) {
            const int64_t second = (r->clock.now_ns() - t0) / 1'000'000'000;
            if (!clearer.clear(frame->buffer, kColours[second % 4])) {
                clear_timeouts++;
            }
        }
        id++;
        {
            std::lock_guard<std::mutex> hold(r->lock);
            // Its GPU work is done: cleared, waited for.
            r->model.queue(frame->seq, id, nvdm::kNoFence, r->clock.now_ns());
        }
        r->waiter.signal_frame_queued(); // after queue(), outside the lock, as the layer's present does
        if (!wait_present(*r, gen, id)) {
            wait_timeouts++;
        }
        r->lines.drain(out);
        // A dead display completes every present at once (design 6.7 step 5): the loop would run at the speed of the
        // clears for the rest of the run. The mode's rate cannot do 3 times its count.
        if (id > 3 * expected_flips(options.seconds, refresh_hz) + 100) {
            result.steps_ok = false;
            result.failed_step = "presenting " + std::to_string(id) +
                                 " frames far above the mode's rate: the display is not flipping (presents complete at once "
                                 "while flips are stopped)";
            break;
        }
        if (r->clock.now_ns() >= next_progress) {
            next_progress += 5'000'000'000;
            const nvdm::FrameModel::Counters now = counters_of(*r);
            say(out, "  %.0f s: %llu presents, %llu flips", static_cast<double>(r->clock.now_ns() - t0) / 1e9,
                static_cast<unsigned long long>(now.presents - before.presents),
                static_cast<unsigned long long>(now.refreshes - before.refreshes));
        }
    }
    const int64_t t1 = r->clock.now_ns();
    const nvdm::FrameModel::Counters after = counters_of(*r);
    {
        std::lock_guard<std::mutex> hold(r->lock);
        r->model.remove_swapchain(gen);
    }

    // 4. Stop the display thread. One that cannot leave NVAPI in a second is left behind with everything it uses.
    const bool joined = r->thread.stop_and_join(nvdm::kJoinTimeoutNs);
    r->lines.drain(out);
    if (!joined) {
        say(out, "warning: the display thread did not leave NVAPI within 1 s and is left behind");
        rig.release();
    }

    result.counts.presents = after.presents - before.presents;
    result.counts.flips = after.refreshes - before.refreshes;
    result.counts.failures = (after.failed - before.failed) + (after.unconfirmed - before.unconfirmed) + wait_timeouts;
    result.counts.link_drops = after.link_drops - before.link_drops;
    result.counts.seconds = static_cast<double>(t1 - t0) / 1e9;

    say(out, "frames: %llu presents, %llu flips, %llu missed vblanks, %llu link drops; dropped %llu, failed %llu, "
             "unconfirmed %llu, back-buffers reserved %llu; %llu present-wait timeouts, %llu clears that did not finish",
        static_cast<unsigned long long>(result.counts.presents), static_cast<unsigned long long>(result.counts.flips),
        static_cast<unsigned long long>(after.missed - before.missed),
        static_cast<unsigned long long>(result.counts.link_drops),
        static_cast<unsigned long long>(after.dropped - before.dropped),
        static_cast<unsigned long long>(after.failed - before.failed),
        static_cast<unsigned long long>(after.unconfirmed - before.unconfirmed),
        static_cast<unsigned long long>(after.reserved - before.reserved), static_cast<unsigned long long>(wait_timeouts),
        static_cast<unsigned long long>(clear_timeouts));

    if (interrupted() && result.steps_ok) {
        result.steps_ok = false;
        result.failed_step = "interrupted";
    }
    return result;
}

ModeResult run_nvapi_mode(const Options& options, Output& out)
{
    // The session's own lines (driver version, EDID route, the failing step) go to the console as they come.
    std::mutex session_mutex;
    std::vector<std::string> session_lines;
    nvdm::set_process_session_log([&](const std::string& line) {
        {
            std::lock_guard<std::mutex> hold(session_mutex);
            session_lines.push_back(line);
        }
        out.line("  session: " + line);
    });
    struct ResetSink {
        ~ResetSink() { nvdm::set_process_session_log({}); }
    } reset_sink;

    NvapiEnv env;
    env.session = &nvdm::process_session(); // the real one: it talks to the driver at its first open()
    env.adapters = nvidia_adapter_luids();
    env.clear_back_buffers = true;
    env.session_lines = [&] {
        std::lock_guard<std::mutex> hold(session_mutex);
        return session_lines;
    };
    return run_nvapi_check(env, options, out);
}

} // namespace dvl
