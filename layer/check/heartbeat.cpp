// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "check/heartbeat.hpp"

#include <windows.h>

#include <setupapi.h>
#include <hidsdi.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>

namespace dvl {

std::array<uint8_t, kHeartbeatSize> heartbeat_report()
{
    std::array<uint8_t, kHeartbeatSize> report{}; // zero-padded
    report[0] = 0x00;                             // report id
    report[1] = 0x58;
    report[2] = 0x04;
    report[3] = 0x39;
    return report;
}

bool is_board_interface_path(std::string_view path)
{
    std::string lowered(path);
    for (char& c : lowered) {
        c = c >= 'A' && c <= 'Z' ? static_cast<char>(c - 'A' + 'a') : c;
    }
    return lowered.find("vid_34a4&pid_0044&mi_00") != std::string::npos;
}

// --- The board through Win32 HID ---------------------------------------------------------------------------------

namespace {

std::string narrow(const wchar_t* wide)
{
    const int size = WideCharToMultiByte(CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 1) {
        return {};
    }
    std::string text(static_cast<size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, &text[0], size, nullptr, nullptr);
    text.resize(static_cast<size_t>(size) - 1);
    return text;
}

std::string error_text(const char* what, DWORD error)
{
    return std::string(what) + " (Windows error " + std::to_string(error) + ")";
}

class HidPort final : public HeartbeatPort {
public:
    ~HidPort() override { close(); }

    bool open(std::string& detail) override
    {
        close();
        GUID hid_guid{};
        HidD_GetHidGuid(&hid_guid);
        const HDEVINFO devices = SetupDiGetClassDevsW(&hid_guid, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devices == INVALID_HANDLE_VALUE) {
            detail = error_text("SetupDiGetClassDevs failed", GetLastError());
            return false;
        }
        bool found = false;
        DWORD open_error = 0;
        for (DWORD index = 0;; index++) {
            SP_DEVICE_INTERFACE_DATA interface_data{};
            interface_data.cbSize = sizeof(interface_data);
            if (!SetupDiEnumDeviceInterfaces(devices, nullptr, &hid_guid, index, &interface_data)) {
                break; // ERROR_NO_MORE_ITEMS
            }
            DWORD size = 0;
            SetupDiGetDeviceInterfaceDetailW(devices, &interface_data, nullptr, 0, &size, nullptr);
            if (size < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)) {
                continue;
            }
            std::vector<uint8_t> storage(size, 0);
            auto* path = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(storage.data());
            path->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
            if (!SetupDiGetDeviceInterfaceDetailW(devices, &interface_data, path, size, nullptr, nullptr)) {
                continue;
            }
            // Only the board's interface is opened: the path names the ids, so no other HID device is touched.
            const std::string narrow_path = narrow(path->DevicePath);
            if (!is_board_interface_path(narrow_path)) {
                continue;
            }
            found = true;
            const HANDLE handle = CreateFileW(path->DevicePath, GENERIC_READ | GENERIC_WRITE,
                                              FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
                                              FILE_FLAG_OVERLAPPED, nullptr);
            if (handle == INVALID_HANDLE_VALUE) {
                open_error = GetLastError();
                continue;
            }
            HIDD_ATTRIBUTES attributes{};
            attributes.Size = sizeof(attributes);
            if (!HidD_GetAttributes(handle, &attributes) || attributes.VendorID != kBoardVendorId ||
                attributes.ProductID != kBoardProductId) {
                CloseHandle(handle);
                continue;
            }
            handle_ = handle;
            detail = narrow_path;
            break;
        }
        SetupDiDestroyDeviceInfoList(devices);
        if (handle_ == INVALID_HANDLE_VALUE) {
            detail = found ? error_text("the Dream Air's board (USB 34A4:0044, interface 0) was found but could not be "
                                        "opened",
                                        open_error)
                           : "the Dream Air's board (USB 34A4:0044, interface 0) was not found; is the headset "
                             "connected?";
            return false;
        }

        // The report sizes the board's descriptor gives (they include the report id byte).
        PHIDP_PREPARSED_DATA preparsed = nullptr;
        if (HidD_GetPreparsedData(handle_, &preparsed)) {
            HIDP_CAPS caps{};
            if (HidP_GetCaps(preparsed, &caps) == HIDP_STATUS_SUCCESS) {
                output_length_ = caps.OutputReportByteLength;
                input_length_ = caps.InputReportByteLength;
            }
            HidD_FreePreparsedData(preparsed);
        }
        write_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        read_event_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (write_event_ == nullptr || read_event_ == nullptr) {
            detail = error_text("CreateEvent failed", GetLastError());
            close();
            return false;
        }
        read_buffer_.assign(std::max<size_t>(input_length_, 1), 0);
        return true;
    }

    bool write(const std::array<uint8_t, kHeartbeatSize>& report) override
    {
        if (handle_ == INVALID_HANDLE_VALUE) {
            return false;
        }
        // hidapi pads or cuts the report to the descriptor's output report length, and so does this.
        std::vector<uint8_t> buffer(output_length_ != 0 ? output_length_ : kHeartbeatSize, 0);
        std::memcpy(buffer.data(), report.data(), std::min(buffer.size(), report.size()));
        OVERLAPPED overlapped{};
        overlapped.hEvent = write_event_;
        ResetEvent(write_event_);
        DWORD written = 0;
        if (!WriteFile(handle_, buffer.data(), static_cast<DWORD>(buffer.size()), &written, &overlapped)) {
            if (GetLastError() != ERROR_IO_PENDING) {
                return false;
            }
            if (WaitForSingleObject(write_event_, 1000) != WAIT_OBJECT_0) {
                CancelIoEx(handle_, &overlapped);
                GetOverlappedResult(handle_, &overlapped, &written, TRUE); // the buffer must outlive the request
                return false;
            }
            if (!GetOverlappedResult(handle_, &overlapped, &written, FALSE)) {
                return false;
            }
        }
        return true;
    }

    void drain() override
    {
        if (handle_ == INVALID_HANDLE_VALUE || input_length_ == 0) {
            return;
        }
        for (int i = 0; i < 64; i++) { // bounded: a board that never stops talking cannot hold the heartbeat
            DWORD n = 0;
            if (!read_pending_) {
                read_overlapped_ = OVERLAPPED{};
                read_overlapped_.hEvent = read_event_;
                ResetEvent(read_event_);
                if (ReadFile(handle_, read_buffer_.data(), static_cast<DWORD>(read_buffer_.size()), &n,
                             &read_overlapped_)) {
                    continue; // a report was waiting: dropped
                }
                if (GetLastError() != ERROR_IO_PENDING) {
                    return;
                }
                read_pending_ = true;
            }
            if (GetOverlappedResult(handle_, &read_overlapped_, &n, FALSE)) {
                read_pending_ = false; // a report arrived while it was pending: dropped, read again
                continue;
            }
            if (GetLastError() != ERROR_IO_INCOMPLETE) {
                read_pending_ = false; // the request failed
            }
            return; // nothing waiting; the request stays pending for the next time
        }
    }

    void close() override
    {
        if (handle_ != INVALID_HANDLE_VALUE) {
            if (read_pending_) {
                CancelIoEx(handle_, &read_overlapped_);
                DWORD n = 0;
                GetOverlappedResult(handle_, &read_overlapped_, &n, TRUE); // the buffer must outlive the request
                read_pending_ = false;
            }
            CloseHandle(handle_);
            handle_ = INVALID_HANDLE_VALUE;
        }
        for (HANDLE* event : {&write_event_, &read_event_}) {
            if (*event != nullptr) {
                CloseHandle(*event);
                *event = nullptr;
            }
        }
    }

private:
    HANDLE handle_ = INVALID_HANDLE_VALUE;
    HANDLE write_event_ = nullptr;
    HANDLE read_event_ = nullptr;
    USHORT output_length_ = 0;
    USHORT input_length_ = 0;
    std::vector<uint8_t> read_buffer_;
    OVERLAPPED read_overlapped_{};
    bool read_pending_ = false;
};

} // namespace

std::unique_ptr<HeartbeatPort> make_hid_port()
{
    return std::make_unique<HidPort>();
}

// --- The thread --------------------------------------------------------------------------------------------------

struct Heartbeat::Impl {
    std::unique_ptr<HeartbeatPort> port;
    std::chrono::milliseconds period;

    mutable std::mutex mutex; // guards the members below, and is what `wake` waits on
    std::condition_variable wake;
    bool stopping = false;
    bool running = false;
    std::string detail;
    std::thread thread;

    std::atomic<uint64_t> sent{0};
    std::atomic<uint64_t> failed{0};

    void send_once()
    {
        if (port->write(heartbeat_report())) {
            sent++;
        } else {
            failed++;
        }
    }

    void run()
    {
        using clock = std::chrono::steady_clock;
        auto next = clock::now() + period;
        std::unique_lock<std::mutex> hold(mutex);
        while (!stopping) {
            wake.wait_until(hold, next);
            if (stopping) {
                break;
            }
            if (clock::now() < next) {
                continue; // woke early
            }
            hold.unlock();
            send_once();
            port->drain();
            hold.lock();
            next = clock::now() + period; // never two reports closer than a period, even after a stall
        }
    }
};

Heartbeat::Heartbeat() : Heartbeat(make_hid_port(), kHeartbeatPeriod) {}

Heartbeat::Heartbeat(std::unique_ptr<HeartbeatPort> port, std::chrono::milliseconds period)
    : impl_(std::make_unique<Impl>())
{
    impl_->port = std::move(port);
    impl_->period = period;
}

Heartbeat::~Heartbeat()
{
    stop();
}

bool Heartbeat::start()
{
    Impl& m = *impl_;
    {
        std::lock_guard<std::mutex> hold(m.mutex);
        if (m.running) {
            return true;
        }
    }
    std::string detail;
    if (!m.port->open(detail)) {
        std::lock_guard<std::mutex> hold(m.mutex);
        m.detail = detail;
        return false;
    }
    // The first report goes out before start() returns, so a caller that then waits for the display has been heard.
    if (!m.port->write(heartbeat_report())) {
        m.failed++;
        m.port->close();
        std::lock_guard<std::mutex> hold(m.mutex);
        m.detail = "the board opened (" + detail + ") but the first report could not be written";
        return false;
    }
    m.sent++;
    std::lock_guard<std::mutex> hold(m.mutex);
    m.detail = detail;
    m.stopping = false;
    m.running = true;
    m.thread = std::thread([&m] { m.run(); });
    return true;
}

void Heartbeat::stop()
{
    Impl& m = *impl_;
    std::thread thread;
    {
        std::lock_guard<std::mutex> hold(m.mutex);
        if (!m.running) {
            return;
        }
        m.stopping = true;
        m.running = false;
        thread = std::move(m.thread);
    }
    m.wake.notify_all();
    if (thread.joinable()) {
        thread.join();
    }
    m.port->close(); // nothing is sent after this
}

bool Heartbeat::running() const
{
    std::lock_guard<std::mutex> hold(impl_->mutex);
    return impl_->running;
}

uint64_t Heartbeat::sent() const
{
    return impl_->sent.load();
}

uint64_t Heartbeat::failed() const
{
    return impl_->failed.load();
}

std::string Heartbeat::detail() const
{
    std::lock_guard<std::mutex> hold(impl_->mutex);
    return impl_->detail;
}

} // namespace dvl
