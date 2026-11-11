// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The Dream Air's board in the list of present devices (design 6.3's last EDID route): which instance ids are the
// board's. Whether the board is in this machine's list is not tested here; the check tool's run on the headset is.

#include <catch2/catch_test_macros.hpp>

#include "core/board.hpp"

TEST_CASE("the board's USB and HID instance ids are recognised, in either case")
{
    CHECK(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_0044\\5&2B1E2F7&0&3"));
    CHECK(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_0044&MI_00\\6&1A2B3C4D&0&0000"));
    CHECK(nvdm::is_board_instance_id(L"HID\\VID_34A4&PID_0044&MI_00\\7&367B06A1&0&0000"));
    CHECK(nvdm::is_board_instance_id(L"usb\\vid_34a4&pid_0044\\5&2b1e2f7&0&3"));
}

TEST_CASE("other devices, other enumerators and look-alike ids are not the board")
{
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_0045\\5&2B1E2F7&0&3"));  // another Pimax product
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\VID_045E&PID_0044\\1"));              // another vendor
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\VID_34A4&PID_00441\\1"));             // a longer product id
    CHECK_FALSE(nvdm::is_board_instance_id(L"PCI\\VEN_10DE&DEV_2C05&SUBSYS_143C196E\\4")); // the GPU
    CHECK_FALSE(nvdm::is_board_instance_id(L"SWD\\MMDEVAPI\\VID_34A4&PID_0044"));         // not USB or HID
    CHECK_FALSE(nvdm::is_board_instance_id(L"DISPLAY\\PVR1019\\5&991C2CF&0&UID4357"));    // the headset as a monitor
    CHECK_FALSE(nvdm::is_board_instance_id(L""));
    CHECK_FALSE(nvdm::is_board_instance_id(L"USB\\"));
}
