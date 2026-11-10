// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "check/report.hpp"

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>

#include "core/holders.hpp"

namespace dvl {
namespace {

std::string joined(const std::vector<std::string>& names)
{
    std::string text;
    for (const std::string& name : names) {
        text += text.empty() ? "" : ", ";
        text += name;
    }
    return text;
}

char lower(char c)
{
    return c >= 'A' && c <= 'Z' ? static_cast<char>(c - 'A' + 'a') : c;
}

bool same_name_ignoring_case(const std::string& a, const char* b)
{
    size_t i = 0;
    for (; i < a.size() && b[i] != '\0'; i++) {
        if (lower(a[i]) != lower(b[i])) {
            return false;
        }
    }
    return i == a.size() && b[i] == '\0';
}

std::string format(_Printf_format_string_ const char* format, ...)
{
    char buffer[512];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    return buffer;
}

uint64_t tolerance(uint64_t expected)
{
    return std::max<uint64_t>(3, (expected + 99) / 100);
}

} // namespace

// --- Output ------------------------------------------------------------------------------------------------------

void StdoutOutput::line(const std::string& text)
{
    std::lock_guard<std::mutex> hold(mutex_);
    std::fputs(text.c_str(), stdout);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

void CaptureOutput::line(const std::string& text)
{
    std::lock_guard<std::mutex> hold(mutex_);
    lines_.push_back(text);
}

std::vector<std::string> CaptureOutput::lines() const
{
    std::lock_guard<std::mutex> hold(mutex_);
    return lines_;
}

std::string CaptureOutput::text() const
{
    std::lock_guard<std::mutex> hold(mutex_);
    std::string text;
    for (const std::string& line : lines_) {
        text += line;
        text += '\n';
    }
    return text;
}

void say(Output& out, _Printf_format_string_ const char* format, ...)
{
    char buffer[2048];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    out.line(buffer);
}

// --- Holder checks -----------------------------------------------------------------------------------------------

HolderFindings holder_findings(const std::vector<std::string>& processes)
{
    HolderFindings findings;
    if (processes.empty()) {
        // A machine always runs something: an empty list is a failed snapshot, and Pimax cannot be ruled out.
        findings.list_unreadable = true;
        findings.blockers.push_back("the process list cannot be read, so Pimax cannot be ruled out");
        return findings;
    }
    const auto display = nvdm::running_holders(nvdm::Held::Display, processes);
    findings.display_held = !display.empty();
    if (!display.empty()) {
        findings.blockers.push_back("the display (direct mode) is held by " + joined(display) +
                                    ": Pimax or SteamVR is running");
    }
    const auto board = nvdm::running_holders(nvdm::Held::Board, processes);
    findings.board_held = !board.empty();
    if (!board.empty()) {
        findings.blockers.push_back("the board and the eye tracker are held by " + joined(board) +
                                    ": Pimax is running");
    }
    const auto monado = nvdm::running_holders(nvdm::Held::Monado, processes);
    findings.monado_running = !monado.empty();
    if (!monado.empty()) {
        findings.blockers.push_back("Monado (" + joined(monado) + ") is running and holds the display, the board and the tracker");
    }
    const bool rtss = std::any_of(processes.begin(), processes.end(),
                                  [](const std::string& p) { return same_name_ignoring_case(p, "RTSS.exe"); });
    if (rtss) {
        findings.warnings.push_back(
            "RTSS.exe is running: its injected hooks can draw an overlay into the headset image; give the program "
            "that presents (this tool, monado-service.exe) an RTSS profile with application detection off");
    }
    return findings;
}

// --- The verdict -------------------------------------------------------------------------------------------------

uint64_t expected_flips(double seconds)
{
    return seconds <= 0 ? 0 : static_cast<uint64_t>(std::llround(kRefreshHz * seconds));
}

Verdict judge(bool steps_ok, const std::string& failed_step, const RunCounts& c)
{
    Verdict verdict;
    if (!steps_ok) {
        verdict.line = "FAIL: " + failed_step;
        return verdict;
    }

    std::vector<std::string> reasons;
    if (c.requested_seconds > 0 && c.seconds < 0.98 * c.requested_seconds) {
        reasons.push_back(format("stopped after %.1f s of the requested %.0f s", c.seconds, c.requested_seconds));
    }
    const uint64_t expected = expected_flips(c.seconds);
    const uint64_t slack = tolerance(expected);
    auto out_of_range = [&](uint64_t n) { return n + slack < expected || n > expected + slack; };
    if (out_of_range(c.presents)) {
        reasons.push_back(format("%llu presents in %.1f s, expected %llu +-%llu", static_cast<unsigned long long>(c.presents),
                                 c.seconds, static_cast<unsigned long long>(expected),
                                 static_cast<unsigned long long>(slack)));
    }
    if (out_of_range(c.flips)) {
        reasons.push_back(format("%llu flips in %.1f s, expected %llu +-%llu", static_cast<unsigned long long>(c.flips),
                                 c.seconds, static_cast<unsigned long long>(expected),
                                 static_cast<unsigned long long>(slack)));
    }
    if (c.failures > 0) {
        reasons.push_back(format("%llu presents failed, were never confirmed or timed out in present-wait",
                                 static_cast<unsigned long long>(c.failures)));
    }
    if (c.have_events) {
        const uint64_t total = c.events_ok + c.events_timeout;
        if (c.events_timeout > std::max<uint64_t>(2, total / 100)) {
            reasons.push_back(format("%llu of %llu display event waits timed out",
                                     static_cast<unsigned long long>(c.events_timeout),
                                     static_cast<unsigned long long>(total)));
        }
    }

    if (!reasons.empty()) {
        verdict.line = "FAIL: ";
        for (size_t i = 0; i < reasons.size(); i++) {
            verdict.line += (i ? "; " : "") + reasons[i];
        }
        return verdict;
    }

    verdict.pass = true;
    verdict.line = format("PASS: %llu presents, %llu flips in %.1f s (%.1f Hz)", static_cast<unsigned long long>(c.presents),
                          static_cast<unsigned long long>(c.flips), c.seconds,
                          c.seconds > 0 ? static_cast<double>(c.flips) / c.seconds : 0.0);
    if (c.have_events) {
        verdict.line += format(", display events %llu ok / %llu timed out", static_cast<unsigned long long>(c.events_ok),
                               static_cast<unsigned long long>(c.events_timeout));
    }
    if (c.link_drops > 0) {
        verdict.line += format(", %llu link drops recovered", static_cast<unsigned long long>(c.link_drops));
    }
    return verdict;
}

// --- Lines about the session and the layer ------------------------------------------------------------------------

std::vector<std::string> describe_open_result(const nvdm::OpenResult& result)
{
    std::vector<std::string> lines;
    for (int s = static_cast<int>(nvdm::OpenStep::Holders); s < static_cast<int>(nvdm::OpenStep::Done); s++) {
        const auto step = static_cast<nvdm::OpenStep>(s);
        const bool failed_here = !result.ok && step == result.step;
        const bool reached = result.ok || step <= result.step;
        if (!reached) {
            break;
        }
        std::string line = std::string(nvdm::step_name(step)) + " ";
        line.resize(std::max<size_t>(line.size(), 18), ' ');
        line += failed_here ? "FAILED: " + result.message : "ok";
        lines.push_back(std::move(line));
        if (failed_here) {
            break;
        }
    }
    return lines;
}

std::string driver_version_from_lines(const std::vector<std::string>& lines)
{
    constexpr const char key[] = "driver version ";
    for (const std::string& line : lines) {
        const size_t at = line.find(key);
        // "driver version 621.01" and "warning: untested driver version 700.12; trying it anyway ...". Not "the driver
        // version could not be read".
        if (at == std::string::npos) {
            continue;
        }
        size_t from = at + sizeof(key) - 1;
        size_t to = from;
        while (to < line.size() && ((line[to] >= '0' && line[to] <= '9') || line[to] == '.')) {
            to++;
        }
        if (to > from) {
            return line.substr(from, to - from);
        }
    }
    return {};
}

std::vector<std::string> explain_failure(const std::string& line)
{
    const size_t colon = line.find(": ");
    const std::string step = colon == std::string::npos ? std::string() : line.substr(0, colon);
    bool known = false;
    for (int s = static_cast<int>(nvdm::OpenStep::Holders); s < static_cast<int>(nvdm::OpenStep::Done); s++) {
        known = known || step == nvdm::step_name(static_cast<nvdm::OpenStep>(s));
    }
    if (!known) {
        return {};
    }
    if (step == nvdm::step_name(nvdm::OpenStep::Product)) {
        if (line.find("unknown display product") == std::string::npos) { // no EDID: unreadable, or skipped (a test)
            return {"what it means: the layer cannot tell which headset this is. No EDID (the display's description of "
                    "itself) could be read through NVAPI or the registry, and the line says why the Dream Air could "
                    "not be assumed instead",
                    "what to do: connect the Dream Air alone, by DisplayPort and USB, check that its board (USB "
                    "34A4:0044) is listed in Device Manager, and run this tool again; if it still fails, send this "
                    "output to the maintainer (README, 'Reporting a driver version')"};
        }
        return {std::string("what it means: the headset's EDID names a product this layer does not know; it drives "
                            "the ") +
                    nvdm::kDreamAir.name + " only",
                "what to do: if this headset is a Dream Air, send this output to the maintainer"};
    }
    return {"what to do: see 'Troubleshooting' in the README, the line that starts with '" + step + ":'"};
}

std::optional<std::string> edid_route_from_text(const std::string& text)
{
    constexpr const char key[] = "EDID route: ";
    const size_t at = text.rfind(key);
    if (at == std::string::npos) {
        return std::nullopt;
    }
    const size_t from = at + sizeof(key) - 1;
    size_t to = from;
    while (to < text.size() && text[to] != '\r' && text[to] != '\n' && text[to] != ' ') {
        to++;
    }
    if (to == from) {
        return std::nullopt;
    }
    return text.substr(from, to - from);
}

namespace {

// The last line of `text` that contains `key`, without its line ending.
std::optional<std::string> last_line_containing(const std::string& text, const std::string& key)
{
    const size_t at = text.rfind(key);
    if (at == std::string::npos) {
        return std::nullopt;
    }
    const size_t begin = text.find_last_of('\n', at);
    const size_t from = begin == std::string::npos ? 0 : begin + 1;
    size_t to = text.find_first_of("\r\n", at);
    if (to == std::string::npos) {
        to = text.size();
    }
    return text.substr(from, to - from);
}

// The lines of `text` the layer stamped with process `pid` ("[pid 1234 tid 5678]", layer/log.cpp), each ended by '\n'.
std::string lines_of_process(const std::string& text, unsigned long pid)
{
    const std::string stamp = "[pid " + std::to_string(pid) + " tid ";
    std::string out;
    size_t from = 0;
    while (from < text.size()) {
        size_t to = text.find('\n', from);
        if (to == std::string::npos) {
            to = text.size();
        }
        std::string line = text.substr(from, to - from);
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        if (line.find(stamp) != std::string::npos) {
            out += line;
            out += '\n';
        }
        from = to + 1;
    }
    return out;
}

} // namespace

LayerLogFacts layer_log_facts(const std::string& log_text, unsigned long pid)
{
    const std::string ours = lines_of_process(log_text, pid);
    LayerLogFacts facts;
    facts.edid_route = edid_route_from_text(ours);
    facts.trial_import = last_line_containing(ours, "trial import on device");
    facts.memory_fallback = last_line_containing(ours, "memory type comes from the image's memoryTypeBits");
    // The stamp ends "] <level padded to 5> ", so an error line has "] error " before its text.
    constexpr const char error[] = "] error ";
    if (const std::optional<std::string> line = last_line_containing(ours, error)) {
        facts.failure = line->substr(line->find(error) + sizeof(error) - 1);
    }
    return facts;
}

std::string nvidia_driver_text(uint32_t v)
{
    return format("%u.%02u", static_cast<unsigned>(v >> 22), static_cast<unsigned>((v >> 14) & 0xff));
}

std::vector<std::string> diagnose_missing_layer(const LayerFacts& facts)
{
    std::vector<std::string> advice;
    if (!facts.layer_listed) {
        advice.push_back(std::string("the Vulkan loader does not know the layer ") + kLayerName +
                         ": install it (install.ps1, run as administrator), or start this tool with VK_IMPLICIT_LAYER_PATH=<the "
                         "NVIDIA driver's nv-vk64.json>;<path of dreamair_vk_layer.json>");
        if (facts.elevated) {
            advice.push_back("this process is elevated, and the loader ignores VK_IMPLICIT_LAYER_PATH and the per-user "
                             "registry in elevated processes: start the tool unelevated, or install the layer");
        }
        return advice;
    }
    if (facts.disable_var_set) {
        advice.push_back("DISABLE_DREAMAIR_VK_LAYER is set in this environment, which switches the layer off: clear it");
        return advice;
    }
    advice.push_back(std::string("the layer ") + kLayerName +
                     " is registered but the loader did not offer its VK_EXT_display_surface_counter: its manifest "
                     "and DLL do not match this build (run with VK_LOADER_DEBUG=layer to see why it was skipped)");
    return advice;
}

} // namespace dvl
