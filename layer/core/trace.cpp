// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#include "trace.hpp"

#include <algorithm>
#include <cstdio>
#include <utility>

namespace nvdm {

Trace::Trace(size_t capacity, std::function<void()> on_full)
    : records_(new Record[capacity]), capacity_(capacity), on_full_(std::move(on_full))
{
    for (size_t i = 0; i < capacity_; ++i) {
        records_[i].kind.store(0, std::memory_order_relaxed);
    }
}

void Trace::add(TraceKind kind, int64_t t_ns, uint64_t a, int64_t b) noexcept
{
    const size_t i = next_.fetch_add(1, std::memory_order_relaxed);
    if (i >= capacity_) {
        if (i == capacity_ && on_full_) {
            try {
                on_full_();
            } catch (...) {
                // A notice only.
            }
        }
        return;
    }
    Record& r = records_[i];
    r.t_ns = t_ns;
    r.a = a;
    r.b = b;
    r.kind.store(static_cast<uint32_t>(kind), std::memory_order_release);
}

size_t Trace::size() const
{
    return std::min(next_.load(std::memory_order_acquire), capacity_);
}

uint64_t Trace::dropped() const
{
    const size_t n = next_.load(std::memory_order_acquire);
    return n > capacity_ ? static_cast<uint64_t>(n - capacity_) : 0;
}

const Trace::Record& Trace::at(size_t i) const
{
    return records_[i];
}

Trace::Written Trace::append_csv(const std::wstring& path, size_t newest_at_most)
{
    std::lock_guard<std::mutex> hold(write_mutex_);
    // The records finished so far, in order: up to the first one another thread is still writing.
    const size_t held = size();
    size_t end = written_;
    while (end < held && records_[end].kind.load(std::memory_order_acquire) != 0) {
        ++end;
    }
    const size_t from = end - written_ > newest_at_most ? end - newest_at_most : written_;

    Written out;
    if (from < end) {
        FILE* f = nullptr;
        if (_wfopen_s(&f, path.c_str(), header_written_ ? L"ab" : L"wb") != 0 || f == nullptr) {
            return out; // the records wait for the next call
        }
        std::setvbuf(f, nullptr, _IOFBF, 1 << 16);
        if (!header_written_) {
            std::fputs("t_ns,kind,a,b\n", f);
            header_written_ = true;
        }
        for (size_t i = from; i < end; ++i) {
            const Record& r = records_[i];
            std::fprintf(f, "%lld,%u,%llu,%lld\n", static_cast<long long>(r.t_ns),
                         r.kind.load(std::memory_order_acquire), static_cast<unsigned long long>(r.a),
                         static_cast<long long>(r.b));
        }
        out.ok = std::fclose(f) == 0;
    } else {
        out.ok = true;
    }
    out.records = end - from;
    out.skipped = from - written_;
    written_ = end;
    return out;
}

} // namespace nvdm
