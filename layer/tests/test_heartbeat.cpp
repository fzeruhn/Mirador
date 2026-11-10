// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The check tool's board heartbeat (design 7): the report, the interface it goes to, and the thread that sends it. The
// real HID transport is never opened here (Pimax runs on this machine); the thread runs against a fake port.

#include <catch2/catch_test_macros.hpp>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "check/heartbeat.hpp"

namespace {

using namespace std::chrono_literals;

// A port that records what the heartbeat does to it.
struct PortLog {
    std::mutex mutex;
    int opens = 0;
    int closes = 0;
    int drains = 0;
    std::vector<std::array<uint8_t, 65>> writes;
    std::vector<std::chrono::steady_clock::time_point> write_times;
    bool open_ok = true;
    bool write_ok = true;
    bool write_ok_after_first = true; // false: the first write works, the later ones fail
    std::string open_detail = "fake board";
};

class FakePort : public dvl::HeartbeatPort {
public:
    explicit FakePort(std::shared_ptr<PortLog> log) : log_(std::move(log)) {}

    bool open(std::string& detail) override
    {
        std::lock_guard<std::mutex> hold(log_->mutex);
        log_->opens++;
        detail = log_->open_ok ? log_->open_detail : "no board";
        return log_->open_ok;
    }
    bool write(const std::array<uint8_t, 65>& report) override
    {
        std::lock_guard<std::mutex> hold(log_->mutex);
        const bool first = log_->writes.empty();
        log_->writes.push_back(report);
        log_->write_times.push_back(std::chrono::steady_clock::now());
        return first ? log_->write_ok : (log_->write_ok && log_->write_ok_after_first);
    }
    void drain() override
    {
        std::lock_guard<std::mutex> hold(log_->mutex);
        log_->drains++;
    }
    void close() override
    {
        std::lock_guard<std::mutex> hold(log_->mutex);
        log_->closes++;
    }

private:
    std::shared_ptr<PortLog> log_;
};

dvl::Heartbeat make_heartbeat(const std::shared_ptr<PortLog>& log, std::chrono::milliseconds period)
{
    return dvl::Heartbeat(std::make_unique<FakePort>(log), period);
}

} // namespace

TEST_CASE("heartbeat_report_bytes")
{
    // DreamAirWin\heartbeat.py: bytes([0x00, 0x58, 0x04, 0x39, 0x00, 0x00, 0x00]).ljust(65, b"\x00")
    const std::array<uint8_t, 65> report = dvl::heartbeat_report();
    CHECK(report.size() == 65);
    const uint8_t head[7] = {0x00, 0x58, 0x04, 0x39, 0x00, 0x00, 0x00};
    CHECK(std::equal(std::begin(head), std::end(head), report.begin()));
    CHECK(std::all_of(report.begin() + 7, report.end(), [](uint8_t b) { return b == 0; }));
    // Report id 0 comes first: the board has one unnumbered output report.
    CHECK(report[0] == 0x00);
}

TEST_CASE("the heartbeat period is design 7's 200 ms")
{
    // DreamAirWin\heartbeat.py sends every 200 ms; the board sleeps without it. The real Heartbeat() uses this period.
    STATIC_REQUIRE(dvl::kHeartbeatPeriod == std::chrono::milliseconds(200));
}

TEST_CASE("the heartbeat goes to the board's interface 0 only")
{
    const char* const board =
        "\\\\?\\hid#vid_34a4&pid_0044&mi_00#8&2a3b4c5d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    CHECK(dvl::is_board_interface_path(board));
    // Windows spells the path in either case.
    CHECK(dvl::is_board_interface_path("\\\\?\\HID#VID_34A4&PID_0044&MI_00#8&1&0&0000#{4d1e55b2}"));

    // Another interface of the same device, another product, another vendor, and not a HID path at all.
    CHECK_FALSE(dvl::is_board_interface_path("\\\\?\\hid#vid_34a4&pid_0044&mi_01#8&2a3b4c5d&0&0000#{4d1e55b2}"));
    CHECK_FALSE(dvl::is_board_interface_path("\\\\?\\hid#vid_34a4&pid_0045&mi_00#8&2a3b4c5d&0&0000#{4d1e55b2}"));
    CHECK_FALSE(dvl::is_board_interface_path("\\\\?\\hid#vid_34a5&pid_0044&mi_00#8&2a3b4c5d&0&0000#{4d1e55b2}"));
    CHECK_FALSE(dvl::is_board_interface_path("\\\\?\\hid#vid_34a4&pid_0044#8&2a3b4c5d&0&0000#{4d1e55b2}"));
    CHECK_FALSE(dvl::is_board_interface_path(""));
}

TEST_CASE("the heartbeat sends the report at once and then every period")
{
    auto log = std::make_shared<PortLog>();
    dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
    REQUIRE(heartbeat.start());
    {
        // The first report is written before start() returns.
        std::lock_guard<std::mutex> hold(log->mutex);
        REQUIRE(log->writes.size() >= 1);
        CHECK(log->writes[0] == dvl::heartbeat_report());
        CHECK(log->opens == 1);
    }
    std::this_thread::sleep_for(300ms);
    heartbeat.stop();

    std::lock_guard<std::mutex> hold(log->mutex);
    // 300 ms at 10 ms is about 30 reports; a busy machine sends fewer, never more than one per period.
    CHECK(log->writes.size() >= 5);
    CHECK(log->writes.size() <= 40);
    for (const auto& w : log->writes) {
        CHECK(w == dvl::heartbeat_report());
    }
    for (size_t i = 1; i < log->write_times.size(); i++) {
        CHECK(log->write_times[i] - log->write_times[i - 1] >= 8ms); // a period, less the clock's grain
    }
    CHECK(heartbeat.sent() == log->writes.size());
    CHECK(heartbeat.failed() == 0);
}

TEST_CASE("the heartbeat drains what the board sends after each report")
{
    auto log = std::make_shared<PortLog>();
    dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
    REQUIRE(heartbeat.start());
    std::this_thread::sleep_for(100ms);
    heartbeat.stop();
    std::lock_guard<std::mutex> hold(log->mutex);
    CHECK(log->drains >= 2);
    CHECK(static_cast<size_t>(log->drains) <= log->writes.size());
}

TEST_CASE("stop closes the port once, ends the thread and may be repeated")
{
    auto log = std::make_shared<PortLog>();
    dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
    REQUIRE(heartbeat.start());
    CHECK(heartbeat.running());
    heartbeat.stop();
    CHECK_FALSE(heartbeat.running());
    size_t writes_at_stop = 0;
    {
        std::lock_guard<std::mutex> hold(log->mutex);
        CHECK(log->closes == 1);
        writes_at_stop = log->writes.size();
    }
    std::this_thread::sleep_for(60ms);
    heartbeat.stop(); // again: nothing happens
    std::lock_guard<std::mutex> hold(log->mutex);
    CHECK(log->closes == 1);
    CHECK(log->writes.size() == writes_at_stop); // nothing is sent after stop() returned
}

TEST_CASE("the destructor stops a running heartbeat")
{
    auto log = std::make_shared<PortLog>();
    {
        dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
        REQUIRE(heartbeat.start());
    }
    std::lock_guard<std::mutex> hold(log->mutex);
    CHECK(log->closes == 1);
}

TEST_CASE("start fails, without a thread, when the board cannot be opened")
{
    auto log = std::make_shared<PortLog>();
    log->open_ok = false;
    dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
    CHECK_FALSE(heartbeat.start());
    CHECK_FALSE(heartbeat.running());
    CHECK(heartbeat.detail() == "no board");
    std::this_thread::sleep_for(40ms);
    heartbeat.stop();
    std::lock_guard<std::mutex> hold(log->mutex);
    CHECK(log->writes.empty());
    CHECK(log->closes == 0); // it never opened
}

TEST_CASE("start fails and closes the port when the first report cannot be written")
{
    auto log = std::make_shared<PortLog>();
    log->write_ok = false;
    dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
    CHECK_FALSE(heartbeat.start());
    CHECK_FALSE(heartbeat.running());
    CHECK(heartbeat.sent() == 0);
    std::lock_guard<std::mutex> hold(log->mutex);
    CHECK(log->closes == 1);
    CHECK(log->writes.size() == 1);
}

TEST_CASE("a write that fails later is counted and the heartbeat keeps trying")
{
    auto log = std::make_shared<PortLog>();
    log->write_ok_after_first = false;
    dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
    REQUIRE(heartbeat.start());
    std::this_thread::sleep_for(150ms);
    CHECK(heartbeat.running());
    heartbeat.stop();
    CHECK(heartbeat.sent() == 1);
    CHECK(heartbeat.failed() >= 3);
    std::lock_guard<std::mutex> hold(log->mutex);
    CHECK(log->writes.size() == 1 + heartbeat.failed());
}

TEST_CASE("a second start while running does nothing")
{
    auto log = std::make_shared<PortLog>();
    dvl::Heartbeat heartbeat = make_heartbeat(log, 10ms);
    REQUIRE(heartbeat.start());
    CHECK(heartbeat.start());
    heartbeat.stop();
    std::lock_guard<std::mutex> hold(log->mutex);
    CHECK(log->opens == 1);
    CHECK(log->closes == 1);
}
