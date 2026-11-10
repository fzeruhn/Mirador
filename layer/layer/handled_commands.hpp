// Copyright 2026, Finn Zeruhn
// SPDX-License-Identifier: BSL-1.0

#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <string_view>
#include <vector>

namespace dvl {

// Command coverage (design 6.3, last bullet). The layer makes its own VkDisplayKHR, VkDisplayModeKHR, VkSurfaceKHR and
// VkSwapchainKHR, so every command that can be handed one must be answered by the layer: none may reach the loader's
// terminators or the driver. tools/gen_handle_commands.py lists those commands from vk.xml, in all extensions; this
// file says what the layer does with each.

// One line of the generated list (build/generated/handle_commands.inc): a command, and the extension (or core version,
// "VK_VERSION_1_1") that requires it. A command that two extensions require is listed twice.
struct HandleCommand {
    std::string_view name;
    std::string_view extension;
};

// The generated list. Lives for the process.
const std::vector<HandleCommand>& handle_commands();

// True for the commands the layer intercepts now, and for those that Tasks 9-11 will (planned_commands()).
bool handled_command(std::string_view name);

// A one-line reason if the command can never be handed our objects in this layer (another window system or platform),
// else nullptr. The reason is a string that lives for the process.
const char* not_applicable(std::string_view name);

// All of them, for the tests.
struct NotApplicableCommand {
    std::string_view name;
    const char* reason;
};
const std::vector<NotApplicableCommand>& not_applicable_commands();

// The commands the layer intercepts now. A test checks each one against the layer's proc-address tables.
const std::vector<std::string_view>& intercepted_commands();

// The commands that are counted as handled but not intercepted yet (kPlanned in handled_commands.cpp), and the task
// that will do it. Each task moves its commands to intercepted_commands() when it adds the intercept; a test fails if
// the two disagree with the layer's tables. Empty since Task 11.
struct PlannedCommand {
    std::string_view name;
    int task;
};
const std::vector<PlannedCommand>& planned_commands();

// The extensions the registry snapshot has (DVL_KNOWN_EXTENSION in the generated file), and the snapshot's Vulkan version
// ("1.4.309"). The registry is pinned (the Vulkan-Headers port), and a driver can be newer than it.
const std::vector<std::string_view>& known_extensions();
std::string_view registry_version();

// The runtime guard, and what it can and cannot see. It looks at the extensions an application enables at
// vkCreateInstance and vkCreateDevice, and
//   - for an extension the snapshot has, reports a command of it in the generated list that is neither handled nor not
//     applicable (coverage_errors). That is the list the build-time test already checks, so on a green build this stays
//     silent: it is a second line of defence for a build that skipped the tests,
//   - for an extension the snapshot does not have (a driver newer than the registry, as VK_EXT_present_timing and
//     VK_KHR_present_wait2 were for the 1.4.309 one), warns that its commands were never checked
//     (unknown_extension_warnings). Some of them may take a VkSwapchainKHR; only a newer registry, and a classification
//     of its commands, can close that.
// It does not see core-version commands (vkCreateImage, vkBindImageMemory2, vkSetPrivateData, vkGetDeviceImage*): they
// need no extension. They are covered by the build-time test every_handle_command_is_classified only.

// The error lines for every enabled extension that has a command in `list` for which `classified(name)` is false: one
// line per extension, naming it and its commands. `where` names the call ("vkCreateInstance"). `extensions` may be null if
// `count` is 0.
std::vector<std::string> coverage_errors(const std::vector<HandleCommand>& list, const char* const* extensions,
                                         uint32_t count, const std::function<bool(std::string_view)>& classified,
                                         std::string_view where);

// The warning lines for every enabled extension that is not in `known`, one per extension (a name given twice is one):
// "<name>: unknown to the layer's Vulkan registry (<version>); its commands are not checked for the layer's handles".
std::vector<std::string> unknown_extension_warnings(const std::vector<std::string_view>& known,
                                                    std::string_view version, const char* const* extensions,
                                                    uint32_t count);

// The guard at vkCreateInstance and vkCreateDevice: coverage_errors() on the generated list (a command classified if it
// is handled or not applicable), written to the log at error level, and unknown_extension_warnings() on known_extensions(),
// at warning level. Each line is written once per process. Never throws.
void check_command_coverage(const char* const* extensions, uint32_t count, const char* where) noexcept;

} // namespace dvl
