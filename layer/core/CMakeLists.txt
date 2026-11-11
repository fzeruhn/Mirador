# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

# nvdm: everything that is not Vulkan. The NVAPI calls behind an interface, the display session, the frame model and
# the display loop. Tasks add their sources here as they bring them.
add_library(nvdm STATIC
    board.cpp
    clock.cpp
    display_loop.cpp
    display_session.cpp
    display_thread.cpp
    fence_pool.cpp
    frame_model.cpp
    holders.cpp
    nvapi_api.cpp
    product_table.cpp
    edid.cpp
    stats.cpp
    trace.cpp
)

# Consumers include the headers as "core/api.hpp", from the repository root. Inside core/ they include each other
# by file name.
target_include_directories(nvdm PUBLIC "${PROJECT_SOURCE_DIR}")
target_compile_features(nvdm PUBLIC cxx_std_17)

# advapi32: the registry EDID route (RegGetValueW and friends). d3d11 and dxgi: the session's D3D11 device on the
# display's adapter. avrt: the display thread's MMCSS priority. setupapi: the board check of the last EDID route.
target_link_libraries(nvdm PUBLIC advapi32 avrt d3d11 dxgi setupapi)
