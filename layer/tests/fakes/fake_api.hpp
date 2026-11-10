// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <algorithm>
#include <cstdint>
#include <deque>
#include <functional>
#include <initializer_list>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "core/api.hpp"
#include "fake_clock.hpp"

namespace nvdm {

// A scripted nvdm::Api for tests. By default it is a happy Dream Air: one display, an EDID that names it, a LUID, three
// back-buffers, SetDisplayMode answering -208, and a flip counter that follows a FakeClock.
//
//  * Every call is recorded, in order, with its fake time and main argument (calls(), names(), count()).
//  * Each call returns its next queued status (queue()), else its default (set_default_status(), else 0, and
//    kSetModeOk for set_display_mode). A non-zero status leaves the call's outputs empty or untouched, and
//    read_edid() EdidRoute::None.
//  * What the calls report is set through the public members below, before the code under test runs.
//  * The refresh counter is a function of the clock: it is 0 when the fake is made and goes up by one every
//    refresh_ns() of fake time, whatever is presented. stop_counter(), start_counter() and reset_counter() change that.
//  * Like the real port, create_back_buffer(), set_display_mode(), present() and present_stats() return kNotBound
//    before a successful bind_device(), but only when strict_bind is set (a test of one call alone need not bind
//    first).
//    bind_device() refuses a NULL device with kBadArgument, strict or not.
//
// The call log and the counter are guarded, so the code under test may call from its own thread while the test thread
// reads them. The public members are not: set them only while no call is in flight.
class FakeApi : public Api {
public:
    // 1/90 s, as the Dream Air refreshes.
    static constexpr int64_t kDefaultRefreshNs = 11'111'111;

    // The calls of Api, for queue() and set_default_status(). A status of 0 means success for all of them, read_edid()
    // too (it then answers edid_route); first_missing_id() and driver_version() have no status.
    enum class Fn {
        Initialize,
        FirstMissingId,
        DriverVersion,
        Enumerate,
        AdapterLuid,
        ReadEdid,
        EnableDirectMode,
        BindDevice,
        CreateBackBuffer,
        SetDisplayMode,
        Present,
        PresentStats,
    };

    // One entry of the call log. name is the Api method's name ("enable_direct_mode"), or a note() text. arg is the
    // call's main argument: the vendor for enumerate and enable_direct_mode, the display id for adapter_luid,
    // read_edid and bind_device, the tag for present; 0 otherwise. at_ns is the clock's time at entry.
    struct Call {
        std::string name;
        int64_t at_ns = 0;
        uint64_t arg = 0;
        int status = 0;
    };

    explicit FakeApi(FakeClock& clock, int64_t refresh_ns = kDefaultRefreshNs)
        : clock_(clock), refresh_ns_(refresh_ns), base_ns_(clock.now_ns())
    {
        for (uint32_t i = 0; i < kBackBufferCount; i++) {
            back_buffers.push_back(BackBuffer{0x100 + i, reinterpret_cast<HANDLE>(static_cast<uintptr_t>(0x1000 + i))});
        }
        edid = make_edid(0x42d2, 0x1019);
    }

    // --- What the fake reports ---------------------------------------------------------------------------------

    std::string driver = "621.01";               // driver_version()
    std::string missing_id;                      // first_missing_id(); empty means none is missing
    std::vector<uint32_t> display_ids{0x80061086}; // enumerate(kVendorPvr); the id Finn's machine has
    int64_t appears_at_ns = 0;                   // enumerate() finds nothing before this clock time
    LUID luid = {0x1234, 0x56};                  // adapter_luid()
    std::vector<uint8_t> edid;                   // read_edid(); empty means unreadable
    EdidRoute edid_route = EdidRoute::Nvapi;     // What read_edid() reports as the route when it succeeds
    bool strict_bind = false;                    // See the class comment: kNotBound before a successful bind_device()
    std::vector<BackBuffer> back_buffers;        // create_back_buffer() hands these out in order, then fails
    uint32_t refresh_num = 90000;                // present_stats(); 0 for both models a down link
    uint32_t refresh_den = 1000;
    int64_t qpc_offset_ns = 0;                   // present_stats() puts its refresh time this far ahead (bogus)

    // Called at the entry of every Api call with its name, before the call is recorded and without any fake lock held,
    // so it may block (a Present that does not return), assert, or call back into the fake.
    std::function<void(const char* name)> on_call;

    // --- What the fake was given -------------------------------------------------------------------------------

    IUnknown* bound_device = nullptr;  // The d3d11 of the last bind_device()
    std::vector<Mode> modes_created;   // The mode of every create_back_buffer()
    std::vector<Mode> modes_set;       // The mode of every set_display_mode()

    // --- Scripting -----------------------------------------------------------------------------------------------

    // The next `times` calls of fn return this status, then the queue is empty again.
    void queue(Fn fn, int status, size_t times = 1)
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (size_t i = 0; i < times; i++) {
            queued_[fn].push_back(status);
        }
    }

    // What fn returns whenever nothing is queued for it.
    void set_default_status(Fn fn, int status)
    {
        std::lock_guard<std::mutex> lock(mutex_);
        defaults_[fn] = status;
    }

    // create_back_buffer() starts again from the first back-buffer.
    void reset_back_buffers()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        handed_out_ = 0;
    }

    // Freezes the counter at its value now. The time it reports stays that of its last flip.
    void stop_counter()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        const Reading now = reading();
        base_count_ = now.counter;
        base_ns_ = now.qpc_ns;
        running_ = false;
    }

    // Counts again from the value it has. It counts as flipped at this moment: that is the time it reports until the
    // next flip, a period later.
    void start_counter()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!running_) {
            running_ = true;
            base_ns_ = clock_.now_ns();
        }
    }

    // A link drop: the counter is 0 at this moment and counts on from there (or stays 0 while stopped).
    void reset_counter()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        base_count_ = 0;
        base_ns_ = clock_.now_ns();
    }

    int64_t refresh_ns() const { return refresh_ns_; }

    // --- The call log --------------------------------------------------------------------------------------------

    std::vector<Call> calls() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return calls_;
    }

    // The names of the logged calls in order, leaving out the names in `except`.
    std::vector<std::string> names(std::initializer_list<std::string> except = {}) const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        std::vector<std::string> out;
        for (const Call& call : calls_) {
            bool skip = false;
            for (const std::string& name : except) {
                skip = skip || name == call.name;
            }
            if (!skip) {
                out.push_back(call.name);
            }
        }
        return out;
    }

    size_t count(const std::string& name) const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        size_t n = 0;
        for (const Call& call : calls_) {
            n += call.name == name ? 1 : 0;
        }
        return n;
    }

    // Puts a line into the call log between Api calls, for what a test fakes elsewhere ("make_d3d11").
    void note(const std::string& name)
    {
        std::lock_guard<std::mutex> lock(mutex_);
        calls_.push_back(Call{name, clock_.now_ns(), 0, 0});
    }

    // A 128-byte EDID base block of a monitor: the manufacturer id as the 16 bits the standard stores big-endian
    // (0x42d2 is "PVR"), the product code (stored little-endian), version 1.4 and a good checksum.
    static std::vector<uint8_t> make_edid(uint16_t pnp_vendor, uint16_t product)
    {
        std::vector<uint8_t> block(128, 0);
        const uint8_t header[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
        for (size_t i = 0; i < 8; i++) {
            block[i] = header[i];
        }
        block[8] = static_cast<uint8_t>(pnp_vendor >> 8);
        block[9] = static_cast<uint8_t>(pnp_vendor & 0xff);
        block[10] = static_cast<uint8_t>(product & 0xff);
        block[11] = static_cast<uint8_t>(product >> 8);
        block[18] = 1;
        block[19] = 4;
        unsigned sum = 0;
        for (size_t i = 0; i < 127; i++) {
            sum += block[i];
        }
        block[127] = static_cast<uint8_t>((256 - sum % 256) % 256);
        return block;
    }

    // --- Api -----------------------------------------------------------------------------------------------------

    int initialize() override
    {
        Scope call(*this, Fn::Initialize, 0);
        return call.status;
    }

    const char* first_missing_id() override
    {
        Scope call(*this, Fn::FirstMissingId, 0);
        return missing_id.empty() ? nullptr : missing_id.c_str();
    }

    std::string driver_version() override
    {
        Scope call(*this, Fn::DriverVersion, 0);
        return driver;
    }

    int enumerate(uint32_t vendor, std::vector<uint32_t>& ids) override
    {
        Scope call(*this, Fn::Enumerate, vendor);
        ids.clear();
        if (call.status == 0 && vendor == kVendorPvr && clock_.now_ns() >= appears_at_ns) {
            ids = display_ids;
        }
        return call.status;
    }

    int adapter_luid(uint32_t display_id, LUID& out) override
    {
        Scope call(*this, Fn::AdapterLuid, display_id);
        if (call.status == 0) {
            out = luid;
        }
        return call.status;
    }

    EdidRoute read_edid(uint32_t display_id, std::vector<uint8_t>& out) override
    {
        Scope call(*this, Fn::ReadEdid, display_id);
        out.clear();
        if (call.status != 0) {
            return EdidRoute::None;
        }
        if (edid.empty()) {
            call.status = kBadOutput;
            return EdidRoute::None;
        }
        out = edid;
        return edid_route;
    }

    int enable_direct_mode(uint32_t vendor) override
    {
        Scope call(*this, Fn::EnableDirectMode, vendor);
        return call.status;
    }

    int bind_device(uint32_t display_id, IUnknown* d3d11, uint32_t /*vendor*/) override
    {
        // A NULL device is refused before the driver (before the queue) and leaves an earlier bind as it is. Any other
        // bind, like the real port's, first undoes the earlier one.
        Scope call(*this, Fn::BindDevice, display_id, false, d3d11 == nullptr ? kBadArgument : 0);
        if (d3d11 == nullptr) {
            return call.status;
        }
        bound_device = d3d11;
        bound_ = call.status == 0;
        return call.status;
    }

    int create_back_buffer(const Mode& mode, BackBuffer& out) override
    {
        Scope call(*this, Fn::CreateBackBuffer, 0, true);
        if (call.status == kNotBound) {
            return call.status;
        }
        modes_created.push_back(mode);
        if (call.status == 0) {
            if (handed_out_ < back_buffers.size()) {
                out = back_buffers[handed_out_++];
            } else {
                call.status = kBadOutput;
            }
        }
        return call.status;
    }

    int set_display_mode(const Mode& mode) override
    {
        Scope call(*this, Fn::SetDisplayMode, 0, true);
        if (call.status != kNotBound) {
            modes_set.push_back(mode);
        }
        return call.status;
    }

    int present(uint64_t tag) override
    {
        Scope call(*this, Fn::Present, tag, true);
        return call.status;
    }

    int present_stats(RefreshStats& out) override
    {
        Scope call(*this, Fn::PresentStats, 0, true);
        if (call.status == 0) {
            const Reading now = reading();
            out = RefreshStats{refresh_num, refresh_den, now.counter, now.qpc_ns + qpc_offset_ns};
        }
        return call.status;
    }

private:
    static const char* name_of(Fn fn)
    {
        switch (fn) {
        case Fn::Initialize: return "initialize";
        case Fn::FirstMissingId: return "first_missing_id";
        case Fn::DriverVersion: return "driver_version";
        case Fn::Enumerate: return "enumerate";
        case Fn::AdapterLuid: return "adapter_luid";
        case Fn::ReadEdid: return "read_edid";
        case Fn::EnableDirectMode: return "enable_direct_mode";
        case Fn::BindDevice: return "bind_device";
        case Fn::CreateBackBuffer: return "create_back_buffer";
        case Fn::SetDisplayMode: return "set_display_mode";
        case Fn::Present: return "present";
        case Fn::PresentStats: return "present_stats";
        }
        return "?";
    }

    // One Api call: runs the hook, then holds the lock, takes the call's status and logs the call. The call may
    // change `status`; the log gets the final one when the scope ends.
    //
    // `refuse`, if non-zero, is the status of a call the real port would refuse before reaching the driver; so is
    // kNotBound for a `needs_bind` call in strict_bind mode while nothing is bound. Neither uses up a queued status.
    class Scope {
    public:
        Scope(FakeApi& api, Fn fn, uint64_t arg, bool needs_bind = false, int refuse = 0) : api_(api)
        {
            if (api.on_call) {
                api.on_call(name_of(fn));
            }
            lock_ = std::unique_lock<std::mutex>(api.mutex_);
            if (refuse != 0) {
                status = refuse;
            } else if (needs_bind && api.strict_bind && !api.bound_) {
                status = kNotBound;
            } else {
                status = api.take_status(fn);
            }
            index_ = api.calls_.size();
            api.calls_.push_back(Call{name_of(fn), api.clock_.now_ns(), arg, status});
        }
        Scope(const Scope&) = delete;
        Scope& operator=(const Scope&) = delete;
        ~Scope() { api_.calls_[index_].status = status; }

        int status;

    private:
        FakeApi& api_;
        std::unique_lock<std::mutex> lock_;
        size_t index_ = 0;
    };

    int take_status(Fn fn)
    {
        std::deque<int>& queued = queued_[fn];
        if (!queued.empty()) {
            const int status = queued.front();
            queued.pop_front();
            return status;
        }
        const auto it = defaults_.find(fn);
        if (it != defaults_.end()) {
            return it->second;
        }
        return fn == Fn::SetDisplayMode ? kSetModeOk : 0;
    }

    struct Reading {
        uint32_t counter;
        int64_t qpc_ns;
    };

    // Counter and time of its last flip. While running, flip number base_count_ is at base_ns_ and every refresh_ns_
    // after that is one more; stopped, both are frozen. Caller holds the lock.
    Reading reading()
    {
        if (!running_) {
            return {base_count_, base_ns_};
        }
        const int64_t elapsed = std::max<int64_t>(0, clock_.now_ns() - base_ns_);
        const int64_t flips = elapsed / refresh_ns_;
        return {base_count_ + static_cast<uint32_t>(flips), base_ns_ + flips * refresh_ns_};
    }

    FakeClock& clock_;
    const int64_t refresh_ns_;

    mutable std::mutex mutex_;
    std::vector<Call> calls_;
    std::map<Fn, std::deque<int>> queued_;
    std::map<Fn, int> defaults_;
    size_t handed_out_ = 0;
    bool bound_ = false; // A bind_device() succeeded and no later one failed.

    bool running_ = true;
    uint32_t base_count_ = 0;
    int64_t base_ns_;
};

} // namespace nvdm
