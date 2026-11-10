// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <vulkan/vulkan.h>

#include <algorithm>
#include <cstdint>

namespace dvl {

// The two-call idiom of Vulkan's list queries: a null `out` asks for the count; otherwise up to *count entries are
// written, *count is set to the number written, and VK_INCOMPLETE says there were more.

// A list the layer makes itself, of `total` entries. `fill(i, item)` writes entry i into an item the caller provided
// (for the ...2KHR structs it writes the payload and keeps sType and pNext).
template <class T, class Fill>
VkResult fill_list(uint32_t* count, T* out, uint32_t total, Fill&& fill)
{
    if (out == nullptr) {
        *count = total;
        return VK_SUCCESS;
    }
    const uint32_t n = std::min(*count, total);
    for (uint32_t i = 0; i < n; i++) {
        fill(i, out[i]);
    }
    *count = n;
    return n < total ? VK_INCOMPLETE : VK_SUCCESS;
}

// The layer's `ours` entries first, then the layer below's list. `down(count, out)` is the same query passed down,
// with the same idiom. An error from it is returned as it is.
template <class T, class Fill, class Down>
VkResult list_ours_first(uint32_t* count, T* out, uint32_t ours, Fill&& fill, Down&& down)
{
    uint32_t below = 0;
    const VkResult counted = down(&below, static_cast<T*>(nullptr));
    if (counted < 0) {
        return counted;
    }
    const uint32_t total = ours + below;
    if (out == nullptr) {
        *count = total;
        return VK_SUCCESS;
    }
    const uint32_t room = std::min(*count, total);
    const uint32_t own = std::min(room, ours);
    for (uint32_t i = 0; i < own; i++) {
        fill(i, out[i]);
    }
    uint32_t written = own;
    VkResult result = room < total ? VK_INCOMPLETE : VK_SUCCESS;
    if (room > ours) {
        uint32_t theirs = room - ours;
        const VkResult r = down(&theirs, out + ours);
        if (r < 0) {
            return r;
        }
        written += theirs;
        if (r == VK_INCOMPLETE) {
            result = VK_INCOMPLETE; // the list below grew between the two calls
        }
    }
    *count = written;
    return result;
}

} // namespace dvl
