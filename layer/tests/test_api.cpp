// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The clock, the QPC conversion, the registry EDID route, and the test fakes (FakeClock, FakeApi) that Tasks 4 to 6
// rely on. The real NVAPI port (make_nvapi) is not exercised here: it needs the headset.

#include <catch2/catch_test_macros.hpp>

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "core/api.hpp"
#include "core/clock.hpp"
#include "fakes/fake_api.hpp"
#include "fakes/fake_clock.hpp"

namespace {

using nvdm::FakeApi;
using nvdm::FakeClock;
using Fn = nvdm::FakeApi::Fn;

constexpr int64_t kRefreshNs = nvdm::FakeApi::kDefaultRefreshNs;

nvdm::RefreshStats poll(FakeApi& api)
{
    nvdm::RefreshStats stats{};
    REQUIRE(api.present_stats(stats) == 0);
    return stats;
}

} // namespace

TEST_CASE("qpc_ticks_to_ns converts without overflowing")
{
    CHECK(nvdm::qpc_ticks_to_ns(10'000'000, 10'000'000) == 1'000'000'000);
    CHECK(nvdm::qpc_ticks_to_ns(1, 10'000'000) == 100);
    CHECK(nvdm::qpc_ticks_to_ns(0, 10'000'000) == 0);
    // A frequency that does not divide 1e9: the remainder is converted, not dropped.
    CHECK(nvdm::qpc_ticks_to_ns(1, 3) == 333'333'333);
    CHECK(nvdm::qpc_ticks_to_ns(4, 3) == 1'333'333'333);
    // ticks * 1e9 would overflow 64 bits here (1e15 ticks are 3 years at 10 MHz).
    CHECK(nvdm::qpc_ticks_to_ns(1'000'000'000'000'000, 10'000'000) == 100'000'000'000'000'000);
    CHECK(nvdm::qpc_ticks_to_ns(5, 0) == 0);
}

TEST_CASE("QpcClock is on the timebase of QueryPerformanceCounter, like present_stats' qpc")
{
    LARGE_INTEGER frequency{};
    REQUIRE(QueryPerformanceFrequency(&frequency));

    nvdm::QpcClock clock;
    const int64_t before = clock.now_ns();
    LARGE_INTEGER counter{};
    REQUIRE(QueryPerformanceCounter(&counter));
    const int64_t between = nvdm::qpc_ticks_to_ns(counter.QuadPart, frequency.QuadPart);
    const int64_t after = clock.now_ns();

    CHECK(before > 0);
    CHECK(before <= between);
    CHECK(between <= after);
}

TEST_CASE("read_edid_from_registry returns a base-block EDID or nothing")
{
    // Whether a PVR monitor is known to Windows depends on the machine (design 6.3), so both outcomes are valid;
    // what must hold is that a result is an EDID and a failure leaves nothing behind.
    std::vector<uint8_t> edid{0xaa, 0xbb};
    if (nvdm::read_edid_from_registry(edid)) {
        REQUIRE(edid.size() >= 128);
        const uint8_t header[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
        CHECK(std::vector<uint8_t>(edid.begin(), edid.begin() + 8) == std::vector<uint8_t>(header, header + 8));
    } else {
        CHECK(edid.empty());
    }
}

TEST_CASE("FakeClock only moves when told to")
{
    FakeClock clock;
    CHECK(clock.now_ns() == 0);
    clock.advance(1500);
    clock.advance(500);
    CHECK(clock.now_ns() == 2000);

    FakeClock later(1'000'000);
    CHECK(later.now_ns() == 1'000'000);
}

TEST_CASE("FakeApi counter advances once per refresh_ns of fake time")
{
    FakeClock clock;
    FakeApi api(clock);

    nvdm::RefreshStats stats = poll(api);
    CHECK(stats.counter == 0);
    CHECK(stats.refresh_num == 90000);
    CHECK(stats.refresh_den == 1000);
    CHECK(stats.qpc_ns == 0);

    clock.advance(kRefreshNs - 1);
    CHECK(poll(api).counter == 0);

    clock.advance(1);
    stats = poll(api);
    CHECK(stats.counter == 1);
    CHECK(stats.qpc_ns == kRefreshNs);

    // qpc_ns is the time of the last flip, not of the poll.
    clock.advance(4 * kRefreshNs + 5000);
    stats = poll(api);
    CHECK(stats.counter == 5);
    CHECK(stats.qpc_ns == 5 * kRefreshNs);

    // Polling does not move it; only the clock does.
    CHECK(poll(api).counter == 5);
    CHECK(poll(api).counter == 5);
}

TEST_CASE("FakeApi counter starts at the clock's time and takes another period")
{
    FakeClock clock(7 * kRefreshNs + 123);
    FakeApi api(clock, 1'000'000);
    CHECK(api.refresh_ns() == 1'000'000);

    CHECK(poll(api).counter == 0);
    CHECK(poll(api).qpc_ns == clock.now_ns());

    clock.advance(3'500'000);
    const nvdm::RefreshStats stats = poll(api);
    CHECK(stats.counter == 3);
    CHECK(stats.qpc_ns == 7 * kRefreshNs + 123 + 3'000'000);
}

TEST_CASE("FakeApi counter can be stopped and started again")
{
    FakeClock clock;
    FakeApi api(clock);

    clock.advance(3 * kRefreshNs + 100);
    api.stop_counter();
    clock.advance(10 * kRefreshNs);
    nvdm::RefreshStats stats = poll(api);
    CHECK(stats.counter == 3);
    CHECK(stats.qpc_ns == 3 * kRefreshNs);

    // Flips resume from the value it stopped at; the first new one is a period after the start.
    const int64_t restart_ns = clock.now_ns();
    api.start_counter();
    stats = poll(api);
    CHECK(stats.counter == 3);
    CHECK(stats.qpc_ns == restart_ns);

    clock.advance(kRefreshNs);
    stats = poll(api);
    CHECK(stats.counter == 4);
    CHECK(stats.qpc_ns == restart_ns + kRefreshNs);
}

TEST_CASE("FakeApi reset_counter models a link drop: back to 0, then counting on")
{
    FakeClock clock;
    FakeApi api(clock);

    clock.advance(7 * kRefreshNs + kRefreshNs / 2);
    CHECK(poll(api).counter == 7);

    const int64_t drop_ns = clock.now_ns();
    api.reset_counter();
    nvdm::RefreshStats stats = poll(api);
    CHECK(stats.counter == 0);
    CHECK(stats.qpc_ns == drop_ns);
    // The display keeps its mode, so the refresh rate is still reported.
    CHECK(stats.refresh_num == 90000);

    clock.advance(2 * kRefreshNs);
    stats = poll(api);
    CHECK(stats.counter == 2);
    CHECK(stats.qpc_ns == drop_ns + 2 * kRefreshNs);

    // A reset while stopped stays at 0 until started.
    api.stop_counter();
    api.reset_counter();
    clock.advance(5 * kRefreshNs);
    CHECK(poll(api).counter == 0);
    api.start_counter();
    clock.advance(kRefreshNs);
    CHECK(poll(api).counter == 1);
}

TEST_CASE("FakeApi can report a down link: refresh 0")
{
    FakeClock clock;
    FakeApi api(clock);
    api.refresh_num = 0;
    api.refresh_den = 0;
    const nvdm::RefreshStats stats = poll(api);
    CHECK(stats.refresh_num == 0);
    CHECK(stats.refresh_den == 0);
}

TEST_CASE("FakeApi records calls in order, with arguments and fake time")
{
    FakeClock clock(100);
    FakeApi api(clock);

    REQUIRE(api.initialize() == 0);
    CHECK(api.first_missing_id() == nullptr);
    CHECK(api.driver_version() == "621.01");

    std::vector<uint32_t> ids;
    REQUIRE(api.enumerate(nvdm::kVendorPvr, ids) == 0);
    REQUIRE(ids == api.display_ids);
    REQUIRE_FALSE(ids.empty());

    clock.advance(50);
    LUID luid{};
    REQUIRE(api.adapter_luid(ids[0], luid) == 0);
    CHECK(luid.LowPart == api.luid.LowPart);
    CHECK(luid.HighPart == api.luid.HighPart);

    std::vector<uint8_t> edid;
    REQUIRE(api.read_edid(ids[0], edid) == nvdm::EdidRoute::Nvapi);
    CHECK(edid == api.edid);

    REQUIRE(api.enable_direct_mode(nvdm::kVendorPvr) == 0);
    IUnknown* device = reinterpret_cast<IUnknown*>(0x1000);
    REQUIRE(api.bind_device(ids[0], device, nvdm::kVendorPvr) == 0);
    CHECK(api.bound_device == device);
    REQUIRE(api.present(0x11) == 0);
    api.note("custom");

    const std::vector<std::string> expected = {
        "initialize", "first_missing_id", "driver_version", "enumerate",   "adapter_luid", "read_edid",
        "enable_direct_mode", "bind_device", "present", "custom",
    };
    CHECK(api.names() == expected);
    // The task 4 order check leaves driver_version out of its list.
    CHECK(api.names({"driver_version"}).size() == expected.size() - 1);
    CHECK(api.count("present") == 1);
    CHECK(api.count("present_stats") == 0);

    const std::vector<FakeApi::Call> calls = api.calls();
    REQUIRE(calls.size() == expected.size());
    CHECK(calls[3].arg == nvdm::kVendorPvr);
    CHECK(calls[3].at_ns == 100);
    CHECK(calls[4].arg == ids[0]);
    CHECK(calls[4].at_ns == 150);
    CHECK(calls[7].arg == ids[0]);
    CHECK(calls[8].arg == 0x11);
    CHECK(calls[8].status == 0);
}

TEST_CASE("FakeApi returns queued results once each, then its defaults")
{
    FakeClock clock;
    FakeApi api(clock);
    IUnknown* const device = reinterpret_cast<IUnknown*>(0x1000);

    api.queue(Fn::BindDevice, -5);
    api.queue(Fn::BindDevice, 0);
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == -5);
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == 0);
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == 0);
    CHECK(api.count("bind_device") == 3);

    // SetDisplayMode succeeds with -208; the queue can say otherwise a number of times.
    nvdm::Mode mode;
    CHECK(api.set_display_mode(mode) == nvdm::kSetModeOk);
    api.queue(Fn::SetDisplayMode, 7, 2);
    CHECK(api.set_display_mode(mode) == 7);
    CHECK(api.set_display_mode(mode) == 7);
    CHECK(api.set_display_mode(mode) == nvdm::kSetModeOk);

    // A default for every call that has no queued result.
    api.set_default_status(Fn::Present, 3);
    CHECK(api.present(1) == 3);
    api.queue(Fn::Present, 0);
    CHECK(api.present(1) == 0);
    CHECK(api.present(1) == 3);
    CHECK(api.calls().back().status == 3);
}

TEST_CASE("FakeApi leaves outputs empty when a call fails")
{
    FakeClock clock;
    FakeApi api(clock);

    api.queue(Fn::ReadEdid, -1);
    std::vector<uint8_t> edid{1, 2, 3};
    CHECK(api.read_edid(1, edid) == nvdm::EdidRoute::None);
    CHECK(edid.empty());
    CHECK(api.read_edid(1, edid) == nvdm::EdidRoute::Nvapi);

    api.edid.clear();
    CHECK(api.read_edid(1, edid) == nvdm::EdidRoute::None);

    api.queue(Fn::Enumerate, -9);
    std::vector<uint32_t> ids{42};
    CHECK(api.enumerate(nvdm::kVendorPvr, ids) == -9);
    CHECK(ids.empty());

    api.queue(Fn::AdapterLuid, -3);
    LUID luid{11, 22};
    CHECK(api.adapter_luid(1, luid) == -3);
    CHECK(luid.LowPart == 11);
    CHECK(luid.HighPart == 22);
}

TEST_CASE("FakeApi shows no display before appears_at_ns, and none for another vendor")
{
    FakeClock clock;
    FakeApi api(clock);
    api.appears_at_ns = 12'000'000'000;

    std::vector<uint32_t> ids;
    CHECK(api.enumerate(nvdm::kVendorPvr, ids) == 0);
    CHECK(ids.empty());

    clock.advance(11'999'999'999);
    CHECK(api.enumerate(nvdm::kVendorPvr, ids) == 0);
    CHECK(ids.empty());

    clock.advance(1);
    CHECK(api.enumerate(nvdm::kVendorPvr, ids) == 0);
    CHECK(ids == api.display_ids);

    CHECK(api.enumerate(0x1234, ids) == 0);
    CHECK(ids.empty());
}

TEST_CASE("FakeApi hands out its back-buffers in order and records the modes")
{
    FakeClock clock;
    FakeApi api(clock);
    REQUIRE(api.back_buffers.size() == nvdm::kBackBufferCount);

    nvdm::Mode mode;
    for (uint32_t i = 0; i < nvdm::kBackBufferCount; i++) {
        nvdm::BackBuffer buffer{};
        REQUIRE(api.create_back_buffer(mode, buffer) == 0);
        CHECK(buffer.tag == api.back_buffers[i].tag);
        CHECK(buffer.shared == api.back_buffers[i].shared);
    }
    nvdm::BackBuffer extra{};
    CHECK(api.create_back_buffer(mode, extra) == nvdm::kBadOutput);
    CHECK(api.modes_created.size() == 4);
    CHECK(api.modes_created[0].width == 7104);

    // The tags are distinct, or a test could not tell the buffers apart.
    CHECK(api.back_buffers[0].tag != api.back_buffers[1].tag);
    CHECK(api.back_buffers[1].tag != api.back_buffers[2].tag);

    api.reset_back_buffers();
    nvdm::BackBuffer again{};
    CHECK(api.create_back_buffer(mode, again) == 0);
    CHECK(again.tag == api.back_buffers[0].tag);

    CHECK(api.set_display_mode(mode) == nvdm::kSetModeOk);
    CHECK(api.modes_set.size() == 1);
}

TEST_CASE("FakeApi reports what it is configured to")
{
    FakeClock clock;
    FakeApi api(clock);
    CHECK(api.first_missing_id() == nullptr);

    api.missing_id = "Present";
    REQUIRE(api.first_missing_id() != nullptr);
    CHECK(std::string(api.first_missing_id()) == "Present");

    api.driver = "630.00";
    CHECK(api.driver_version() == "630.00");

    api.luid = LUID{0xabcd, 0x12};
    LUID luid{};
    REQUIRE(api.adapter_luid(1, luid) == 0);
    CHECK(luid.LowPart == 0xabcd);
    CHECK(luid.HighPart == 0x12);
}

TEST_CASE("FakeApi default EDID is a base block for a Pimax Dream Air")
{
    FakeClock clock;
    FakeApi api(clock);
    REQUIRE(api.edid.size() == 128);
    const uint8_t header[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
    CHECK(std::vector<uint8_t>(api.edid.begin(), api.edid.begin() + 8) == std::vector<uint8_t>(header, header + 8));
    // Manufacturer "PVR" (0x42d2, big-endian), product 0x1019 (little-endian).
    CHECK(api.edid[8] == 0x42);
    CHECK(api.edid[9] == 0xd2);
    CHECK(api.edid[10] == 0x19);
    CHECK(api.edid[11] == 0x10);
    unsigned sum = 0;
    for (uint8_t b : api.edid) {
        sum += b;
    }
    CHECK(sum % 256 == 0);
}

TEST_CASE("FakeApi calls the on_call hook at entry, before the call is recorded")
{
    FakeClock clock;
    FakeApi api(clock);
    std::vector<std::string> seen;
    std::vector<size_t> recorded_when_seen;
    api.on_call = [&](const char* name) {
        seen.push_back(name);
        recorded_when_seen.push_back(api.calls().size());
    };

    REQUIRE(api.present(1) == 0);
    nvdm::RefreshStats stats{};
    REQUIRE(api.present_stats(stats) == 0);

    CHECK(seen == std::vector<std::string>{"present", "present_stats"});
    CHECK(recorded_when_seen == std::vector<size_t>{0, 1});
}

// --- Fixes folded in later -------------------------------------------------------------------------------------------

TEST_CASE("FakeApi strict_bind refuses the calls that need the bound display until bind_device succeeds")
{
    FakeClock clock;
    IUnknown* const device = reinterpret_cast<IUnknown*>(0x1000);
    nvdm::Mode mode;
    nvdm::BackBuffer buffer{};
    nvdm::RefreshStats stats{};

    // Off by default: the fake lets a test call these without a bind.
    FakeApi lenient(clock);
    CHECK_FALSE(lenient.strict_bind);
    CHECK(lenient.create_back_buffer(mode, buffer) == 0);
    CHECK(lenient.present(1) == 0);

    FakeApi api(clock);
    api.strict_bind = true;
    CHECK(api.create_back_buffer(mode, buffer) == nvdm::kNotBound);
    CHECK(api.set_display_mode(mode) == nvdm::kNotBound);
    CHECK(api.present(1) == nvdm::kNotBound);
    CHECK(api.present_stats(stats) == nvdm::kNotBound);
    // The real port makes no driver call here, so nothing is recorded as created or set, and no queued result is used.
    CHECK(api.modes_created.empty());
    CHECK(api.modes_set.empty());
    api.queue(Fn::Present, 9);
    CHECK(api.present(1) == nvdm::kNotBound);

    // A refused bind leaves it unbound; a good one binds.
    api.queue(Fn::BindDevice, -5);
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == -5);
    CHECK(api.present(1) == nvdm::kNotBound);
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == 0);
    CHECK(api.present(1) == 9); // The queued result was still there.
    CHECK(api.create_back_buffer(mode, buffer) == 0);
    CHECK(api.set_display_mode(mode) == nvdm::kSetModeOk);
    CHECK(api.present_stats(stats) == 0);

    // As with the real port, a bind that fails after a good one leaves the display unbound.
    api.queue(Fn::BindDevice, -5);
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == -5);
    CHECK(api.present(1) == nvdm::kNotBound);
    CHECK(api.calls().back().status == nvdm::kNotBound);
}

TEST_CASE("bind_device refuses a NULL device without calling the driver")
{
    FakeClock clock;
    FakeApi api(clock);
    IUnknown* const device = reinterpret_cast<IUnknown*>(0x1000);

    api.queue(Fn::BindDevice, -5);
    CHECK(api.bind_device(1, nullptr, nvdm::kVendorPvr) == nvdm::kBadArgument);
    CHECK(api.bound_device == nullptr);
    // The refusal did not use up the queued result.
    CHECK(api.bind_device(1, device, nvdm::kVendorPvr) == -5);

    // The real port, which has made no NVAPI call yet, says the same: the check comes before any lookup (without it
    // the answer would be kNoEntry).
    const std::unique_ptr<nvdm::Api> real = nvdm::make_nvapi();
    CHECK(real->bind_device(1, nullptr, nvdm::kVendorPvr) == nvdm::kBadArgument);
}

TEST_CASE("FakeApi read_edid says which route answered")
{
    FakeClock clock;
    FakeApi api(clock);
    std::vector<uint8_t> edid;
    CHECK(api.read_edid(1, edid) == nvdm::EdidRoute::Nvapi);

    api.edid_route = nvdm::EdidRoute::Registry;
    CHECK(api.read_edid(1, edid) == nvdm::EdidRoute::Registry);
    CHECK(edid == api.edid);

    api.queue(Fn::ReadEdid, -1);
    CHECK(api.read_edid(1, edid) == nvdm::EdidRoute::None);

    CHECK(std::string(nvdm::edid_route_name(nvdm::EdidRoute::None)) == "none");
    CHECK(std::string(nvdm::edid_route_name(nvdm::EdidRoute::Nvapi)) == "NVAPI");
    CHECK(std::string(nvdm::edid_route_name(nvdm::EdidRoute::NvapiOutput)) == "NVAPI-output");
    CHECK(std::string(nvdm::edid_route_name(nvdm::EdidRoute::Registry)) == "registry");
    CHECK(std::string(nvdm::edid_route_name(nvdm::EdidRoute::Board)) == "board");
}

namespace {

// A scratch copy of Enum\DISPLAY under HKCU, so the registry scan can be tested without touching HKLM.
class ScratchDisplayKey {
public:
    ScratchDisplayKey()
        : path_(L"Software\\MiradorDirectModeTests\\" + std::to_wstring(GetCurrentProcessId()))
    {
        const LONG status = RegCreateKeyExW(HKEY_CURRENT_USER, path_.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr,
                                            &root_, nullptr);
        REQUIRE(status == ERROR_SUCCESS);
    }
    ScratchDisplayKey(const ScratchDisplayKey&) = delete;
    ScratchDisplayKey& operator=(const ScratchDisplayKey&) = delete;
    ~ScratchDisplayKey()
    {
        if (root_ != nullptr) {
            RegCloseKey(root_);
        }
        RegDeleteTreeW(HKEY_CURRENT_USER, path_.c_str());
        RegDeleteKeyW(HKEY_CURRENT_USER, L"Software\\MiradorDirectModeTests"); // Only if nothing else is in it.
    }

    HKEY key() const { return root_; }

    void add(const wchar_t* model, const wchar_t* instance, const std::vector<uint8_t>& edid)
    {
        const std::wstring sub = std::wstring(model) + L"\\" + instance + L"\\Device Parameters";
        HKEY parameters = nullptr;
        REQUIRE(RegCreateKeyExW(root_, sub.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS, nullptr, &parameters, nullptr) ==
                ERROR_SUCCESS);
        REQUIRE(RegSetValueExW(parameters, L"EDID", 0, REG_BINARY, edid.data(), static_cast<DWORD>(edid.size())) ==
                ERROR_SUCCESS);
        RegCloseKey(parameters);
    }

    void remove_model(const wchar_t* model) { RegDeleteTreeW(root_, model); }

private:
    std::wstring path_;
    HKEY root_ = nullptr;
};

uint16_t product_of(const std::vector<uint8_t>& edid)
{
    REQUIRE(edid.size() >= 12);
    return static_cast<uint16_t>(edid[10] | edid[11] << 8);
}

} // namespace

TEST_CASE("the registry scan prefers the PVR1019 key, then any other PVR key")
{
    ScratchDisplayKey scratch;
    // PVR0001 comes before PVR1019 in key order, so only the preference makes PVR1019 win.
    scratch.add(L"ABC0001", L"inst", FakeApi::make_edid(0x0421, 0x0001)); // Not a PVR monitor.
    scratch.add(L"PVR0001", L"inst", FakeApi::make_edid(0x42d2, 0x0002));
    scratch.add(L"PVR1019", L"inst", FakeApi::make_edid(0x42d2, 0x1019));
    scratch.add(L"PVR2000", L"inst", FakeApi::make_edid(0x42d2, 0x2000));

    std::vector<uint8_t> edid{0xaa};
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(product_of(edid) == 0x1019);

    // Without it, another PVR key answers (which one is up to the registry's order); a non-PVR monitor never does.
    scratch.remove_model(L"PVR1019");
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    const uint16_t other = product_of(edid);
    CHECK((other == 0x0002 || other == 0x2000));

    scratch.remove_model(L"PVR0001");
    scratch.remove_model(L"PVR2000");
    CHECK_FALSE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(edid.empty());
}

TEST_CASE("the registry scan skips a PVR1019 instance with no usable EDID")
{
    ScratchDisplayKey scratch;
    scratch.add(L"PVR1019", L"broken", std::vector<uint8_t>(128, 0x11)); // No EDID header.
    scratch.add(L"PVR0001", L"inst", FakeApi::make_edid(0x42d2, 0x0002));

    std::vector<uint8_t> edid;
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(product_of(edid) == 0x0002);

    // A good second instance under PVR1019 beats the other key.
    scratch.add(L"PVR1019", L"good", FakeApi::make_edid(0x42d2, 0x1019));
    REQUIRE(nvdm::read_edid_from_display_key(scratch.key(), edid));
    CHECK(product_of(edid) == 0x1019);
}
