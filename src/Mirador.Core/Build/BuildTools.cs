using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mirador.Core.Build;

/// <summary>Where the build tools are: Mirador's own copies when it has them, else what is on <c>PATH</c>.</summary>
/// <param name="Recipe">Mirador's <c>build-monado.ps1</c> (<c>{app}\tools</c>).</param>
/// <param name="ToolsDir">Mirador's own vcpkg, CMake, Ninja and Git (<see cref="AppPaths.ToolsDir"/>); used once it exists.</param>
/// <param name="PowerShell">Windows PowerShell, which runs the recipe.</param>
public sealed record BuildTools(string Recipe, string? ToolsDir, string PowerShell)
{
    /// <summary>The tools of an installed Mirador.</summary>
    public static BuildTools For(string appDir, AppPaths paths) => new(
        Path.Combine(appDir, "tools", "build-monado.ps1"),
        paths.ToolsDir,
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));

    /// <summary>
    /// <see cref="ToolsDir"/> when it exists now: "Install what's missing" fills it while Mirador runs, and the next
    /// build uses it without a restart.
    /// </summary>
    public string? ExistingToolsDir => ToolsDir is { } dir && Directory.Exists(dir) ? dir : null;

    /// <summary>Git: Mirador's copy, else the one on <c>PATH</c>.</summary>
    public string Git => ToolsDir is { } dir && File.Exists(Path.Combine(dir, "git", "cmd", "git.exe"))
        ? Path.Combine(dir, "git", "cmd", "git.exe")
        : "git";

    /// <summary>ctest: beside Mirador's CMake, else the one on <c>PATH</c>.</summary>
    public string Ctest => ToolsDir is { } dir && File.Exists(Path.Combine(dir, "cmake", "bin", "ctest.exe"))
        ? Path.Combine(dir, "cmake", "bin", "ctest.exe")
        : "ctest";
}

/// <summary>What the "Check build tools" page says of one part (Mirador.md §4.11 item 49).</summary>
public enum ToolState
{
    /// <summary>There and as builds need it.</summary>
    Found,

    /// <summary>There, but not as builds need it: Mirador can fix it.</summary>
    Fixable,

    /// <summary>Not there: Mirador can install it.</summary>
    Missing,
}

/// <summary>One part's state and what it means, in plain words.</summary>
public sealed record ToolStatus(string Name, ToolState State, string Detail);

/// <summary>A Visual Studio or Build Tools install, as vswhere lists it.</summary>
/// <param name="Path">Its installation folder.</param>
/// <param name="Name">Its display name ("Visual Studio Build Tools 2026").</param>
/// <param name="Version">Its installation version (17 is 2022, 18 is 2026).</param>
/// <param name="IsComplete">Whether its last install or update finished.</param>
/// <param name="Packages">The ids of its components and packages.</param>
public sealed record VsInstance(string Path, string Name, Version Version, bool IsComplete, IReadOnlySet<string> Packages);

/// <summary>vswhere, Visual Studio's own finder: how Mirador lists the installs and their components.</summary>
public static class VsWhere
{
    /// <summary>Where Visual Studio's installer keeps vswhere.</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");

    /// <summary>Every install (Build Tools too), with its packages, as JSON. No prereleases: the recipe takes none.</summary>
    public static IReadOnlyList<string> Arguments { get; } = ["-all", "-products", "*", "-include", "packages", "-format", "json", "-utf8"];

    /// <summary>The installs in vswhere's JSON; none for output that isn't its JSON.</summary>
    public static IReadOnlyList<VsInstance> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var all = new List<VsInstance>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || Text(item, "installationPath") is not { } path
                    || !Version.TryParse(Text(item, "installationVersion"), out var version))
                {
                    continue;
                }

                var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (item.TryGetProperty("packages", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var package in list.EnumerateArray())
                    {
                        if (package.ValueKind == JsonValueKind.Object && Text(package, "id") is { } id)
                        {
                            packages.Add(id);
                        }
                    }
                }

                var complete = !item.TryGetProperty("isComplete", out var flag) || flag.ValueKind != JsonValueKind.False;
                all.Add(new VsInstance(path, Text(item, "displayName") ?? path, version, complete, packages));
            }

            return all;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Runs vswhere at <paramref name="vswhere"/>; no installs when it isn't there or fails.</summary>
    public static async Task<IReadOnlyList<VsInstance>> FindAsync(ICommandRunner runner, string vswhere, CancellationToken ct)
    {
        if (!File.Exists(vswhere))
        {
            return [];
        }

        var lines = new List<string>();
        int code;
        try
        {
            code = await runner.RunAsync(new CommandSpec(vswhere, Arguments), lines.Add, ct).ConfigureAwait(false);
        }
        catch (MiradorException)
        {
            return [];
        }

        return code == 0 ? Parse(string.Join('\n', lines)) : [];
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>The Visual Studio components Monado's build needs, and those a new install gets.</summary>
public static class VsComponents
{
    public const string CppTools = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64";
    public const string Windows11Sdk = "Microsoft.VisualStudio.Component.Windows11SDK.26100";

    /// <summary>What the check asks of the install the recipe uses: MSVC for x64 and the Windows 11 SDK 26100.</summary>
    public static IReadOnlyList<string> Required { get; } = [CppTools, Windows11Sdk];

    /// <summary>What a new Build Tools install gets: exactly upstream's CI (<c>.gitlab-ci/windows/monado_deps_vs2022.ps1</c>).</summary>
    public static IReadOnlyList<string> Install { get; } =
    [
        "Microsoft.VisualStudio.Component.VC.CoreBuildTools",
        "Microsoft.VisualStudio.ComponentGroup.NativeDesktop.Core",
        "Microsoft.VisualStudio.Component.Windows10SDK",
        Windows11Sdk,
        "Component.Microsoft.Windows.CppWinRT",
        CppTools,
    ];

    /// <summary>A component in plain words.</summary>
    public static string Describe(string id) => id switch
    {
        CppTools => "the C++ x64 tools (MSVC)",
        Windows11Sdk => "the Windows 11 SDK (10.0.26100)",
        _ => id,
    };
}

/// <summary>
/// Visual Studio for Monado's build: the install the recipe enters (vswhere's newest with the C++ x64 tools, else the
/// newest at all), what it lacks, and the one installer command that fixes it.
/// </summary>
/// <param name="State">Found, fixable (components to add) or missing (no Visual Studio 2022 or newer).</param>
/// <param name="Install">The install checked; null when there is none.</param>
/// <param name="Missing">The components to add; for a missing install, those a new one gets.</param>
/// <param name="Detail">The state in plain words.</param>
public sealed record VsPlan(ToolState State, VsInstance? Install, IReadOnlyList<string> Missing, string Detail)
{
    /// <summary>The part's name on the page.</summary>
    public const string Name = "Visual Studio C++ tools";

    /// <summary>The plan for these installs.</summary>
    public static VsPlan For(IReadOnlyList<VsInstance> all)
    {
        var usable = all.Where(i => i.IsComplete && i.Version.Major >= 17).ToList();
        var target = usable.Where(i => i.Packages.Contains(VsComponents.CppTools)).MaxBy(i => i.Version) ?? usable.MaxBy(i => i.Version);
        if (target is null)
        {
            return new VsPlan(
                ToolState.Missing, null, VsComponents.Install,
                "No Visual Studio 2022 or newer, and no Build Tools. Mirador can install Microsoft's Build Tools for Visual Studio 2022 with the C++ tools and the Windows 11 SDK (one administrator prompt).");
        }

        var missing = VsComponents.Required.Where(c => !target.Packages.Contains(c)).ToList();
        var name = $"{target.Name} ({target.Version.ToString(2)})";
        return missing.Count == 0
            ? new VsPlan(ToolState.Found, target, [], $"{name} has the C++ x64 tools and the Windows 11 SDK (10.0.26100).")
            : new VsPlan(
                ToolState.Fixable, target, missing,
                $"{name} lacks {string.Join(" and ", missing.Select(VsComponents.Describe))}. Mirador can add {(missing.Count == 1 ? "it" : "them")} with Visual Studio's installer (one administrator prompt).");
    }

    /// <summary>
    /// The command that fixes this: Visual Studio's own installer (<c>setup.exe modify</c> in <paramref name="installerDir"/>)
    /// adding what is missing, or Microsoft's bootstrapper (<paramref name="bootstrapper"/>) for a new install. Both
    /// show their progress and wait until they are done.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is to be fixed.</exception>
    public CommandSpec Command(string installerDir, string bootstrapper)
    {
        // --wait is the bootstrapper's alone (it would otherwise hand over to the installer and return); setup.exe is the
        // installer itself and runs until it is done.
        var adds = Missing.SelectMany(c => new[] { "--add", c });
        return State switch
        {
            ToolState.Fixable => new CommandSpec(Path.Combine(installerDir, "setup.exe"), ["modify", "--installPath", Install!.Path, .. adds, "--passive", "--norestart"]),
            ToolState.Missing => new CommandSpec(bootstrapper, [.. adds, "--passive", "--norestart", "--wait"]),
            _ => throw new InvalidOperationException("Visual Studio has what Monado's build needs."),
        };
    }
}

/// <summary>One of Mirador's small build tools: a zip from a GitHub release, checked by its SHA-256.</summary>
/// <param name="Name">Its name ("CMake").</param>
/// <param name="Folder">Its folder under <see cref="AppPaths.ToolsDir"/>, where the recipe looks (<c>cmake</c>).</param>
/// <param name="Version">Its version.</param>
/// <param name="Url">Its zip, an https URL on github.com.</param>
/// <param name="Sha256">The zip's SHA-256 (lower-case hex), from GitHub's digest of the release file.</param>
/// <param name="Exe">Its program inside the folder (<c>bin\cmake.exe</c>), which must be in the zip.</param>
public sealed record ToolPin(string Name, string Folder, string Version, string Url, string Sha256, string Exe);

/// <summary>
/// The versions Mirador's builds use (Mirador.md §4.11 item 49: "every build uses the same versions"). Raising one is
/// a code change, with the new release file's SHA-256 from GitHub.
/// </summary>
public static class ToolPins
{
    /// <summary>MinGit, the portable Git for Windows: cloning vcpkg and the branches.</summary>
    public static readonly ToolPin Git = new(
        "Git", "git", "2.56.0.4",
        "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.4/MinGit-2.56.0.4-64-bit.zip",
        "e3bf78351fe777fba47ddee5950082f8a98bb0f02604fdc7ba3ee2cb68ffa877", @"cmd\git.exe");

    public static readonly ToolPin CMake = new(
        "CMake", "cmake", "4.4.5",
        "https://github.com/Kitware/CMake/releases/download/v4.4.5/cmake-4.4.5-windows-x86_64.zip",
        "326c826486d46a23cf9d5a1c9ee3a567d427bdd02c4a34071937a2a675cff004", @"bin\cmake.exe");

    public static readonly ToolPin Ninja = new(
        "Ninja", "ninja", "1.13.2",
        "https://github.com/ninja-build/ninja/releases/download/v1.13.2/ninja-win.zip",
        "07fc8261b42b20e71d1720b39068c2e14ffcee6396b76fb7a795fb460b78dc65", "ninja.exe");

    /// <summary>Every pinned tool, Git first (vcpkg is cloned with it).</summary>
    public static IReadOnlyList<ToolPin> All { get; } = [Git, CMake, Ninja];
}

/// <summary>
/// Checks Mirador's own build tools under <c>%LOCALAPPDATA%\Mirador\tools</c>: each pinned tool and vcpkg at the commit
/// Mirador pins (<c>tools\vcpkg-commit.txt</c>). Reads files only; nothing is run.
/// </summary>
public sealed partial class BuildToolsCheck(string toolsDir, string vcpkgCommit)
{
    /// <summary>The file in a tool's folder that says which pinned download it came from.</summary>
    public const string MarkerFile = "mirador-tool.json";

    /// <summary>Where vcpkg is cloned.</summary>
    public string VcpkgDir => Path.Combine(toolsDir, "vcpkg");

    /// <summary>The state of <paramref name="pin"/>'s copy.</summary>
    public ToolStatus Tool(ToolPin pin)
    {
        var folder = Path.Combine(toolsDir, pin.Folder);
        if (!File.Exists(Path.Combine(folder, pin.Exe)))
        {
            return new ToolStatus(pin.Name, ToolState.Missing, $"Not downloaded yet: {pin.Name} {pin.Version} from GitHub.");
        }

        var marker = ReadMarker(Path.Combine(folder, MarkerFile));
        if (marker is { } m && m.Version == pin.Version && string.Equals(m.Sha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ToolStatus(pin.Name, ToolState.Found, $"{pin.Name} {pin.Version}, Mirador's copy.");
        }

        return new ToolStatus(
            pin.Name, ToolState.Fixable,
            $"Mirador has {pin.Name} {marker?.Version ?? "of an unknown version"}; builds use {pin.Version}.");
    }

    /// <summary>The state of Mirador's vcpkg clone.</summary>
    public ToolStatus Vcpkg()
    {
        if (!Commit().IsMatch(vcpkgCommit))
        {
            return new ToolStatus("vcpkg", ToolState.Missing, "This Mirador has no vcpkg pin (tools\\vcpkg-commit.txt is missing or wrong): reinstall Mirador.");
        }

        var shortPin = vcpkgCommit[..7];
        var head = Path.Combine(VcpkgDir, ".git", "HEAD");
        if (!File.Exists(head))
        {
            return new ToolStatus("vcpkg", ToolState.Missing, $"Not cloned yet: vcpkg from github.com/microsoft/vcpkg, at commit {shortPin}.");
        }

        string text;
        try
        {
            text = File.ReadAllText(head).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ToolStatus("vcpkg", ToolState.Fixable, $"Mirador's vcpkg can't be read; it can be cloned again at commit {shortPin}.");
        }

        if (string.Equals(text, vcpkgCommit, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(VcpkgDir, "scripts", "buildsystems", "vcpkg.cmake")))
        {
            return new ToolStatus("vcpkg", ToolState.Found, $"At Mirador's pinned commit {shortPin}.");
        }

        var at = Commit().IsMatch(text) ? "commit " + text[..7] : "a branch";
        return new ToolStatus("vcpkg", ToolState.Fixable, $"At {at}, not Mirador's pinned commit {shortPin}.");
    }

    /// <summary>The pinned vcpkg commit in <paramref name="file"/> (<c>{app}\tools\vcpkg-commit.txt</c>), or null.</summary>
    public static string? ReadPin(string file)
    {
        try
        {
            var text = File.ReadAllText(file).Trim();
            return Commit().IsMatch(text) ? text.ToLowerInvariant() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal sealed record Marker(string Version, string Sha256);

    internal static Marker? ReadMarker(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    internal static partial Regex Commit();
}
