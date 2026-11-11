// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

// VK_EXT_present_timing on the layer's swapchain (design 6.11), from the frame model's per-frame times
// (FrameModel::set_report), whose first-pixel-out time is the NVAPI flip: the stats' QPC time of the refresh that put
// the frame on screen. No Vulkan call and no lock of its own: the intercepts (present.cpp,
// present_timing_intercepts.cpp) hold the display-session lock around every call on PresentTimings, and the frame model
// reports under it.

#include <vulkan/vulkan.h>

#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <map>
#include <optional>

#include "core/frame_model.hpp"

namespace dvl {

// The time domains of a swapchain of the layer's (vkGetSwapchainTimeDomainPropertiesEXT), by id. Both are QPC
// nanoseconds, the frame model's timeline and Monado's clock on Windows (os_monotonic_get_ns): the QPC domain, and the
// present-stage-local domain every swapchain has to offer, which here is the same clock for every stage. Neither ever
// changes, so the swapchain's counters stay at 1.
inline constexpr uint64_t kQpcTimeDomainId = 1;
inline constexpr uint64_t kStageLocalTimeDomainId = 2;
inline constexpr uint64_t kTimingPropertiesCounter = 1;
inline constexpr uint64_t kTimeDomainsCounter = 1;

// The stages a swapchain of the layer's reports: the end of the present's queue operations (its frame fence seen
// signalled), the request dequeued (its NVAPI Present started) and the first pixel out (the flip). Not the first pixel
// visible: the panel's latency is not known to the layer.
inline constexpr VkPresentStageFlagsEXT kPresentStages = VK_PRESENT_STAGE_QUEUE_OPERATIONS_END_BIT_EXT |
                                                         VK_PRESENT_STAGE_REQUEST_DEQUEUED_BIT_EXT |
                                                         VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT;

// Present at a time (design 6.11): on unless MIRADOR_DIRECT_MODE_NO_PRESENT_AT is set to a value other than 0, for an A/B
// of the same build with and without target times. Read when called; a swapchain keeps what it read at creation.
bool present_at_enabled();

// The layer's surface (VkPresentTimingSurfaceCapabilitiesEXT): timings of kPresentStages, present at an absolute time
// when `present_at`, and never at a relative one.
void fill_present_timing_caps(VkPresentTimingSurfaceCapabilitiesEXT& out, bool present_at);

// How close to its target a flip may be and still count as at or after it, for a present without
// PRESENT_AT_NEAREST_REFRESH_CYCLE: the period's rounding and the flips' jitter (~0.1 ms) must not cost a refresh.
inline constexpr int64_t kTargetToleranceNs = 1'000'000;

// The time domains (kQpcTimeDomainId, kStageLocalTimeDomainId), in the two-call idiom of
// vkGetSwapchainTimeDomainPropertiesEXT: both arrays null asks for the count.
VkResult fill_time_domains(VkSwapchainTimeDomainPropertiesEXT& out);

// What a present on the layer's swapchain (one swapchain, entry 0 of every array) asks for in its pNext chain.
struct PresentRequest {
    uint64_t present_id = 0; // VkPresentId2KHR's, else VkPresentIdKHR's; 0 for none
    // VkPresentTimingsInfoEXT's entry, if one is chained with a timing info.
    bool timing = false;
    VkPresentTimingInfoFlagsEXT timing_flags = 0;
    uint64_t target_time = 0;
    uint64_t time_domain_id = 0;
    VkPresentStageFlagsEXT stages = 0; // presentStageQueries
};
PresentRequest read_present_request(const VkPresentInfoKHR& info);

// A target further ahead of its present than this is not held for: it would hold the back-buffer past acquire's bound
// (600 ms, design 6.6), which then drops the frame, and on a headset it is a wrong clock, not a wish.
inline constexpr int64_t kFarTargetNs = 500'000'000;

// What a present's target time asks of the display thread (design 6.11).
struct Target {
    enum class Use {
        None,     // no target (no timing info, or targetTime 0): shown at the first flip it can make, as always
        Hold,     // not shown at a flip before not_before_ns (FrameModel::hold)
        Off,      // a target, but present at a time is off (MIRADOR_DIRECT_MODE_NO_PRESENT_AT): taken as none
        Relative, // PRESENT_AT_RELATIVE_TIME, which the surface does not offer: taken as none
        TooFar,   // more than kFarTargetNs after now_ns: taken as none
    } use = Use::None;
    int64_t not_before_ns = 0;
};
// The target time is QPC nanoseconds in either of the swapchain's domains (an unknown id is read as QPC, as the results
// name it), read against the flip, the first pixel out, which is the stage the layer times, whatever stage
// targetTimeDomainPresentStage names. The flip may come up to half a refresh period before it with
// PRESENT_AT_NEAREST_REFRESH_CYCLE, else up to kTargetToleranceNs before it. A target in the past holds nothing (the
// first flip the frame can make is after it).
Target read_target(const PresentRequest& request, int64_t refresh_ns, int64_t now_ns, bool present_at);

// The present timing state of the layer's swapchains, by frame-model generation (design 6.11). Every call is made under
// the display-session lock.
//
// A present that asks for stages (presentStageQueries non-zero) takes a slot of its swapchain's results queue, whose
// size the application sets (vkSetSwapchainPresentTimingQueueSizeEXT; 0 until it does), and holds it until
// vkGetPastPresentationTimingEXT has returned its complete results. A frame's results are complete once the frame model
// has reported it: on screen, or ended without. A stage the frame never reached reads 0 ("not available"). Results come
// in present order unless the application allows any order, and partial ones only if it allows them.
class PresentTimings {
public:
    void add_swapchain(nvdm::SwapchainGen gen);
    void remove_swapchain(nvdm::SwapchainGen gen);

    // vkSetSwapchainPresentTimingQueueSizeEXT: VK_NOT_READY, and nothing changed, if more slots are taken than `size`.
    VkResult set_queue_size(nvdm::SwapchainGen gen, uint32_t size);
    // A present with `request` can be made: it asks for no stages, or a slot is free. Otherwise it fails with
    // VK_ERROR_PRESENT_TIMING_QUEUE_FULL_EXT. An unknown `gen` has room.
    bool slot_free(nvdm::SwapchainGen gen, const PresentRequest& request) const;
    // The frame `seq` of `gen` is queued with `request`: called before FrameModel::queue, which may report it at once.
    void queued(nvdm::SwapchainGen gen, nvdm::FrameSeq seq, const PresentRequest& request);
    // FrameModel::queue did not take it (its frame was gone), so it is never reported: its results end with no times.
    void not_queued(nvdm::SwapchainGen gen, nvdm::FrameSeq seq);
    // FrameModel::set_report's callback. A report for a generation removed already, or for a frame that asked for no
    // stages, is dropped.
    void on_report(const nvdm::FrameReport& report);

    // vkGetPastPresentationTimingEXT for `gen`, as the extension describes it. `live` gives the times so far of a frame
    // the model has not reported yet (FrameModel::frame_times), for partial results.
    using LiveTimes = std::function<std::optional<nvdm::FrameTimes>(nvdm::FrameSeq)>;
    VkResult past(nvdm::SwapchainGen gen, VkPastPresentationTimingFlagsEXT flags,
                  VkPastPresentationTimingPropertiesEXT& out, const LiveTimes& live);

    // The results-queue slots `gen` holds.
    size_t slots_taken(nvdm::SwapchainGen gen) const;

private:
    struct Record {
        nvdm::FrameSeq seq;
        uint64_t present_id;
        uint64_t target_time;
        uint64_t time_domain_id;
        VkPresentStageFlagsEXT stages;
        nvdm::FrameTimes times;
        bool complete; // reported (or never to be): its times are final
    };
    struct Swapchain {
        uint32_t queue_size = 0;
        std::deque<Record> records; // present order; each holds a slot until its complete results are read
    };

    std::map<nvdm::SwapchainGen, Swapchain> swapchains_;
};

} // namespace dvl
