# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0

include(Catch)

add_executable(dvl_tests
    test_smoke.cpp
    test_nvapi_ids.cpp
    test_api.cpp
    test_product_table.cpp
    test_holders.cpp
    test_board.cpp
    test_display_session.cpp
    test_fence_pool.cpp
    test_frame_model.cpp
    test_frame_scenarios.cpp
    test_stats.cpp
    test_trace.cpp
    test_display_loop.cpp
    test_guard.cpp
    test_log.cpp
    test_dispatch.cpp
    test_exit.cpp
    test_layer_chain.cpp
    test_command_coverage.cpp
    test_device_setup.cpp
    test_display.cpp
    test_surface.cpp
    test_import.cpp
    test_trial_import.cpp
    test_events.cpp
    test_warnings.cpp
    test_swapchain.cpp
    test_present_timing.cpp
    test_heartbeat.cpp
    test_check_report.cpp
    test_check_vulkan.cpp
    test_check_nvapi.cpp
)
target_link_libraries(dvl_tests PRIVATE Catch2::Catch2WithMain)

# The libraries under test join as their folders appear.
foreach(lib nvdm dvl_pure dvl_check_lib)
    if(TARGET ${lib})
        target_link_libraries(dvl_tests PRIVATE ${lib})
    endif()
endforeach()

# The layer tests load the built DLL and read the manifest next to it, so the tests build after both. The intercept
# tests load the DLL's test build, which takes a display session on a fake NVAPI (fakes/fake_chain.hpp).
if(TARGET dreamair_vk_layer)
    add_dependencies(dvl_tests dreamair_vk_layer dvl_layer_for_tests)
    target_compile_definitions(dvl_tests PRIVATE
        "DVL_LAYER_DLL=\"$<TARGET_FILE:dreamair_vk_layer>\""
        "DVL_LAYER_TEST_DLL=\"$<TARGET_FILE:dvl_layer_for_tests>\""
        "DVL_LAYER_MANIFEST=\"$<TARGET_FILE_DIR:dreamair_vk_layer>/dreamair_vk_layer.json\"")
endif()

# PRE_TEST: discover test cases when ctest runs, not as a post-build step, so
# the vcpkg runtime DLLs next to the exe are in place by then.
catch_discover_tests(dvl_tests DISCOVERY_MODE PRE_TEST)
