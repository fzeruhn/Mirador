// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

// The per-frame timing trace (core/trace.hpp): a fixed array any thread appends to without a lock, and its CSV file.

#include <catch2/catch_test_macros.hpp>

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <fstream>
#include <iterator>
#include <set>
#include <string>
#include <thread>

#include "core/trace.hpp"

namespace {

using nvdm::Trace;
using nvdm::TraceKind;

constexpr char kHeader[] = "t_ns,kind,a,b\n";

// A file in the temp folder, gone again at the end of the test.
class TempCsv {
public:
    TempCsv()
    {
        static int counter = 0;
        wchar_t dir[MAX_PATH];
        REQUIRE(GetTempPathW(MAX_PATH, dir) != 0);
        dir_ = dir;
        path_ = dir_ + L"dvl_trace_test_" + std::to_wstring(GetCurrentProcessId()) + L"_" +
                std::to_wstring(counter++) + L".csv";
        DeleteFileW(path_.c_str());
    }
    ~TempCsv() { DeleteFileW(path_.c_str()); }
    TempCsv(const TempCsv&) = delete;
    TempCsv& operator=(const TempCsv&) = delete;

    const std::wstring& path() const { return path_; }
    // A path in a folder that is not there.
    std::wstring unwritable() const { return dir_ + L"dvl_no_such_folder\\x.csv"; }
    std::string text() const
    {
        std::ifstream in(path_, std::ios::binary);
        return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    }

private:
    std::wstring dir_;
    std::wstring path_;
};

// Two threads appending at once, `each` records apiece: thread 0 numbers its records (the `a` column) 0..each-1,
// thread 1 each..2*each-1.
void append_from_two_threads(Trace& trace, size_t each)
{
    std::atomic<bool> go{false};
    const auto work = [&](uint64_t first) {
        while (!go.load()) {
            std::this_thread::yield();
        }
        for (size_t i = 0; i < each; ++i) {
            trace.add(TraceKind::Counter, static_cast<int64_t>(i), first + i, 0);
        }
    };
    std::thread a(work, uint64_t{0});
    std::thread b(work, static_cast<uint64_t>(each));
    go.store(true);
    a.join();
    b.join();
}

} // namespace

TEST_CASE("Trace: a fixed array any thread appends to, written as CSV")
{
    Trace trace(3);
    trace.add(TraceKind::Acquire, 100, 7, 2);
    trace.add(TraceKind::Credit, 200, 7, -5);
    trace.add(TraceKind::Counter, 300, 9, 250);
    trace.add(TraceKind::Counter, 400, 10, 350); // full: dropped, counted
    CHECK(trace.size() == 3);
    CHECK(trace.dropped() == 1);
    CHECK(trace.at(1).t_ns == 200);
    CHECK(trace.at(1).b == -5);

    TempCsv csv;
    const Trace::Written written = trace.append_csv(csv.path());
    CHECK(written.ok);
    CHECK(written.records == 3);
    CHECK(written.skipped == 0);
    CHECK(csv.text() == "t_ns,kind,a,b\n100,1,7,2\n200,32,7,-5\n300,31,9,250\n");
}

TEST_CASE("Trace: a write adds only the records added since the last one")
{
    Trace trace(10);
    TempCsv csv;
    trace.add(TraceKind::Acquire, 100, 1, 0);
    trace.add(TraceKind::Acquire, 200, 2, 0);
    Trace::Written written = trace.append_csv(csv.path());
    CHECK(written.records == 2);
    CHECK(csv.text() == std::string(kHeader) + "100,1,1,0\n200,1,2,0\n");

    trace.add(TraceKind::Credit, 300, 3, 0);
    written = trace.append_csv(csv.path());
    CHECK(written.ok);
    CHECK(written.records == 1); // not the two before it again
    CHECK(csv.text() == std::string(kHeader) + "100,1,1,0\n200,1,2,0\n300,32,3,0\n"); // one header

    written = trace.append_csv(csv.path()); // nothing new
    CHECK(written.ok);
    CHECK(written.records == 0);
    CHECK(csv.text() == std::string(kHeader) + "100,1,1,0\n200,1,2,0\n300,32,3,0\n");
}

TEST_CASE("Trace: a file that cannot be written keeps its records for the next write")
{
    Trace trace(10);
    TempCsv csv;
    trace.add(TraceKind::Acquire, 100, 1, 0);
    trace.add(TraceKind::Acquire, 200, 2, 0);

    Trace::Written written = trace.append_csv(csv.unwritable());
    CHECK_FALSE(written.ok);
    CHECK(written.records == 0);

    written = trace.append_csv(csv.path());
    CHECK(written.ok);
    CHECK(written.records == 2);
    CHECK(csv.text() == std::string(kHeader) + "100,1,1,0\n200,1,2,0\n");
}

TEST_CASE("Trace: a bound writes the newest records and counts the older ones it leaves out")
{
    // The console handler gives its hook 100 ms: it writes the newest records only (layer/swapchain.cpp).
    Trace trace(10);
    TempCsv csv;
    for (uint64_t i = 0; i < 5; ++i) {
        trace.add(TraceKind::Acquire, static_cast<int64_t>(100 * (i + 1)), i, 0);
    }
    Trace::Written written = trace.append_csv(csv.path(), 2);
    CHECK(written.ok);
    CHECK(written.records == 2);
    CHECK(written.skipped == 3);
    CHECK(csv.text() == std::string(kHeader) + "400,1,3,0\n500,1,4,0\n");

    // The ones left out stay out; the next write goes on from the newest.
    trace.add(TraceKind::Acquire, 600, 5, 0);
    written = trace.append_csv(csv.path());
    CHECK(written.records == 1);
    CHECK(written.skipped == 0);
    CHECK(csv.text() == std::string(kHeader) + "400,1,3,0\n500,1,4,0\n600,1,5,0\n");
}

TEST_CASE("Trace: the first record that does not fit calls on_full, once")
{
    std::atomic<int> full{0};
    Trace trace(3, [&full] { ++full; });
    for (int i = 0; i < 3; ++i) {
        trace.add(TraceKind::Acquire, i, 0, 0);
    }
    CHECK(full == 0); // the array is full, but nothing was dropped yet
    trace.add(TraceKind::Acquire, 3, 0, 0);
    CHECK(full == 1);
    for (int i = 0; i < 5; ++i) {
        trace.add(TraceKind::Acquire, i, 0, 0);
    }
    CHECK(full == 1);
    CHECK(trace.dropped() == 6);
}

TEST_CASE("Trace: a callback that throws does not leave add")
{
    Trace trace(1, [] { throw 1; });
    trace.add(TraceKind::Acquire, 1, 0, 0);
    trace.add(TraceKind::Acquire, 2, 0, 0); // calls it
    CHECK(trace.dropped() == 1);
}

TEST_CASE("Trace: two threads appending at once lose nothing")
{
    constexpr size_t kEach = 20'000;
    Trace trace(2 * kEach);
    append_from_two_threads(trace, kEach);

    CHECK(trace.size() + trace.dropped() == 2 * kEach);
    CHECK(trace.size() == 2 * kEach);
    std::set<uint64_t> numbers;
    for (size_t i = 0; i < trace.size(); ++i) {
        CHECK(trace.at(i).kind.load() != 0); // every record that is held was finished
        numbers.insert(trace.at(i).a);
    }
    CHECK(numbers.size() == 2 * kEach); // and none was written over by the other thread
}

TEST_CASE("Trace: two threads appending to an array that fills up lose nothing but the overflow")
{
    constexpr size_t kEach = 2'000;
    constexpr size_t kCapacity = 1'000;
    std::atomic<int> full{0};
    Trace trace(kCapacity, [&full] { ++full; });
    append_from_two_threads(trace, kEach);

    CHECK(trace.size() == kCapacity);
    CHECK(trace.dropped() == 2 * kEach - kCapacity);
    CHECK(trace.size() + trace.dropped() == 2 * kEach);
    CHECK(full == 1); // however many threads dropped records, the notice is made once
    std::set<uint64_t> numbers;
    for (size_t i = 0; i < trace.size(); ++i) {
        CHECK(trace.at(i).kind.load() != 0);
        numbers.insert(trace.at(i).a);
    }
    CHECK(numbers.size() == kCapacity);

    TempCsv csv;
    const Trace::Written written = trace.append_csv(csv.path());
    CHECK(written.ok);
    CHECK(written.records == kCapacity);
}
