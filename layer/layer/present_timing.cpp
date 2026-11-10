// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/present_timing.hpp"

#include <algorithm>
#include <string>
#include <vector>

#include "layer/counted.hpp"
#include "layer/env.hpp"

namespace dvl {
namespace {

// A stage's time in `times`: 0 for a stage the frame has not reached, or one the layer does not report.
uint64_t stage_time(const nvdm::FrameTimes& times, VkPresentStageFlagsEXT stage)
{
    int64_t t = 0;
    switch (stage) {
    case VK_PRESENT_STAGE_QUEUE_OPERATIONS_END_BIT_EXT: t = times.queue_done_ns; break;
    case VK_PRESENT_STAGE_REQUEST_DEQUEUED_BIT_EXT: t = times.dequeued_ns; break;
    case VK_PRESENT_STAGE_IMAGE_FIRST_PIXEL_OUT_BIT_EXT: t = times.on_screen_ns; break;
    default: break;
    }
    return t > 0 ? static_cast<uint64_t>(t) : 0;
}

// Whether any of the stages asked for has a time.
bool any_stage(const nvdm::FrameTimes& times, VkPresentStageFlagsEXT stages)
{
    for (VkPresentStageFlagsEXT bit = 1; bit != 0 && bit <= stages; bit <<= 1) {
        if ((stages & bit) != 0 && stage_time(times, bit) != 0) {
            return true;
        }
    }
    return false;
}

// One result. A complete one lists every stage asked for, 0 where the frame never got there; a partial one the stages
// that have a time so far. As many as fit in pPresentStages (its presentStageCount on the way in).
void fill_result(VkPastPresentationTimingEXT& out, uint64_t present_id, uint64_t target_time, uint64_t domain_id,
                 VkPresentStageFlagsEXT stages, const nvdm::FrameTimes& times, bool complete)
{
    out.presentId = present_id;
    out.targetTime = target_time;
    // A domain id that is not the swapchain's falls back to QPC, which the result then names.
    if (domain_id == kStageLocalTimeDomainId) {
        out.timeDomain = VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT;
        out.timeDomainId = kStageLocalTimeDomainId;
    } else {
        out.timeDomain = VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR;
        out.timeDomainId = kQpcTimeDomainId;
    }
    const uint32_t room = out.pPresentStages != nullptr ? out.presentStageCount : 0;
    uint32_t written = 0;
    for (VkPresentStageFlagsEXT bit = 1; bit != 0 && bit <= stages; bit <<= 1) {
        if ((stages & bit) == 0) {
            continue;
        }
        const uint64_t t = stage_time(times, bit);
        if ((complete || t != 0) && written < room) {
            out.pPresentStages[written++] = VkPresentStageTimeEXT{bit, t};
        }
    }
    out.presentStageCount = written;
    out.reportComplete = complete ? VK_TRUE : VK_FALSE;
}

} // namespace

bool present_at_enabled()
{
    const std::optional<std::string> off = env_get(L"DREAMAIR_VK_LAYER_NO_PRESENT_AT");
    return !off || off->empty() || *off == "0";
}

void fill_present_timing_caps(VkPresentTimingSurfaceCapabilitiesEXT& out, bool present_at)
{
    out.presentTimingSupported = VK_TRUE;
    out.presentAtAbsoluteTimeSupported = present_at ? VK_TRUE : VK_FALSE;
    out.presentAtRelativeTimeSupported = VK_FALSE;
    out.presentStageQueries = kPresentStages;
}

VkResult fill_time_domains(VkSwapchainTimeDomainPropertiesEXT& out)
{
    static constexpr VkTimeDomainKHR kDomains[] = {VK_TIME_DOMAIN_QUERY_PERFORMANCE_COUNTER_KHR,
                                                   VK_TIME_DOMAIN_PRESENT_STAGE_LOCAL_EXT};
    static constexpr uint64_t kIds[] = {kQpcTimeDomainId, kStageLocalTimeDomainId};
    if (out.pTimeDomains == nullptr || out.pTimeDomainIds == nullptr) {
        out.timeDomainCount = 2; // both null asks for the count (one null alone is invalid; taken as asking too)
        return VK_SUCCESS;
    }
    return fill_list(&out.timeDomainCount, out.pTimeDomains, 2, [&](uint32_t i, VkTimeDomainKHR& domain) {
        domain = kDomains[i];
        out.pTimeDomainIds[i] = kIds[i];
    });
}

PresentRequest read_present_request(const VkPresentInfoKHR& info)
{
    PresentRequest r;
    bool id2 = false;
    for (auto* s = static_cast<const VkBaseInStructure*>(info.pNext); s != nullptr; s = s->pNext) {
        switch (s->sType) {
        case VK_STRUCTURE_TYPE_PRESENT_ID_2_KHR: {
            const auto* ids = reinterpret_cast<const VkPresentId2KHR*>(s);
            if (ids->swapchainCount >= 1 && ids->pPresentIds != nullptr) {
                r.present_id = ids->pPresentIds[0];
                id2 = true;
            }
            break;
        }
        case VK_STRUCTURE_TYPE_PRESENT_ID_KHR: {
            const auto* ids = reinterpret_cast<const VkPresentIdKHR*>(s);
            if (!id2 && ids->swapchainCount >= 1 && ids->pPresentIds != nullptr) {
                r.present_id = ids->pPresentIds[0];
            }
            break;
        }
        case VK_STRUCTURE_TYPE_PRESENT_TIMINGS_INFO_EXT: {
            const auto* timings = reinterpret_cast<const VkPresentTimingsInfoEXT*>(s);
            if (timings->swapchainCount >= 1 && timings->pTimingInfos != nullptr) {
                const VkPresentTimingInfoEXT& t = timings->pTimingInfos[0];
                r.timing = true;
                r.timing_flags = t.flags;
                r.target_time = t.targetTime;
                r.time_domain_id = t.timeDomainId;
                r.stages = t.presentStageQueries;
            }
            break;
        }
        default:
            break;
        }
    }
    return r;
}

Target read_target(const PresentRequest& request, int64_t refresh_ns, int64_t now_ns, bool present_at)
{
    Target t;
    if (!request.timing || request.target_time == 0) {
        return t;
    }
    if (!present_at) {
        t.use = Target::Use::Off;
        return t;
    }
    if ((request.timing_flags & VK_PRESENT_TIMING_INFO_PRESENT_AT_RELATIVE_TIME_BIT_EXT) != 0) {
        t.use = Target::Use::Relative;
        return t;
    }
    // Compared as unsigned first: a target past INT64_MAX is far ahead of any QPC time.
    if (now_ns < 0 || request.target_time > static_cast<uint64_t>(now_ns) + kFarTargetNs) {
        t.use = Target::Use::TooFar;
        return t;
    }
    const bool nearest = (request.timing_flags & VK_PRESENT_TIMING_INFO_PRESENT_AT_NEAREST_REFRESH_CYCLE_BIT_EXT) != 0;
    const int64_t early = nearest ? std::max<int64_t>(refresh_ns, 0) / 2 : kTargetToleranceNs;
    t.use = Target::Use::Hold;
    t.not_before_ns = static_cast<int64_t>(request.target_time) - early;
    return t;
}

void PresentTimings::add_swapchain(nvdm::SwapchainGen gen)
{
    swapchains_[gen] = Swapchain{};
}

void PresentTimings::remove_swapchain(nvdm::SwapchainGen gen)
{
    swapchains_.erase(gen);
}

VkResult PresentTimings::set_queue_size(nvdm::SwapchainGen gen, uint32_t size)
{
    const auto it = swapchains_.find(gen);
    if (it == swapchains_.end()) {
        return VK_SUCCESS;
    }
    if (size < it->second.records.size()) {
        return VK_NOT_READY;
    }
    it->second.queue_size = size;
    return VK_SUCCESS;
}

bool PresentTimings::slot_free(nvdm::SwapchainGen gen, const PresentRequest& request) const
{
    if (!request.timing || request.stages == 0) {
        return true;
    }
    const auto it = swapchains_.find(gen);
    return it == swapchains_.end() || it->second.records.size() < it->second.queue_size;
}

void PresentTimings::queued(nvdm::SwapchainGen gen, nvdm::FrameSeq seq, const PresentRequest& request)
{
    const auto it = swapchains_.find(gen);
    if (it == swapchains_.end()) {
        return;
    }
    if (request.timing && request.stages != 0) {
        it->second.records.push_back(Record{seq, request.present_id, request.target_time, request.time_domain_id,
                                            request.stages, nvdm::FrameTimes{}, false});
    }
}

void PresentTimings::not_queued(nvdm::SwapchainGen gen, nvdm::FrameSeq seq)
{
    on_report(nvdm::FrameReport{gen, seq, nvdm::FrameTimes{}});
}

void PresentTimings::on_report(const nvdm::FrameReport& report)
{
    const auto it = swapchains_.find(report.gen);
    if (it == swapchains_.end()) {
        return;
    }
    for (Record& r : it->second.records) {
        if (r.seq == report.seq && !r.complete) {
            r.times = report.times;
            r.complete = true;
            return;
        }
    }
}

VkResult PresentTimings::past(nvdm::SwapchainGen gen, VkPastPresentationTimingFlagsEXT flags,
                              VkPastPresentationTimingPropertiesEXT& out, const LiveTimes& live)
{
    out.timingPropertiesCounter = kTimingPropertiesCounter;
    out.timeDomainsCounter = kTimeDomainsCounter;
    const auto it = swapchains_.find(gen);
    if (it == swapchains_.end()) {
        out.presentationTimingCount = 0;
        return VK_SUCCESS;
    }
    std::deque<Record>& records = it->second.records;
    const bool partial = (flags & VK_PAST_PRESENTATION_TIMING_ALLOW_PARTIAL_RESULTS_BIT_EXT) != 0;
    const bool any_order = (flags & VK_PAST_PRESENTATION_TIMING_ALLOW_OUT_OF_ORDER_RESULTS_BIT_EXT) != 0;

    // The results there are, in present order: complete ones, and partial ones if allowed. In present order, the first
    // record with none holds back every later one.
    struct Ready {
        size_t index;
        nvdm::FrameTimes times;
        bool complete;
    };
    std::vector<Ready> ready;
    for (size_t i = 0; i < records.size(); ++i) {
        const Record& r = records[i];
        if (r.complete) {
            ready.push_back(Ready{i, r.times, true});
            continue;
        }
        if (partial) {
            const nvdm::FrameTimes so_far = live ? live(r.seq).value_or(nvdm::FrameTimes{}) : nvdm::FrameTimes{};
            if (any_stage(so_far, r.stages)) {
                ready.push_back(Ready{i, so_far, false});
                continue;
            }
        }
        if (!any_order) {
            break;
        }
    }

    if (out.pPresentationTimings == nullptr) {
        out.presentationTimingCount = static_cast<uint32_t>(ready.size());
        return VK_SUCCESS;
    }
    const size_t n = std::min<size_t>(out.presentationTimingCount, ready.size());
    for (size_t k = 0; k < n; ++k) {
        const Record& r = records[ready[k].index];
        fill_result(out.pPresentationTimings[k], r.present_id, r.target_time, r.time_domain_id, r.stages,
                    ready[k].times, ready[k].complete);
    }
    // The complete ones written give their slots back (from the back, so the indexes stay right).
    for (size_t k = n; k-- > 0;) {
        if (ready[k].complete) {
            records.erase(records.begin() + static_cast<std::ptrdiff_t>(ready[k].index));
        }
    }
    out.presentationTimingCount = static_cast<uint32_t>(n);
    return n < ready.size() ? VK_INCOMPLETE : VK_SUCCESS;
}

size_t PresentTimings::slots_taken(nvdm::SwapchainGen gen) const
{
    const auto it = swapchains_.find(gen);
    return it == swapchains_.end() ? 0 : it->second.records.size();
}

} // namespace dvl
