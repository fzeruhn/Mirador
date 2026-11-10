# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

cmake_minimum_required(VERSION 3.21)
project(mirador_direct_mode VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT WIN32)
    message(FATAL_ERROR "mirador-direct-mode is a Windows-only project")
endif()

if(MSVC)
    add_compile_options(/W4 /permissive- /utf-8)
endif()

# The static CRT for every target, so the layer and the check tool need no Visual C++ Redistributable: a binary built
# with MSVC 14.40 or newer crashes in std::mutex against an older msvcp140.dll, and the first one loaded into
# monado-service.exe wins. The layer exchanges no CRT objects with Monado (a C API), so a CRT of its own is safe. The
# vcpkg dependencies come from the x64-windows-static triplet to match (build.ps1).
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_definitions(WIN32_LEAN_AND_MEAN NOMINMAX)

# Dependencies come from vcpkg manifest mode (vcpkg.json).
find_package(VulkanHeaders CONFIG REQUIRED)
find_package(Catch2 3 CONFIG REQUIRED)

# Each target is added by the task that brings its first source file, so the
# build works while some of these folders do not exist yet.
#   core/   nvdm                   static library
#   layer/  dvl_pure               object library (no driver calls)
#           mirador_direct_mode      shared library, links dvl_pure and nvdm
#   check/  mirador-display-check executable
#   tests/  dvl_tests              Catch2 tests, links nvdm and dvl_pure
foreach(dir core layer check)
    if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/${dir}/CMakeLists.txt")
        add_subdirectory(${dir})
    endif()
endforeach()

enable_testing()
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/tests/CMakeLists.txt")
    add_subdirectory(tests)
endif()
