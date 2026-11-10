// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <sal.h>

#include <memory>
#include <optional>
#include <string>
#include <string_view>

namespace dvl {

// What a line is worth. MIRADOR_DIRECT_MODE_LOG sets the lowest level that is written: "error", "warn", "info"
// (the default) or "debug"; "off" writes nothing and makes no file. Off is a setting only, never the level of a line.
enum class Level { Off, Error, Warn, Info, Debug };

// Case-insensitive, blanks around the word ignored; nullopt for anything else.
std::optional<Level> parse_level(std::string_view text);
const char* level_name(Level level);

// A line of the display session's (core/display_session.hpp) as the layer logs it: "error: " starts the failure of a
// step (Error), "warning: " a warning (Warn), anything else is information (Info). The text is the line without that
// prefix: the log names the level itself.
struct SessionLine {
    Level level;
    std::string_view text;
};
SessionLine session_line(std::string_view line);

// MIRADOR_DIRECT_MODE_TRACE (per-frame timing records, a debugging aid): the folder the trace file goes to, or nullopt
// for off. Unset, empty or "0" is off; "1" is the log's folder (`log_dir`); anything else is that folder. Blanks around
// the value are ignored. An empty folder (a "1" with no log folder, a value that is not UTF-8) is on, with nowhere to
// write.
std::optional<std::wstring> trace_folder(const std::optional<std::string>& setting, const std::wstring& log_dir);

// The last this many log files stay in the folder: layer.log (the newest), layer.1.log, ... (design 8).
inline constexpr int kLogsKept = 5;

// Starts a new log: layer.log becomes layer.1.log, layer.1.log becomes layer.2.log, and so on; the one that would
// become layer.<keep>.log is deleted. Files and folders that are not there or are in use are skipped without a
// word: the log must never stop the layer.
void rotate_logs(const std::wstring& dir, int keep = kLogsKept);

struct LoggerConfig {
    std::wstring dir; // empty: no file
    Level threshold = Level::Info;
    bool to_stderr = true;
    bool to_debugger = true; // OutputDebugStringA
    int keep = kLogsKept;
};

// Writes lines to <dir>\layer.log, to stderr (it shows in Monado's console) and to the debugger. The file is made at
// the first line that passes the level, so a quiet process leaves nothing behind. One lock; safe from any thread.
class Logger {
public:
    explicit Logger(LoggerConfig config);
    ~Logger();
    Logger(const Logger&) = delete;
    Logger& operator=(const Logger&) = delete;

    // One line. `rotate`: when this is the process's first line, start a new file (rotate_logs) rather than add to
    // the newest one. Lines longer than a few thousand characters are cut.
    void write(Level level, std::string_view message, bool rotate = true);

    Level threshold() const;
    // The folder of its files; empty if it writes none.
    const std::wstring& dir() const;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

// The process's logger: %LOCALAPPDATA%\Mirador\direct-mode\layer.log, the level from MIRADOR_DIRECT_MODE_LOG, read at the
// first line. It is never destroyed (no destructors that take a lock at process exit, design 6.10).
Logger& process_logger();

// printf-style lines on the process's logger.
void log(Level level, _Printf_format_string_ const char* format, ...);
// Like log(), but a process that has not logged yet adds to the newest file instead of starting a new one. For the
// line of a process that is not ours to log in (the guard's decline): a stray MIRADOR_DIRECT_MODE must not push the
// real runs' logs out of the last five.
void log_append(Level level, _Printf_format_string_ const char* format, ...);

// Tests only (the test build's dvl_test_set_log_sink, layer/test_hooks.cpp): while a sink is set, every line of log()
// and log_append(), at any level, goes to it instead of the process's logger, on the thread that logs it. Null puts
// the logger back; once this returns, no call to the old sink is in progress.
using LogSink = void (*)(Level level, const char* line, void* context);
void set_log_sink_for_tests(LogSink sink, void* context);

} // namespace dvl
