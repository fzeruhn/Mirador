// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <array>
#include <chrono>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>

#include "core/board.hpp"

namespace dvl {

// The Dream Air's board (design 7): USB vendor 0x34A4, product 0x0044, interface 0.
inline constexpr uint16_t kBoardVendorId = nvdm::kBoardVendorId;
inline constexpr uint16_t kBoardProductId = nvdm::kBoardProductId;

inline constexpr size_t kHeartbeatSize = 65;
inline constexpr std::chrono::milliseconds kHeartbeatPeriod{200};

// The "PC alive" output report DreamAirWin\heartbeat.py sends: report id 0, then 58 04 39 00 00 00, zero-padded to 65
// bytes. Without it the board goes to sleep and the display disappears.
std::array<uint8_t, kHeartbeatSize> heartbeat_report();

// True for the Win32 device path of the board's interface 0 (`\\?\hid#vid_34a4&pid_0044&mi_00#...`), in either case.
bool is_board_interface_path(std::string_view path);

// Where the heartbeat goes. The real one is the board through Win32 HID (make_hid_port); tests use a fake.
class HeartbeatPort {
public:
    virtual ~HeartbeatPort() = default;
    // Finds and opens the port. `detail` is what it opened (its path), or why it could not.
    virtual bool open(std::string& detail) = 0;
    virtual bool write(const std::array<uint8_t, kHeartbeatSize>& report) = 0;
    // Reads and drops whatever the board sent, without waiting.
    virtual void drain() = 0;
    virtual void close() = 0;
};

// SetupAPI finds the HID interface, CreateFile opens it for overlapped reads and writes, WriteFile sends, ReadFile
// drains. No hidapi, no Python.
std::unique_ptr<HeartbeatPort> make_hid_port();

// Sends heartbeat_report() to the port every period, on a thread of its own, and drains what the board sends back.
class Heartbeat {
public:
    Heartbeat();                                 // the real board, every kHeartbeatPeriod
    Heartbeat(std::unique_ptr<HeartbeatPort> port, std::chrono::milliseconds period);
    ~Heartbeat();                                // stop()
    Heartbeat(const Heartbeat&) = delete;
    Heartbeat& operator=(const Heartbeat&) = delete;

    // Opens the port, sends the first report before returning, and starts the thread. False, with nothing running, if
    // the port does not open or the first report cannot be written (detail() says which). True again if running.
    bool start();
    // Stops the thread, then closes the port. Nothing is sent after it returns. May be called again, or never started.
    void stop();

    bool running() const;
    uint64_t sent() const;   // reports written
    uint64_t failed() const; // writes that failed
    std::string detail() const; // the path it sends to, or why start() failed

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace dvl
