// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The layer's log (design 8): levels from DREAMAIR_VK_LAYER_LOG, a new file per process with the last five kept.

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>

#include "layer/log.hpp"

namespace {

namespace fs = std::filesystem;

// A scratch folder that is gone again at the end of the test.
class TempDir {
public:
    TempDir()
    {
        static int counter = 0;
        path_ = fs::temp_directory_path() /
                ("dvl_test_log_" + std::to_string(GetCurrentProcessId()) + "_" + std::to_string(counter++));
        fs::remove_all(path_);
        fs::create_directories(path_);
    }
    ~TempDir()
    {
        std::error_code ec;
        fs::remove_all(path_, ec);
    }
    TempDir(const TempDir&) = delete;
    TempDir& operator=(const TempDir&) = delete;

    const fs::path& path() const { return path_; }
    std::wstring dir() const { return path_.wstring(); }

private:
    fs::path path_;
};

std::string read_file(const fs::path& p)
{
    std::ifstream in(p, std::ios::binary);
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

void write_file(const fs::path& p, const std::string& text)
{
    std::ofstream out(p, std::ios::binary);
    out << text;
}

dvl::LoggerConfig quiet_config(const TempDir& dir, dvl::Level threshold = dvl::Level::Info)
{
    dvl::LoggerConfig c;
    c.dir = dir.dir();
    c.threshold = threshold;
    c.to_stderr = false;
    c.to_debugger = false;
    return c;
}

} // namespace

TEST_CASE("log_level_parsing")
{
    CHECK(dvl::parse_level("error") == dvl::Level::Error);
    CHECK(dvl::parse_level("warn") == dvl::Level::Warn);
    CHECK(dvl::parse_level("warning") == dvl::Level::Warn);
    CHECK(dvl::parse_level("info") == dvl::Level::Info);
    CHECK(dvl::parse_level("debug") == dvl::Level::Debug);
    CHECK(dvl::parse_level("off") == dvl::Level::Off);
    CHECK(dvl::parse_level("DEBUG") == dvl::Level::Debug);
    CHECK(dvl::parse_level("  Info ") == dvl::Level::Info);
    CHECK_FALSE(dvl::parse_level("").has_value());
    CHECK_FALSE(dvl::parse_level("verbose").has_value());
    CHECK_FALSE(dvl::parse_level("infos").has_value());
}

TEST_CASE("a display-session failure line is an error, a warning line a warning, the rest information")
{
    const dvl::SessionLine failure = dvl::session_line(
        "error: Product: the display's EDID could not be read (NVAPI, then the registry) (code -10003)");
    CHECK(failure.level == dvl::Level::Error);
    CHECK(failure.text == "Product: the display's EDID could not be read (NVAPI, then the registry) (code -10003)");

    const dvl::SessionLine warning = dvl::session_line("warning: untested driver version 630.00; trying it anyway");
    CHECK(warning.level == dvl::Level::Warn);
    CHECK(warning.text == "untested driver version 630.00; trying it anyway");

    const dvl::SessionLine info = dvl::session_line("EDID route: NVAPI");
    CHECK(info.level == dvl::Level::Info);
    CHECK(info.text == "EDID route: NVAPI");
    CHECK(dvl::session_line("errors: not a prefix").level == dvl::Level::Info);
}

TEST_CASE("a display-session failure line is written with DREAMAIR_VK_LAYER_LOG=warn, its information lines are not")
{
    TempDir dir;
    {
        dvl::Logger logger(quiet_config(dir, *dvl::parse_level("warn")));
        for (const char* line : {"EDID route: NVAPI", "error: Holders: Pimax or SteamVR is running; stop it first"}) {
            const dvl::SessionLine s = dvl::session_line(line);
            logger.write(s.level, s.text);
        }
    }
    const std::string text = read_file(dir.path() / "layer.log");
    CHECK(text.find("] error Holders: Pimax or SteamVR is running; stop it first") != std::string::npos);
    CHECK(text.find("EDID route") == std::string::npos);
}

TEST_CASE("trace_folder_is_off_the_log_folder_or_another")
{
    // DREAMAIR_VK_LAYER_TRACE: unset, empty or 0 is off; 1 is the log's folder; anything else is a folder.
    const std::wstring log_dir = L"C:\\Users\\finn\\AppData\\Local\\DreamAirVkLayer";
    const auto folder = [&](std::optional<std::string> setting) { return dvl::trace_folder(setting, log_dir); };

    CHECK_FALSE(folder(std::nullopt).has_value());
    CHECK_FALSE(folder("").has_value());
    CHECK_FALSE(folder("0").has_value());
    CHECK_FALSE(folder(" 0 ").has_value());
    CHECK_FALSE(folder("  ").has_value());

    REQUIRE(folder("1").has_value());
    CHECK(*folder("1") == log_dir);
    CHECK(*folder("1 ") == log_dir); // set in a cmd file with a blank after it

    REQUIRE(folder("D:\\traces").has_value());
    CHECK(*folder("D:\\traces") == L"D:\\traces");
    CHECK(*folder("10") == L"10"); // a folder, not 1
    CHECK(*folder("00") == L"00"); // not 0 either
    CHECK(*folder("D:\\sp\xC3\xA4t") == L"D:\\sp\u00E4t");

    // 1 with no log folder (no %LOCALAPPDATA%): on, but with no folder to put the file in; the caller says so.
    const std::optional<std::wstring> nowhere = dvl::trace_folder(std::string("1"), L"");
    REQUIRE(nowhere.has_value());
    CHECK(nowhere->empty());
}

TEST_CASE("log_rotation_keeps_the_last_five")
{
    TempDir dir;
    write_file(dir.path() / "layer.log", "0");
    for (int i = 1; i <= 4; i++) {
        write_file(dir.path() / ("layer." + std::to_string(i) + ".log"), std::to_string(i));
    }
    dvl::rotate_logs(dir.dir(), 5);

    CHECK_FALSE(fs::exists(dir.path() / "layer.log"));
    // Each file moved one place up; the oldest is gone.
    CHECK(read_file(dir.path() / "layer.1.log") == "0");
    CHECK(read_file(dir.path() / "layer.2.log") == "1");
    CHECK(read_file(dir.path() / "layer.3.log") == "2");
    CHECK(read_file(dir.path() / "layer.4.log") == "3");
    CHECK_FALSE(fs::exists(dir.path() / "layer.5.log"));
}

TEST_CASE("log_rotation_with_gaps_and_a_missing_folder")
{
    TempDir dir;
    write_file(dir.path() / "layer.log", "new");
    write_file(dir.path() / "layer.2.log", "old");
    dvl::rotate_logs(dir.dir(), 5);
    CHECK(read_file(dir.path() / "layer.1.log") == "new");
    CHECK(read_file(dir.path() / "layer.3.log") == "old");
    CHECK_FALSE(fs::exists(dir.path() / "layer.log"));

    // Nothing there: nothing happens, and nothing throws.
    dvl::rotate_logs((dir.path() / "missing").wstring(), 5);

    // Keeping one file: the previous one goes.
    write_file(dir.path() / "layer.log", "x");
    dvl::rotate_logs(dir.dir(), 1);
    CHECK_FALSE(fs::exists(dir.path() / "layer.log"));
}

TEST_CASE("logger_writes_lines_at_or_above_the_level")
{
    TempDir dir;
    {
        dvl::Logger logger(quiet_config(dir, dvl::Level::Info));
        logger.write(dvl::Level::Error, "an error line");
        logger.write(dvl::Level::Warn, "a warning line");
        logger.write(dvl::Level::Info, "an info line");
        logger.write(dvl::Level::Debug, "a debug line");
    }
    const std::string text = read_file(dir.path() / "layer.log");
    CHECK(text.find("an error line") != std::string::npos);
    CHECK(text.find("a warning line") != std::string::npos);
    CHECK(text.find("an info line") != std::string::npos);
    CHECK(text.find("a debug line") == std::string::npos);
    // The level is named and the line says which process wrote it.
    CHECK(text.find("error") != std::string::npos);
    CHECK(text.find("warn") != std::string::npos);
    CHECK(text.find("pid " + std::to_string(GetCurrentProcessId())) != std::string::npos);
    // One line per message.
    CHECK(std::count(text.begin(), text.end(), '\n') == 3);
}

TEST_CASE("logger_off_makes_no_file")
{
    TempDir dir;
    {
        dvl::Logger logger(quiet_config(dir, dvl::Level::Off));
        logger.write(dvl::Level::Error, "dropped");
    }
    CHECK_FALSE(fs::exists(dir.path() / "layer.log"));

    // A quiet level makes no file until something is written at it.
    {
        dvl::Logger logger(quiet_config(dir, dvl::Level::Warn));
        logger.write(dvl::Level::Info, "dropped");
    }
    CHECK_FALSE(fs::exists(dir.path() / "layer.log"));
}

TEST_CASE("logger_starts_a_new_file_for_each_process")
{
    TempDir dir;
    for (int process = 1; process <= 7; process++) {
        dvl::Logger logger(quiet_config(dir));
        logger.write(dvl::Level::Info, "process " + std::to_string(process));
    }
    // The newest is layer.log, then layer.1.log ... layer.4.log; processes 1 and 2 are gone.
    for (int i = 0; i <= 4; i++) {
        const fs::path p = dir.path() / (i == 0 ? "layer.log" : "layer." + std::to_string(i) + ".log");
        INFO(p.string());
        REQUIRE(fs::exists(p));
        CHECK(read_file(p).find("process " + std::to_string(7 - i)) != std::string::npos);
    }
    CHECK_FALSE(fs::exists(dir.path() / "layer.5.log"));
}

TEST_CASE("logger_append_leaves_the_newest_file_in_place")
{
    // A process the guard declines must not push real logs out (design 5: the guard is a safety net).
    TempDir dir;
    {
        dvl::Logger first(quiet_config(dir));
        first.write(dvl::Level::Info, "from the real run");
    }
    {
        dvl::Logger stray(quiet_config(dir));
        stray.write(dvl::Level::Warn, "from a declined process", /*rotate=*/false);
    }
    const std::string text = read_file(dir.path() / "layer.log");
    CHECK(text.find("from the real run") != std::string::npos);
    CHECK(text.find("from a declined process") != std::string::npos);
    CHECK_FALSE(fs::exists(dir.path() / "layer.1.log"));
}

TEST_CASE("logger_cuts_a_very_long_line")
{
    TempDir dir;
    {
        dvl::Logger logger(quiet_config(dir));
        logger.write(dvl::Level::Info, std::string(20000, 'x'));
        logger.write(dvl::Level::Info, "after");
    }
    const std::string text = read_file(dir.path() / "layer.log");
    CHECK(text.size() < 8000);
    CHECK(text.find("after") != std::string::npos);
}

TEST_CASE("logger_names_its_folder")
{
    TempDir dir;
    dvl::Logger logger(quiet_config(dir));
    CHECK(logger.dir() == dir.dir());

    dvl::Logger without{dvl::LoggerConfig{}};
    CHECK(without.dir().empty());
}

TEST_CASE("logger_without_a_folder_still_works")
{
    // No %LOCALAPPDATA%: no file, no failure.
    dvl::LoggerConfig c;
    c.threshold = dvl::Level::Info;
    c.to_stderr = false;
    c.to_debugger = false;
    dvl::Logger logger(c);
    logger.write(dvl::Level::Info, "nowhere to go");
    SUCCEED();
}
