# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

# dvl_check_lib: everything of the check tool but main(). Linked into mirador-display-check and into dvl_tests, so the
# tests run the heartbeat, the argument parser, the verdict and (against a fake Vulkan chain) the --vulkan mode
# directly. It reaches Vulkan only through the pointers vkGetInstanceProcAddr gives it, never through vulkan-1.lib.
add_library(dvl_check_lib STATIC
    heartbeat.cpp
    interrupt.cpp
    nvapi_mode.cpp
    options.cpp
    report.cpp
    vulkan_mode.cpp
)
target_include_directories(dvl_check_lib PUBLIC "${PROJECT_SOURCE_DIR}")
target_compile_features(dvl_check_lib PUBLIC cxx_std_17)
target_compile_definitions(dvl_check_lib PUBLIC VK_NO_PROTOTYPES)
# setupapi and hid: finding and writing to the board. d3d11 and dxgi come with nvdm (the NVAPI mode clears the
# back-buffers through a D3D11 device of its own).
target_link_libraries(dvl_check_lib PUBLIC nvdm Vulkan::Headers setupapi hid)

# mirador-display-check.exe: the name matters. The layer's process guard lets exactly this executable name (and
# monado-service.exe) load it (design 5).
add_executable(mirador-display-check main.cpp)
target_link_libraries(mirador-display-check PRIVATE dvl_check_lib)
target_compile_definitions(mirador-display-check PRIVATE "DVL_CHECK_VERSION=\"${PROJECT_VERSION}\"")
set_target_properties(mirador-display-check PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}")
