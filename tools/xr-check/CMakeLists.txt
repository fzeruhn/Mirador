# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0
#
# xr-check: Mirador's small OpenXR test client for "Verify build for Windows" (Mirador.md §4.11 item 49). Built with
# tools\build-cmake.ps1 (vcpkg x64-windows-static: the OpenXR loader is linked in, the CRT is static).

cmake_minimum_required(VERSION 3.21)
project(mirador_xr_check VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(NOT WIN32)
    message(FATAL_ERROR "xr-check is Windows-only")
endif()

find_package(OpenXR CONFIG REQUIRED)
find_package(VulkanHeaders CONFIG REQUIRED)

add_executable(xr-check main.cpp)
target_compile_options(xr-check PRIVATE /W4 /WX /permissive- /utf-8)
target_compile_definitions(xr-check PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_link_libraries(xr-check PRIVATE OpenXR::openxr_loader Vulkan::Headers d3d11 dxgi)
