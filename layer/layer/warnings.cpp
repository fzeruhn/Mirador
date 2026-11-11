// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "layer/warnings.hpp"

#include <utility>

namespace dvl {
namespace {

const char* mode_name(VkPresentModeKHR mode)
{
    switch (mode) {
    case VK_PRESENT_MODE_IMMEDIATE_KHR: return "IMMEDIATE";
    case VK_PRESENT_MODE_MAILBOX_KHR: return "MAILBOX";
    case VK_PRESENT_MODE_FIFO_KHR: return "FIFO";
    case VK_PRESENT_MODE_FIFO_RELAXED_KHR: return "FIFO_RELAXED";
    default: return "an unknown one";
    }
}

std::string count_text(uint64_t n)
{
    return std::to_string(static_cast<unsigned long long>(n));
}

} // namespace

void Warnings::set_display_control(bool enabled)
{
    display_control_ = enabled;
}

void Warnings::set_present_mode(VkPresentModeKHR mode)
{
    present_mode_ = mode;
}

void Warnings::set_memory_fallback(bool used)
{
    memory_fallback_ = used;
}

void Warnings::fire(Kind kind, std::string line)
{
    if (fired_.insert(kind).second) {
        lines_.push_back(std::move(line));
    }
}

void Warnings::begin_session()
{
    in_session_ = true;
    presents_ = 0;
    saw_event_ = false;
    saw_present_wait_ = false;
    fired_.clear();
    if (!display_control_) {
        fire(Kind::NoDisplayControl,
             "the device was created without VK_EXT_display_control: Monado runs no vblank thread, so its pacer is not "
             "kept on the headset's refresh (Monado needs VK_EXT_display_surface_counter on its instance)");
    }
    if (present_mode_ != VK_PRESENT_MODE_FIFO_KHR) {
        fire(Kind::PresentMode, std::string("the swapchain's present mode is ") + mode_name(present_mode_) +
                                    ", not FIFO: it is paced as FIFO (one frame per refresh)");
    }
    if (memory_fallback_) {
        fire(Kind::MemoryFallback,
             "the back-buffer import took its memory type from the image alone: "
             "vkGetMemoryWin32HandlePropertiesKHR gave none for the back-buffers");
    }
}

void Warnings::on_present(int64_t now_ns)
{
    if (!in_session_ || now_ns - last_present_ns_ > kAppSessionGapNs) {
        begin_session();
    }
    last_present_ns_ = now_ns;
    if (++presents_ == kCheckAfterPresents) {
        if (!saw_event_) {
            fire(Kind::NoDisplayEvent,
                 "no vkRegisterDisplayEventEXT in the first 90 presents of this app session: Monado's vblank thread is "
                 "not running, so its pacer is not kept on the headset's refresh");
        }
        if (!saw_present_wait_) {
            fire(Kind::NoPresentWait,
                 "no vkWaitForPresentKHR in the first 90 presents of this app session: set "
                 "XRT_COMPOSITOR_USE_PRESENT_WAIT=1 for Monado");
        }
    }
}

void Warnings::on_present_wait()
{
    saw_present_wait_ = true;
}

void Warnings::on_event_registered(uint64_t key, int64_t now_ns)
{
    saw_event_ = true;
    unwaited_[key] = now_ns;
}

void Warnings::on_event_waited(uint64_t key)
{
    unwaited_.erase(key);
}

void Warnings::on_event_gone(uint64_t)
{
    // Not forgotten if no wait reached the layer first: Monado always waits before it destroys (design 6.9), so a
    // destroy alone means its wait went past the layer. check() warns about it at 2 s, and then forgets it.
}

void Warnings::check(int64_t now_ns)
{
    bool overdue = false;
    for (auto it = unwaited_.begin(); it != unwaited_.end();) {
        if (now_ns - it->second >= kEventWaitNs) {
            overdue = true;
            it = unwaited_.erase(it); // each event counts once
        } else {
            ++it;
        }
    }
    if (overdue) {
        fire(Kind::EventNotWaited,
             "a display event was registered and no wait on its fence reached the layer within 2 s: Monado's fence "
             "calls may not be reaching the layer (a layer above it?), so its vblank thread gets no refreshes");
    }
}

void Warnings::on_counters(const nvdm::FrameModel::Counters& counters, bool flips_stopped)
{
    if (flips_stopped) {
        fire(Kind::FlipsStopped,
             "flips stopped in this app session: the headset's refresh counter did not move for 600 ms");
    }
    if (counters.dropped > counters_.dropped) {
        fire(Kind::Dropped, "frames dropped in this app session (" + count_text(counters.dropped) + " in all)");
    }
    if (counters.failed > counters_.failed) {
        fire(Kind::Failed, "NVAPI Presents failed in this app session (" + count_text(counters.failed) + " in all)");
    }
    if (counters.unconfirmed > counters_.unconfirmed) {
        fire(Kind::Unconfirmed, "Presents unconfirmed (never seen on screen) in this app session (" +
                                    count_text(counters.unconfirmed) + " in all)");
    }
    if (counters.reserved > counters_.reserved) {
        fire(Kind::Reserved, "back-buffers reserved after a Present of unknown outcome in this app session (" +
                                 count_text(counters.reserved) + " in all)");
    }
    counters_ = counters;
}

void Warnings::on_fence_fallback()
{
    fire(Kind::FenceFallback, "no frame fence was free for a present, so it waited for its queue to go idle "
                              "(vkQueueWaitIdle) instead");
}

void Warnings::on_thread_left_behind()
{
    fire(Kind::ThreadLeftBehind,
         "a display thread was left behind inside an NVAPI call at teardown; it exits when the call returns");
}

std::vector<std::string> Warnings::take_lines()
{
    std::vector<std::string> out;
    out.swap(lines_);
    return out;
}

} // namespace dvl
