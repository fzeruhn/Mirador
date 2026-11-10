// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "check/report.hpp"

#include <windows.h>

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>

#include "core/edid.hpp"
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

uint64_t expected_flips(double seconds, double refresh_hz)
{
    return seconds <= 0 || refresh_hz <= 0 ? 0 : static_cast<uint64_t>(std::llround(refresh_hz * seconds));
}

Verdict judge(bool steps_ok, const std::string& failed_step, const RunCounts& c)
{
    Verdict verdict;
    if (!steps_ok && failed_step.find(nvdm::kWindowsDrivesIt) != std::string::npos) {
        // The step-aside: Windows drives this headset, so this driver's direct mode is neither shown working nor broken.
        verdict.not_used = true;
        verdict.line = "NOT USED: " + failed_step;
        return verdict;
    }
    if (!steps_ok) {
        verdict.line = "FAIL: " + failed_step;
        return verdict;
    }

    std::vector<std::string> reasons;
    if (c.requested_seconds > 0 && c.seconds < 0.98 * c.requested_seconds) {
        reasons.push_back(format("stopped after %.1f s of the requested %.0f s", c.seconds, c.requested_seconds));
    }
    const uint64_t expected = expected_flips(c.seconds, c.refresh_hz);
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
        if (line.find(nvdm::kWindowsDrivesIt) != std::string::npos) {
            return {"what it means: Windows drives this headset itself, through its own route for headsets (its EDID "
                    "says so), so the layer leaves it alone and direct mode is not used for it",
                    "what to do: nothing for direct mode; if the headset should run in direct mode instead, send this "
                    "output to the maintainer"};
        }
        if (line.find("can't be trusted to be the connected headset's") != std::string::npos) {
            return {"what it means: NVAPI could not read the connected display's EDID, and the one Windows kept from "
                    "an earlier connection names a headset this layer has not been tested with, so its mode may be "
                    "another headset's",
                    "what to do: update the NVIDIA driver, connect the headset alone and run this tool again; if it "
                    "still fails, send this output, with the driver version, to the maintainer"};
        }
        if (line.find("unknown display product") == std::string::npos) { // no EDID: unreadable, or skipped (a test)
            return {"what it means: the layer cannot tell which headset this is. No EDID (the display's description of "
                    "itself) could be read through NVAPI or the registry, and the line says why the Dream Air could "
                    "not be assumed instead",
                    "what to do: connect the Dream Air alone, by DisplayPort and USB, check that its board (USB "
                    "34A4:0044) is listed in Device Manager, and run this tool again; if it still fails, send this "
                    "output, with the driver version, to the maintainer"};
        }
        return {std::string("what it means: the headset's EDID names a product this layer does not know: it drives "
                            "the ") +
                    nvdm::kDreamAir.name + ", and other headsets of a direct-mode vendor whose EDID gives their mode",
                "what to do: if this headset should run in direct mode, send this output to the maintainer"};
    }
    using nvdm::OpenStep;
    const auto is = [&](OpenStep s) { return step == nvdm::step_name(s); };
    if (is(OpenStep::Holders)) {
        return {"what to do: quit Pimax Play and SteamVR and stop the PiServiceLauncher service (Mirador does this when "
                "it starts Monado), then try again"};
    }
    if (is(OpenStep::LoadNvapi) || is(OpenStep::D3D11)) {
        return {"what to do: install or update NVIDIA's driver; AMD and Intel GPUs are not supported"};
    }
    if (is(OpenStep::Enumerate)) {
        return {"what to do: check that the headset is switched on and connected by DisplayPort and USB, and that "
                "nothing else holds it; unplug and replug it, then try again"};
    }
    if (is(OpenStep::Luid)) {
        return {"what to do: connect the headset to the NVIDIA GPU Monado uses (on a laptop, the port wired to it)"};
    }
    if (is(OpenStep::EnableDirectMode) || is(OpenStep::BindDevice)) {
        return {"what to do: another program holds the display (Pimax, SteamVR or a second Monado): stop it and try "
                "again; if it persists, restart Windows"};
    }
    // CheckIds (the driver moved a call), CreateBackBuffers and SetDisplayMode (the driver refused the mode).
    return {"what to do: this NVIDIA driver doesn't do what the layer needs: send this output, with the driver "
            "version, to the maintainer"};
}

} // namespace dvl
