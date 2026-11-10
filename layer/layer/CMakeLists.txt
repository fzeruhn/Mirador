# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

# The commands of vk.xml that can be handed one of the layer's own objects (design 6.3, last bullet), as a generated
# include. vk.xml comes with the Vulkan headers (vcpkg's vulkan-headers port installs it next to them); DVL_VK_XML
# overrides the search.
find_package(Python3 REQUIRED COMPONENTS Interpreter)
find_file(DVL_VK_XML vk.xml
    HINTS "${VulkanHeaders_DIR}/../../.."
    PATH_SUFFIXES share/vulkan/registry
    DOC "Vulkan registry (vk.xml) that tools/gen_handle_commands.py reads"
    REQUIRED)
set(DVL_HANDLE_COMMANDS_INC "${PROJECT_BINARY_DIR}/generated/handle_commands.inc")
add_custom_command(
    OUTPUT "${DVL_HANDLE_COMMANDS_INC}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${PROJECT_BINARY_DIR}/generated"
    COMMAND "${Python3_EXECUTABLE}" -I "${PROJECT_SOURCE_DIR}/tools/gen_handle_commands.py"
            "${DVL_VK_XML}" "${DVL_HANDLE_COMMANDS_INC}"
    DEPENDS "${PROJECT_SOURCE_DIR}/tools/gen_handle_commands.py" "${DVL_VK_XML}"
    COMMENT "Listing the vk.xml commands that take display, mode, surface or swapchain handles"
    VERBATIM)

# dvl_pure: the layer's files that call no driver and no Vulkan function of their own (the process guard, the log, the
# dispatch tables, the command classification, process exit, the display-event table, the warnings). Linked into the DLL and into dvl_tests, so the tests run
# them directly.
add_library(dvl_pure OBJECT
    device_setup.cpp
    dispatch.cpp
    display.cpp
    env.cpp
    event_table.cpp
    exit.cpp
    guard.cpp
    handled_commands.cpp
    import_rules.cpp
    log.cpp
    present_timing.cpp
    surface.cpp
    warnings.cpp
    "${DVL_HANDLE_COMMANDS_INC}"
)
target_include_directories(dvl_pure PUBLIC "${PROJECT_SOURCE_DIR}")
target_include_directories(dvl_pure PRIVATE "${PROJECT_BINARY_DIR}/generated")
target_compile_features(dvl_pure PUBLIC cxx_std_17)
# The layer reaches Vulkan only through the pointers the loader hands it, never through vulkan-1.lib. The Win32
# extensions (VK_KHR_external_memory_win32, VK_EXT_full_screen_exclusive, VK_NV_acquire_winrt_display) are part of what
# it handles.
target_compile_definitions(dvl_pure PUBLIC VK_NO_PROTOTYPES VK_USE_PLATFORM_WIN32_KHR)
target_link_libraries(dvl_pure PUBLIC Vulkan::Headers nvdm)

# dvl_layer: the layer's entry points and intercepts, which call the layer below and the display session. Compiled
# once, linked into the DLL and into its test build.
add_library(dvl_layer OBJECT
    device.cpp
    display_intercepts.cpp
    events.cpp
    import.cpp
    layer_main.cpp
    misc_intercepts.cpp
    present.cpp
    present_timing_intercepts.cpp
    surface_intercepts.cpp
    swapchain.cpp
)
target_link_libraries(dvl_layer PRIVATE dvl_pure nvdm)
target_compile_definitions(dvl_layer PRIVATE "DVL_LAYER_VERSION=\"${PROJECT_VERSION}\"")

# dreamair_vk_layer.dll: the implicit layer VK_LAYER_DREAMAIR_nvapi_direct. The manifest is generated next to it, so
# the build folder (layer/ in it) can be given to the loader as it is.
add_library(dreamair_vk_layer SHARED $<TARGET_OBJECTS:dvl_layer>)
target_link_libraries(dreamair_vk_layer PRIVATE dvl_pure nvdm)
set_target_properties(dreamair_vk_layer PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)

# dvl_layer_for_tests.dll: the same objects, plus the export through which the tests put a display session on a fake
# NVAPI in place of the process's (test_hooks.cpp). Never installed; the shipped DLL has no such export.
add_library(dvl_layer_for_tests SHARED $<TARGET_OBJECTS:dvl_layer> test_hooks.cpp)
target_link_libraries(dvl_layer_for_tests PRIVATE dvl_pure nvdm)
set_target_properties(dvl_layer_for_tests PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}"
)

# The system DLLs only an open display session uses are delay-loaded (delayimp): a process the layer declines (the
# guard, design 5) maps none of them, and the display session's first call loads each from its usual place. d3d11 and
# dxgi: the session's D3D11 device; avrt: the display thread's MMCSS priority; setupapi: the board check of the last
# EDID route.
foreach(target dreamair_vk_layer dvl_layer_for_tests)
    target_link_libraries(${target} PRIVATE delayimp)
    target_link_options(${target} PRIVATE
        /DELAYLOAD:d3d11.dll /DELAYLOAD:dxgi.dll /DELAYLOAD:avrt.dll /DELAYLOAD:setupapi.dll)
endforeach()

configure_file(dreamair_vk_layer.json.in "${CMAKE_CURRENT_BINARY_DIR}/dreamair_vk_layer.json" @ONLY)
