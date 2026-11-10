// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/log.hpp"

#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>

#include "layer/env.hpp"

namespace dvl {
namespace {

constexpr size_t kMaxLine = 4000;

char lower(char c)
{
    return (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c;
}

bool equal_ignoring_case(std::string_view a, std::string_view b)
{
    if (a.size() != b.size()) {
        return false;
    }
    for (size_t i = 0; i < a.size(); i++) {
        if (lower(a[i]) != lower(b[i])) {
            return false;
        }
    }
    return true;
}

std::wstring log_name(const std::wstring& dir, int index)
{
    return dir + L"\\layer" + (index > 0 ? L"." + std::to_wstring(index) : std::wstring()) + L".log";
}

void write_all(HANDLE handle, const std::string& text)
{
    const char* p = text.data();
    size_t left = text.size();
    while (left > 0) {
        DWORD wrote = 0;
        if (!WriteFile(handle, p, static_cast<DWORD>(left), &wrote, nullptr) || wrote == 0) {
            return;
        }
        p += wrote;
        left -= wrote;
    }
}

} // namespace

std::optional<Level> parse_level(std::string_view text)
{
    constexpr std::string_view blanks = " \t\r\n";
    const size_t first = text.find_first_not_of(blanks);
    if (first == std::string_view::npos) {
        return std::nullopt;
    }
    text = text.substr(first, text.find_last_not_of(blanks) - first + 1);
    if (equal_ignoring_case(text, "off")) {
        return Level::Off;
    }
    if (equal_ignoring_case(text, "error")) {
        return Level::Error;
    }
    if (equal_ignoring_case(text, "warn") || equal_ignoring_case(text, "warning")) {
        return Level::Warn;
    }
    if (equal_ignoring_case(text, "info")) {
        return Level::Info;
    }
    if (equal_ignoring_case(text, "debug")) {
        return Level::Debug;
    }
    return std::nullopt;
}

std::optional<std::wstring> trace_folder(const std::optional<std::string>& setting, const std::wstring& log_dir)
{
    if (!setting) {
        return std::nullopt;
    }
    constexpr std::string_view blanks = " \t\r\n";
    std::string_view text = *setting;
    const size_t first = text.find_first_not_of(blanks);
    if (first == std::string_view::npos) {
        return std::nullopt;
    }
    text = text.substr(first, text.find_last_not_of(blanks) - first + 1);
    if (text == "0") {
        return std::nullopt;
    }
    if (text == "1") {
        return log_dir;
    }
    return from_utf8(text);
}

SessionLine session_line(std::string_view line)
{
    constexpr std::string_view error = "error: ";
    constexpr std::string_view warning = "warning: ";
    if (line.substr(0, error.size()) == error) {
        return SessionLine{Level::Error, line.substr(error.size())};
    }
    if (line.substr(0, warning.size()) == warning) {
        return SessionLine{Level::Warn, line.substr(warning.size())};
    }
    return SessionLine{Level::Info, line};
}

const char* level_name(Level level)
{
    switch (level) {
    case Level::Off: return "off";
    case Level::Error: return "error";
    case Level::Warn: return "warn";
    case Level::Info: return "info";
    case Level::Debug: return "debug";
    }
    return "?";
}

void rotate_logs(const std::wstring& dir, int keep)
{
    if (dir.empty() || keep < 1) {
        return;
    }
    if (keep == 1) {
        DeleteFileW(log_name(dir, 0).c_str());
        return;
    }
    // layer.<keep-1>.log is the oldest that stays; the file that would take its place goes first.
    DeleteFileW(log_name(dir, keep - 1).c_str());
    for (int i = keep - 2; i >= 0; i--) {
        MoveFileExW(log_name(dir, i).c_str(), log_name(dir, i + 1).c_str(), MOVEFILE_REPLACE_EXISTING);
    }
}

struct Logger::Impl {
    explicit Impl(LoggerConfig c) : config(std::move(c)) {}
    ~Impl()
    {
        if (file != INVALID_HANDLE_VALUE) {
            CloseHandle(file);
        }
    }
    Impl(const Impl&) = delete;
    Impl& operator=(const Impl&) = delete;

    // Opens the file at the first line that is written. Failing once, it does not try again.
    void open_file(bool rotate)
    {
        file_tried = true;
        if (config.dir.empty()) {
            return;
        }
        // The folder is two levels below %LOCALAPPDATA%, which exists: Mirador's folder first.
        if (const size_t slash = config.dir.find_last_of(L'\\'); slash != std::wstring::npos) {
            CreateDirectoryW(config.dir.substr(0, slash).c_str(), nullptr);
        }
        CreateDirectoryW(config.dir.c_str(), nullptr);
        if (rotate) {
            rotate_logs(config.dir, config.keep);
        }
        // Append-only and shared with everyone, so a second process can rotate this file away (rename it) or add a
        // line (log_append) while this one writes on.
        file = CreateFileW(log_name(config.dir, 0).c_str(), FILE_APPEND_DATA,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    }

    LoggerConfig config;
    SRWLOCK lock = SRWLOCK_INIT;
    HANDLE file = INVALID_HANDLE_VALUE;
    bool file_tried = false;
};

Logger::Logger(LoggerConfig config) : impl_(std::make_unique<Impl>(std::move(config))) {}

Logger::~Logger() = default;

Level Logger::threshold() const
{
    return impl_->config.threshold;
}

const std::wstring& Logger::dir() const
{
    return impl_->config.dir;
}

void Logger::write(Level level, std::string_view message, bool rotate)
{
    if (level == Level::Off || level > impl_->config.threshold) {
        return;
    }
    std::string text(message.substr(0, kMaxLine));
    if (message.size() > kMaxLine) {
        text += " ...";
    }

    SYSTEMTIME t;
    GetLocalTime(&t);
    char stamp[96];
    std::snprintf(stamp, sizeof(stamp), "%04u-%02u-%02u %02u:%02u:%02u.%03u [pid %lu tid %lu] %-5s ", t.wYear,
                  t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentProcessId(),
                  GetCurrentThreadId(), level_name(level));
    const std::string file_line = std::string(stamp) + text + "\n";
    // The console and the debugger get the short form: no date, no ids.
    const std::string short_line = std::string("[dvl] ") + level_name(level) + ": " + text + "\n";

    AcquireSRWLockExclusive(&impl_->lock);
    if (!impl_->file_tried) {
        impl_->open_file(rotate);
    }
    if (impl_->file != INVALID_HANDLE_VALUE) {
        write_all(impl_->file, file_line);
    }
    if (impl_->config.to_stderr) {
        HANDLE err = GetStdHandle(STD_ERROR_HANDLE);
        if (err != nullptr && err != INVALID_HANDLE_VALUE) {
            write_all(err, short_line);
        }
    }
    ReleaseSRWLockExclusive(&impl_->lock);
    if (impl_->config.to_debugger) {
        OutputDebugStringA(short_line.c_str());
    }
}

namespace {

LoggerConfig config_from_environment()
{
    LoggerConfig c;
    const auto local = env_get(L"LOCALAPPDATA");
    if (local && !local->empty()) {
        c.dir = from_utf8(*local) + L"\\Mirador\\direct-mode";
    }
    if (const auto text = env_get(L"MIRADOR_DIRECT_MODE_LOG")) {
        if (const auto level = parse_level(*text)) {
            c.threshold = *level;
        }
    }
    return c;
}

// The tests' sink (set_log_sink_for_tests). Plain data, constant-initialised, never destroyed. The flag spares every
// line the lock while no sink is set, which is always in the shipped layer.
struct SinkSlot {
    SRWLOCK lock = SRWLOCK_INIT;
    LogSink sink = nullptr;
    void* context = nullptr;
};
SinkSlot g_sink;
std::atomic<bool> g_sink_set{false};

// The line to the sink, if one is set: true if it took it.
bool to_sink(Level level, const char* format, va_list args)
{
    if (!g_sink_set.load(std::memory_order_acquire)) {
        return false;
    }
    struct Shared {
        Shared() { AcquireSRWLockShared(&g_sink.lock); }
        ~Shared() { ReleaseSRWLockShared(&g_sink.lock); }
        Shared(const Shared&) = delete;
        Shared& operator=(const Shared&) = delete;
    } hold;
    if (g_sink.sink == nullptr) {
        return false;
    }
    char buffer[kMaxLine + 16];
    if (std::vsnprintf(buffer, sizeof(buffer), format, args) < 0) {
        std::snprintf(buffer, sizeof(buffer), "%s", "(log line could not be formatted)");
    }
    g_sink.sink(level, buffer, g_sink.context);
    return true;
}

void vlog(Level level, bool rotate, const char* format, va_list args) noexcept
{
    // The log must never stop the layer: a failure to log (out of memory) is dropped, not thrown into a Vulkan call.
    try {
        if (to_sink(level, format, args)) {
            return;
        }
        Logger& logger = process_logger();
        if (level == Level::Off || level > logger.threshold()) {
            return;
        }
        char buffer[kMaxLine + 16];
        const int n = std::vsnprintf(buffer, sizeof(buffer), format, args);
        if (n < 0) {
            logger.write(level, "(log line could not be formatted)", rotate);
            return;
        }
        const size_t length = static_cast<size_t>(n) < sizeof(buffer) ? static_cast<size_t>(n) : sizeof(buffer) - 1;
        logger.write(level, std::string_view(buffer, length), rotate);
    } catch (...) {
    }
}

} // namespace

Logger& process_logger()
{
    // Made at first use and kept: a static Logger would run its destructor at process exit, when a thread that holds
    // the lock may already be killed.
    static Logger* const logger = new Logger(config_from_environment());
    return *logger;
}

void log(Level level, const char* format, ...)
{
    va_list args;
    va_start(args, format);
    vlog(level, true, format, args);
    va_end(args);
}

void log_append(Level level, const char* format, ...)
{
    va_list args;
    va_start(args, format);
    vlog(level, false, format, args);
    va_end(args);
}

void set_log_sink_for_tests(LogSink sink, void* context)
{
    AcquireSRWLockExclusive(&g_sink.lock);
    g_sink.sink = sink;
    g_sink.context = context;
    g_sink_set.store(sink != nullptr, std::memory_order_release);
    ReleaseSRWLockExclusive(&g_sink.lock);
}

} // namespace dvl
