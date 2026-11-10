// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "edid.hpp"

#include <algorithm>
#include <iterator>

namespace nvdm {

namespace {

constexpr size_t kBlock = 128;
constexpr uint8_t kCtaTag = 0x02;
constexpr uint8_t kDisplayIdTag = 0x70;
constexpr uint8_t kTypeITiming = 0x03;   // DisplayID 1.x detailed timing, pixel clock in 10 kHz
constexpr uint8_t kTypeVIITiming = 0x22; // DisplayID 2.x detailed timing, pixel clock in 1 kHz
constexpr size_t kTimingSize = 20;
constexpr uint32_t kMicrosoftOui = 0xca125c;

bool has_header(const std::vector<uint8_t>& edid)
{
    static const uint8_t kHeader[8] = {0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00};
    return edid.size() >= kBlock && std::equal(std::begin(kHeader), std::end(kHeader), edid.begin());
}

// The extension blocks there are bytes for.
size_t block_count(const std::vector<uint8_t>& edid)
{
    return edid.size() / kBlock;
}

std::optional<EdidMode> mode_of(uint64_t clock_hz, uint32_t width, uint32_t height, uint32_t h_total, uint32_t v_total)
{
    if (clock_hz == 0 || width == 0 || height == 0 || h_total == 0 || v_total == 0) {
        return std::nullopt;
    }
    const uint64_t pixels = static_cast<uint64_t>(h_total) * v_total;
    return EdidMode{width, height, static_cast<uint32_t>((clock_hz * 1000 + pixels / 2) / pixels)};
}

// The base block's first detailed timing descriptor (offset 54), when it is a timing (a pixel clock other than 0).
std::optional<EdidMode> base_timing(const std::vector<uint8_t>& edid)
{
    const uint8_t* d = edid.data() + 54;
    const uint32_t clock_10khz = d[0] | d[1] << 8;
    if (clock_10khz == 0) {
        return std::nullopt;
    }
    const uint32_t h_active = d[2] | (d[4] & 0xf0) << 4;
    const uint32_t h_blank = d[3] | (d[4] & 0x0f) << 8;
    const uint32_t v_active = d[5] | (d[7] & 0xf0) << 4;
    const uint32_t v_blank = d[6] | (d[7] & 0x0f) << 8;
    return mode_of(static_cast<uint64_t>(clock_10khz) * 10'000, h_active, v_active, h_active + h_blank, v_active + v_blank);
}

struct DisplayIdTiming {
    EdidMode mode;
    bool preferred;
};

// Every Type I and Type VII timing of the DisplayID extension blocks, in order.
std::vector<DisplayIdTiming> displayid_timings(const std::vector<uint8_t>& edid)
{
    std::vector<DisplayIdTiming> timings;
    for (size_t b = 1; b < block_count(edid); b++) {
        const uint8_t* block = edid.data() + b * kBlock;
        if (block[0] != kDisplayIdTag) {
            continue;
        }
        // Byte 2 is the length of the data blocks, which start at byte 5; the last byte is the checksum.
        const size_t end = std::min<size_t>(5 + block[2], kBlock - 1);
        size_t off = 5;
        while (off + 3 <= end) {
            const uint8_t tag = block[off];
            const size_t length = block[off + 2];
            if (tag == 0 && length == 0) {
                break; // padding
            }
            if (off + 3 + length > end) {
                break; // a length past the section: nothing more of this block is trusted
            }
            if ((tag == kTypeITiming || tag == kTypeVIITiming) && length % kTimingSize == 0) {
                const uint64_t clock_unit = tag == kTypeITiming ? 10'000 : 1'000;
                for (size_t t = off + 3; t < off + 3 + length; t += kTimingSize) {
                    const uint8_t* d = block + t;
                    const uint64_t clock = (static_cast<uint64_t>(d[0] | d[1] << 8 | d[2] << 16) + 1) * clock_unit;
                    const uint32_t h_active = (d[4] | d[5] << 8) + 1u;
                    const uint32_t h_blank = (d[6] | d[7] << 8) + 1u;
                    const uint32_t v_active = (d[12] | d[13] << 8) + 1u;
                    const uint32_t v_blank = (d[14] | d[15] << 8) + 1u;
                    if (auto mode = mode_of(clock, h_active, v_active, h_active + h_blank, v_active + v_blank)) {
                        timings.push_back({*mode, (d[3] & 0x80) != 0});
                    }
                }
            }
            off += 3 + length;
        }
    }
    return timings;
}

} // namespace

std::optional<EdidMode> preferred_mode(const std::vector<uint8_t>& edid)
{
    if (!has_header(edid)) {
        return std::nullopt;
    }
    const std::vector<DisplayIdTiming> timings = displayid_timings(edid);
    for (const DisplayIdTiming& timing : timings) {
        if (timing.preferred) {
            return timing.mode;
        }
    }
    if (auto mode = base_timing(edid)) {
        return mode;
    }
    if (!timings.empty()) {
        return timings.front().mode;
    }
    return std::nullopt;
}

bool has_microsoft_hmd_block(const std::vector<uint8_t>& edid)
{
    if (!has_header(edid)) {
        return false;
    }
    for (size_t b = 1; b < block_count(edid); b++) {
        const uint8_t* block = edid.data() + b * kBlock;
        if (block[0] != kCtaTag) {
            continue;
        }
        // Data blocks run from byte 4 to the offset of the first detailed timing (byte 2).
        const size_t end = std::min<size_t>(block[2], kBlock - 1);
        size_t off = 4;
        while (off < end) {
            const uint8_t tag = block[off] >> 5;
            const size_t length = block[off] & 0x1f;
            if (off + 1 + length > end) {
                break;
            }
            if (tag == 3 && length >= 3) {
                const uint32_t oui = block[off + 1] | block[off + 2] << 8 | block[off + 3] << 16;
                if (oui == kMicrosoftOui) {
                    return true;
                }
            }
            off += 1 + length;
        }
    }
    return false;
}

} // namespace nvdm
