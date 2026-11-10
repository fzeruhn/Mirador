# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0
"""Lists the Vulkan commands that can be handed one of the layer's own objects.

    python -I tools/gen_handle_commands.py <vk.xml> <handle_commands.inc>

The layer makes its own VkDisplayKHR, VkDisplayModeKHR, VkSurfaceKHR and VkSwapchainKHR (design 6.3). Every command
that takes one of them must be answered by the layer, so none reaches the loader's terminators or the driver. This reads
vk.xml and writes one DVL_HANDLE_COMMAND("<command>", "<extension>") line for each command, in all extensions (not only
the ones today's driver has), that

  * has a parameter of one of the four handle types, or a pointer to one (an output counts: a command that gives the
    application a handle must give it ours); or
  * has a parameter of a struct type that contains such a member, directly, through a nested struct, or through a
    struct that extends it in the pNext chain (VkImageSwapchainCreateInfoKHR in a VkImageCreateInfo, for one); or
  * names an object by type and number (a VkObjectType or VkDebugReportObjectTypeEXT parameter, or one in a struct
    that is a parameter or nested in one, as in vkSetDebugUtilsObjectNameEXT and vkSetPrivateData), because that number
    can be the number of one of ours. This one does not follow pNext: the only extension struct with such a member
    (VkDebugUtilsObjectNameInfoEXT in a shader stage's pNext) names a shader module.

A command that several extensions require has a line for each of them, because the runtime guard looks the list up by
the extension an application enables. Core commands are listed under their VK_VERSION_x_y feature; newer registries
(1.4.3xx) require them in internal features (VK_BASE_VERSION_x_y, VK_COMPUTE_VERSION_x_y, VK_GRAPHICS_VERSION_x_y) that
VK_VERSION_x_y depends on, and those count as VK_VERSION_x_y of the same number. Only the "vulkan" API counts, not
"vulkansc", and not extensions that are disabled.

The registry is a snapshot, and a driver can be newer than it. So the file also has DVL_KNOWN_EXTENSION("<name>") for
every extension the registry has (of any API: the name is known, even if no application can enable it here), and
DVL_REGISTRY_VERSION("<major.minor.patch>"). The guard warns about an enabled extension that is not among the known ones:
its commands were never checked.

The output has no dates and no paths, so a rebuild with the same vk.xml changes nothing.
"""

import sys
import xml.etree.ElementTree as ET

# The objects the layer makes.
OUR_HANDLES = frozenset({"VkSurfaceKHR", "VkSwapchainKHR", "VkDisplayKHR", "VkDisplayModeKHR"})
# Types that name an object by number: the number can be the number of one of ours.
OBJECT_TYPES = frozenset({"VkObjectType", "VkDebugReportObjectTypeEXT"})


def serves_vulkan(api):
    """True for an api attribute ("vulkan", "vulkan,vulkansc", ...) that includes the Vulkan API; absent means yes."""
    return api is None or "vulkan" in api.split(",")


class Registry:
    def __init__(self, root):
        self.root = root
        self.struct_aliases = {}
        self.struct_members = {}   # struct name -> list of (member name, member type name)
        self.struct_extends = {}   # struct name -> list of the structs it extends (pNext)
        self.command_aliases = {}  # alias name -> target name
        self.command_params = {}   # command name -> list of (parameter name, parameter type name)
        self._read_types()
        self._read_commands()
        self.extenders = {}        # struct name -> structs that extend it
        for child, parents in self.struct_extends.items():
            for parent in parents:
                self.extenders.setdefault(self.canonical_struct(parent), []).append(child)
        self.header_version = self._read_header_version()

    def _read_header_version(self):
        for t in self.root.find("types").findall("type"):
            name = t.find("name")
            if t.get("category") == "define" and name is not None and name.text == "VK_HEADER_VERSION":
                return (name.tail or "").strip()
        return "unknown"

    def _read_types(self):
        for t in self.root.find("types").findall("type"):
            if t.get("category") not in ("struct", "union") or not serves_vulkan(t.get("api")):
                continue
            name = t.get("name")
            if t.get("alias"):
                self.struct_aliases[name] = t.get("alias")
                continue
            members = self.struct_members.setdefault(name, [])
            for m in t.findall("member"):
                if serves_vulkan(m.get("api")):
                    members.append((m.find("name").text, m.find("type").text))
            if t.get("structextends"):
                self.struct_extends[name] = t.get("structextends").split(",")

    def _read_commands(self):
        for c in self.root.find("commands").findall("command"):
            if not serves_vulkan(c.get("api")):
                continue
            if c.get("alias"):
                self.command_aliases[c.get("name")] = c.get("alias")
                continue
            name = c.find("proto/name").text
            self.command_params[name] = [(p.find("name").text, p.find("type").text) for p in c.findall("param")]

    def canonical_struct(self, name):
        while name in self.struct_aliases:
            name = self.struct_aliases[name]
        return name

    def canonical_command(self, name):
        while name in self.command_aliases:
            name = self.command_aliases[name]
        return name

    def why(self, command, wanted, follow_pnext):
        """How a parameter of `command` can carry one of the types in `wanted`: a short phrase, or None if none can."""
        for param_name, param_type in self.command_params.get(self.canonical_command(command), []):
            if param_type in wanted:
                return "parameter %s" % param_name
            start = self.canonical_struct(param_type)
            if start not in self.struct_members:
                continue
            seen = set()
            todo = [(start, "parameter %s" % param_name)]
            while todo:
                struct, via = todo.pop(0)
                if struct in seen:
                    continue
                seen.add(struct)
                for member_name, member_type in self.struct_members[struct]:
                    if member_type in wanted:
                        return "%s::%s, %s" % (struct, member_name, via)
                for _, member_type in self.struct_members[struct]:
                    nested = self.canonical_struct(member_type)
                    if nested in self.struct_members:
                        todo.append((nested, via))
                if follow_pnext:
                    for child in self.extenders.get(struct, []):
                        todo.append((self.canonical_struct(child), "pNext of %s, %s" % (struct, via)))
        return None


def requirements(root):
    """Which features and extensions require which commands.

    Returns (required, excluded). `required` maps a command name to the features and extensions (file order, once
    each) that require it for the Vulkan API. `excluded` is the set of commands that only elements outside the Vulkan
    API require (disabled extensions, Vulkan SC only ones); no application can enable those.
    """
    required = {}
    excluded = set()

    def owner_name(element):
        # An internal core feature (VK_BASE_VERSION_1_0, ...) is part of the public VK_VERSION_x_y of its number.
        if element.tag == "feature" and element.get("apitype") == "internal" and element.get("number"):
            return "VK_VERSION_" + element.get("number").replace(".", "_")
        return element.get("name")

    def scan(element):
        supported = element.get("supported")
        element_counts = serves_vulkan(element.get("api")) and (supported is None or "vulkan" in supported.split(","))
        for require in element.findall("require"):
            counts = element_counts and serves_vulkan(require.get("api"))
            for command in require.findall("command"):
                if not counts:
                    excluded.add(command.get("name"))
                    continue
                owners = required.setdefault(command.get("name"), [])
                if owner_name(element) not in owners:
                    owners.append(owner_name(element))

    for feature in root.findall("feature"):
        scan(feature)
    for extension in root.find("extensions").findall("extension"):
        scan(extension)
    return required, excluded


def registry_version(registry):
    """The Vulkan version of the registry as major.minor.patch: the newest core version it has, and the header version."""
    newest = (1, 0)
    for feature in registry.root.findall("feature"):
        if serves_vulkan(feature.get("api")) and feature.get("number"):
            newest = max(newest, tuple(int(part) for part in feature.get("number").split(".")))
    return "%d.%d.%s" % (newest[0], newest[1], registry.header_version)


def main(argv):
    if len(argv) != 3:
        sys.stderr.write("usage: python -I gen_handle_commands.py <vk.xml> <handle_commands.inc>\n")
        return 2
    registry = Registry(ET.parse(argv[1]).getroot())
    owners, excluded = requirements(registry.root)

    lines = []
    orphans = []
    for name in sorted(set(registry.command_params) | set(registry.command_aliases)):
        reason = registry.why(name, OUR_HANDLES, True) or registry.why(name, OBJECT_TYPES, False)
        if reason is None:
            continue
        if name not in owners:
            if name not in excluded:
                orphans.append(name)
            continue
        for owner in owners[name]:
            lines.append('DVL_HANDLE_COMMAND("%s", "%s") // %s' % (name, owner, reason))

    if orphans:
        sys.stderr.write("commands that take handles but that no feature or extension lists: %s\n" % ", ".join(orphans))
        return 1

    version = registry_version(registry)
    extensions = sorted(e.get("name") for e in registry.root.find("extensions").findall("extension"))
    header = [
        "// Generated by tools/gen_handle_commands.py from vk.xml (VK_HEADER_VERSION %s). Do not edit." % registry.header_version,
        "// DVL_REGISTRY_VERSION(the registry's Vulkan version), then",
        "// DVL_HANDLE_COMMAND(command, the extension or core version that requires it) for every command, in every",
        "// extension, that can be handed a VkDisplayKHR, VkDisplayModeKHR, VkSurfaceKHR or VkSwapchainKHR, or an object by",
        "// type and number (the comment says how the command reaches such a handle), then",
        "// DVL_KNOWN_EXTENSION(name) for every extension the registry has, so that the runtime guard can tell an extension",
        "// whose commands it has checked from one that is newer than this registry.",
        'DVL_REGISTRY_VERSION("%s")' % version,
    ]
    known = ['DVL_KNOWN_EXTENSION("%s")' % name for name in extensions]
    with open(argv[2], "w", encoding="utf-8", newline="\n") as out:
        out.write("\n".join(header + lines + known) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
