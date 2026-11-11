// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "board.hpp"

#include <windows.h>

#include <setupapi.h>

#include <iterator>

namespace nvdm {
namespace {

wchar_t upper(wchar_t c)
{
    return c >= L'a' && c <= L'z' ? static_cast<wchar_t>(c - L'a' + L'A') : c;
}

bool starts_with_ignoring_case(std::wstring_view text, std::wstring_view prefix)
{
    if (text.size() < prefix.size()) {
        return false;
    }
    for (size_t i = 0; i < prefix.size(); i++) {
        if (upper(text[i]) != upper(prefix[i])) {
            return false;
        }
    }
    return true;
}

// The present devices of one enumerator ("USB", "HID"): true if one is the board.
bool enumerator_has_board(const wchar_t* enumerator)
{
    const HDEVINFO devices = SetupDiGetClassDevsW(nullptr, enumerator, nullptr, DIGCF_ALLCLASSES | DIGCF_PRESENT);
    if (devices == INVALID_HANDLE_VALUE) {
        return false;
    }
    bool found = false;
    for (DWORD index = 0; !found; index++) {
        SP_DEVINFO_DATA device{};
        device.cbSize = sizeof(device);
        if (!SetupDiEnumDeviceInfo(devices, index, &device)) {
            break; // ERROR_NO_MORE_ITEMS
        }
        wchar_t id[512];
        if (SetupDiGetDeviceInstanceIdW(devices, &device, id, static_cast<DWORD>(std::size(id)), nullptr)) {
            found = is_board_instance_id(id);
        }
    }
    SetupDiDestroyDeviceInfoList(devices);
    return found;
}

} // namespace

bool is_board_instance_id(std::wstring_view instance_id)
{
    // "<enumerator>\VID_34A4&PID_0044", then the end of the hardware id: '\' before the instance, or '&' before MI_xx.
    for (const std::wstring_view enumerator : {std::wstring_view(L"USB\\"), std::wstring_view(L"HID\\")}) {
        if (!starts_with_ignoring_case(instance_id, enumerator)) {
            continue;
        }
        const std::wstring_view rest = instance_id.substr(enumerator.size());
        constexpr std::wstring_view ids = L"VID_34A4&PID_0044";
        if (starts_with_ignoring_case(rest, ids) && rest.size() > ids.size() &&
            (rest[ids.size()] == L'\\' || rest[ids.size()] == L'&')) {
            return true;
        }
    }
    return false;
}

bool board_present()
{
    return enumerator_has_board(L"USB") || enumerator_has_board(L"HID");
}

} // namespace nvdm
