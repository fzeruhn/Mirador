// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <string_view>

namespace nvdm {

// The Dream Air's board (design 7): USB vendor 0x34A4, product 0x0044. The display only appears while the board gets
// its heartbeat, so a connected Dream Air always has it.
inline constexpr uint16_t kBoardVendorId = 0x34A4;
inline constexpr uint16_t kBoardProductId = 0x0044;

// True for a device instance id of the board under the USB or HID enumerator ("USB\VID_34A4&PID_0044\...",
// "USB\VID_34A4&PID_0044&MI_00\...", "HID\VID_34A4&PID_0044&MI_00\..."), in either case.
bool is_board_instance_id(std::wstring_view instance_id);

// Whether a present device of the USB or HID enumerator is the board (SetupAPI's list of present devices). Nothing is
// opened: the board belongs to whoever sends its heartbeat. False if the list cannot be read.
bool board_present();

} // namespace nvdm
