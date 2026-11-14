using System.Text.Json.Serialization;
using Mirador.Core.Monado;

namespace Mirador.Core.Build;

/// <summary>How one check went.</summary>
public enum CheckOutcome
{
    Passed,
    Failed,

    /// <summary>Not run here, for the reason in <see cref="BuildCheck.Detail"/> (no build folder, no ctest).</summary>
    Skipped,
}

/// <summary>One check of a Monado build and what it found.</summary>
/// <param name="Name">What was checked, in plain words.</param>
/// <param name="Outcome">How it went.</param>
/// <param name="Detail">What was found: the missing files or DLLs, the failed tests, why it was skipped.</param>
public sealed record BuildCheck(string Name, CheckOutcome Outcome, string Detail);

/// <summary>
/// The checks of one build, kept as <c>checks.json</c> in the install folder by commit. CI's
/// <c>tools\check-monado-build.ps1</c> writes the same file into the weekly build's zip.
/// </summary>
/// <param name="Format">1.</param>
/// <param name="Commit">The commit the build came from, when known.</param>
/// <param name="CheckedAt">When the checks ran.</param>
/// <param name="Checks">Each check, in the order run.</param>
public sealed record BuildCheckResult(int Format, string? Commit, DateTimeOffset CheckedAt, IReadOnlyList<BuildCheck> Checks)
{
    /// <summary>The file name, in the install folder.</summary>
    public const string FileName = "checks.json";

    /// <summary>Whether no check failed (skipped ones don't count).</summary>
    [JsonIgnore]
    public bool AllPassed => Checks.All(c => c.Outcome != CheckOutcome.Failed);

    /// <summary>One line for the profile: all passed, or which failed.</summary>
    public string Summary()
    {
        var failed = Checks.Where(c => c.Outcome == CheckOutcome.Failed).Select(c => c.Name).ToList();
        return failed.Count == 0
            ? "All checks passed."
            : $"Failed: {string.Join(", ", failed)}. The build may still work; see the build log.";
    }

    /// <summary>Saves the result into <paramref name="installDir"/>.</summary>
    public void Save(string installDir) =>
        JsonFile.Save(Path.Combine(installDir, FileName), this, BuildJsonContext.Default.BuildCheckResult);

    /// <summary>The result kept in <paramref name="installDir"/>, or null when there is none or it can't be read.</summary>
    public static BuildCheckResult? Load(string installDir)
    {
        var path = Path.Combine(installDir, FileName);
        try
        {
            return File.Exists(path)
                ? System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), BuildJsonContext.Default.BuildCheckResult)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The checks that run after every Monado build, with no window opened (Mirador.md §4.11 item 49): the install's files
/// and runtime manifest, every DLL they need found, the service setting in the configure cache, and Monado's unit tests
/// (<c>ctest</c>). A failure warns; it doesn't block (upstream <c>main</c> is sometimes red).
/// </summary>
/// <param name="runner">Runs <c>ctest</c>.</param>
/// <param name="systemDir">Where Windows' own DLLs are (<c>System32</c>).</param>
/// <param name="time">The clock, for <see cref="BuildCheckResult.CheckedAt"/>.</param>
public sealed class BuildChecks(ICommandRunner runner, string systemDir, TimeProvider time)
{
    /// <summary>The install's runtime manifest, relative to its folder (Monado's Windows install layout).</summary>
    public const string ManifestFile = "openxr_monado.json";

    /// <summary>The install's runtime DLL, relative to its folder.</summary>
    public static readonly string RuntimeDllFile = Path.Combine("bin", "openxr_monado.dll");

    /// <summary>The install's service, relative to its folder.</summary>
    public static readonly string ServiceFile = Path.Combine("bin", "monado-service.exe");

    /// <summary>How long Monado's unit tests may take in all.</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs the checks on the install in <paramref name="installDir"/>. With <paramref name="buildDir"/> (the CMake build
    /// folder) also the service setting and, with <paramref name="ctest"/>, the unit tests; without them those are
    /// skipped. Nothing is saved: see <see cref="BuildCheckResult.Save"/>.
    /// </summary>
    public async Task<BuildCheckResult> RunAsync(
        string installDir, string? buildDir, string? ctest, string? commit, Action<string> log, CancellationToken ct)
    {
        var checks = new List<BuildCheck>
        {
            Files(installDir),
            Manifest(installDir),
            Dlls(installDir),
            ServiceSetting(buildDir),
        };
        checks.Add(await UnitTestsAsync(buildDir, ctest, log, ct).ConfigureAwait(false));
        foreach (var check in checks)
        {
            log($"Check: {check.Name}: {check.Outcome}. {check.Detail}");
        }

        return new BuildCheckResult(1, commit, time.GetUtcNow(), checks);
    }

    private static BuildCheck Files(string installDir)
    {
        const string name = "Files";
        var missing = new[] { ManifestFile, RuntimeDllFile, ServiceFile }
            .Where(f => !File.Exists(Path.Combine(installDir, f)))
            .ToList();
        return missing.Count == 0
            ? new BuildCheck(name, CheckOutcome.Passed, "The runtime manifest, its DLL and monado-service.exe are there.")
            : new BuildCheck(name, CheckOutcome.Failed, $"Missing: {string.Join(", ", missing)}.");
    }

    private static BuildCheck Manifest(string installDir)
    {
        const string name = "Runtime manifest";
        var manifest = Path.Combine(installDir, ManifestFile);
        var problem = RuntimeManifest.Validate(manifest);
        if (problem is not null)
        {
            return new BuildCheck(name, CheckOutcome.Failed, problem);
        }

        var library = RuntimeManifest.ReadLibraryPath(manifest);
        return string.Equals(library, Path.GetFullPath(Path.Combine(installDir, RuntimeDllFile)), StringComparison.OrdinalIgnoreCase)
            ? new BuildCheck(name, CheckOutcome.Passed, $"It names {RuntimeDllFile}.")
            : new BuildCheck(name, CheckOutcome.Failed, $"It names {library}, not the install's {RuntimeDllFile}.");
    }

    /// <summary>
    /// Every DLL the runtime and the service import, and the DLLs those import in turn, found in the install's
    /// <c>bin</c> folder or Windows' own folder. API sets (<c>api-ms-win-*</c>, <c>ext-ms-*</c>) are Windows' too.
    /// </summary>
    private BuildCheck Dlls(string installDir)
    {
        const string name = "DLLs";
        var bin = Path.Combine(installDir, "bin");
        var start = new[] { RuntimeDllFile, ServiceFile }.Select(f => Path.Combine(installDir, f)).Where(File.Exists).ToList();
        if (start.Count == 0)
        {
            return new BuildCheck(name, CheckOutcome.Skipped, "Nothing to check: the runtime DLL and the service are missing.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var broken = new List<string>();
        var queue = new Queue<string>(start);
        while (queue.TryDequeue(out var file))
        {
            IReadOnlyList<string> imports;
            try
            {
                imports = PeImports.Read(file);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                broken.Add($"{Path.GetFileName(file)} ({e.Message.TrimEnd('.')})");
                continue;
            }

            foreach (var dll in imports)
            {
                if (!seen.Add(dll) || IsApiSet(dll))
                {
                    continue;
                }

                var local = Path.Combine(bin, dll);
                if (File.Exists(local))
                {
                    queue.Enqueue(local);
                }
                else if (!File.Exists(Path.Combine(systemDir, dll)))
                {
                    missing.Add($"{dll} (needed by {Path.GetFileName(file)})");
                }
            }
        }

        if (missing.Count == 0 && broken.Count == 0)
        {
            return new BuildCheck(name, CheckOutcome.Passed, $"All {seen.Count} DLLs needed are in bin or Windows.");
        }

        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add($"Not found: {string.Join(", ", missing)}.");
        }

        if (broken.Count > 0)
        {
            parts.Add($"Unreadable: {string.Join(", ", broken)}.");
        }

        return new BuildCheck(name, CheckOutcome.Failed, string.Join(" ", parts));
    }

    private static bool IsApiSet(string dll) =>
        dll.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);

    private static BuildCheck ServiceSetting(string? buildDir)
    {
        const string name = "Service setting";
        if (buildDir is null)
        {
            return new BuildCheck(name, CheckOutcome.Skipped, "No build folder here (a downloaded build: CI checked it).");
        }

        var cache = Path.Combine(buildDir, "CMakeCache.txt");
        if (!File.Exists(cache))
        {
            return new BuildCheck(name, CheckOutcome.Failed, $"{cache} is missing.");
        }

        var value = CMakeCache.Read(cache, "XRT_SERVICE_EXECUTABLE");
        return Monado.ServiceLaunch.StartsStandIn(value)
            ? new BuildCheck(name, CheckOutcome.Passed, $"XRT_SERVICE_EXECUTABLE is {value}: games can start Monado through Mirador.")
            : new BuildCheck(name, CheckOutcome.Failed,
                $"XRT_SERVICE_EXECUTABLE is {(value is null ? "not set" : $"'{value}'")}: games can't start this Monado by themselves.");
    }

    private async Task<BuildCheck> UnitTestsAsync(string? buildDir, string? ctest, Action<string> log, CancellationToken ct)
    {
        const string name = "Unit tests";
        if (buildDir is null || ctest is null)
        {
            return new BuildCheck(name, CheckOutcome.Skipped,
                buildDir is null ? "No build folder here (a downloaded build: CI ran them)." : "ctest wasn't found.");
        }

        var failures = new List<string>();
        var noTests = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        int code;
        try
        {
            code = await runner.RunAsync(
                new CommandSpec(ctest, ["--test-dir", buildDir, "--output-on-failure", "--timeout", "120"]),
                line =>
                {
                    log(line);

                    // ctest exits 0 when the build has no tests at all (a fork without them, BUILD_TESTING off).
                    noTests |= line.Contains("No tests were found", StringComparison.Ordinal);

                    // ctest's summary: "  3 - tests_foo (Failed)".
                    if (line.TrimStart().Length > 0 && char.IsDigit(line.TrimStart()[0]) && line.Contains(" - ", StringComparison.Ordinal)
                        && (line.EndsWith("(Failed)", StringComparison.Ordinal) || line.EndsWith("(Timeout)", StringComparison.Ordinal)
                            || line.Contains("(SEGFAULT)", StringComparison.Ordinal) || line.Contains("(Exception)", StringComparison.Ordinal)))
                    {
                        failures.Add(line.Split(" - ", 2)[1].Trim());
                    }
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new BuildCheck(name, CheckOutcome.Failed, $"They took longer than {TestTimeout.TotalMinutes:0} minutes.");
        }
        catch (MiradorException e)
        {
            return new BuildCheck(name, CheckOutcome.Failed, e.Message);
        }

        if (code == 0 && noTests)
        {
            return new BuildCheck(name, CheckOutcome.Skipped, "This build has no unit tests (ctest found none).");
        }

        return code == 0
            ? new BuildCheck(name, CheckOutcome.Passed, "ctest passed.")
            : new BuildCheck(name, CheckOutcome.Failed,
                failures.Count > 0 ? $"Failed: {string.Join(", ", failures)}." : $"ctest failed (exit code {code}).");
    }
}

/// <summary>Reading one entry of a CMake configure cache (<c>CMakeCache.txt</c>).</summary>
public static class CMakeCache
{
    /// <summary>The value of <paramref name="key"/> (any type), or null when it isn't there.</summary>
    public static string? Read(string cacheFile, string key)
    {
        foreach (var line in File.ReadLines(cacheFile))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal))
            {
                continue;
            }

            var equals = line.IndexOf('=', key.Length);
            if (equals > 0)
            {
                return line[(equals + 1)..];
            }
        }

        return null;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(BuildCheckResult))]
internal sealed partial class BuildJsonContext : JsonSerializerContext;
