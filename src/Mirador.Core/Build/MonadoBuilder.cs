using System.Globalization;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;

namespace Mirador.Core.Build;

/// <summary>How a build ended.</summary>
/// <param name="Ok">Whether the build (not the checks) passed and the profile now runs it.</param>
/// <param name="Message">What happened, in plain words.</param>
/// <param name="Commit">The commit built, when the source was fetched.</param>
/// <param name="Checks">The checks of a build that passed; null otherwise.</param>
/// <param name="Log">The build's log file.</param>
public sealed record BuildOutcome(bool Ok, string Message, string? Commit, BuildCheckResult? Checks, string Log);

/// <summary>
/// Builds a branch profile's Monado (Mirador.md §4.11 item 49, Mirador_Plan.md Task 61): clones or pulls the branch into
/// <c>Builds\&lt;profile&gt;\src</c>, builds it in <c>…\build</c> with Mirador's recipe and installs it in
/// <c>…\install</c>, then runs the checks. The last good install stays until a new build passes; the checks only warn
/// (a build cancelled during them stays: it built).
/// Each build's log is kept in <c>…\logs</c>. One build at a time per profile.
/// </summary>
public sealed class MonadoBuilder(ICommandRunner runner, MonadoLocator locator, BuildTools tools, BuildChecks checks, TimeProvider time)
{
    /// <summary>How many build logs are kept per profile.</summary>
    public const int KeptLogs = 10;

    private readonly HashSet<string> building = [];

    /// <summary>Whether <paramref name="profileId"/> is being built now.</summary>
    public bool IsBuilding(string profileId)
    {
        lock (building)
        {
            return building.Contains(profileId);
        }
    }

    /// <summary>The newest build log of <paramref name="profileId"/>, or null.</summary>
    public string? LastLog(string profileId)
    {
        var logs = Path.Combine(locator.BranchDir(profileId), "logs");
        return Directory.Exists(logs) ? Directory.GetFiles(logs, "build-*.log").OrderDescending(StringComparer.Ordinal).FirstOrDefault() : null;
    }

    /// <summary>
    /// Fetches <paramref name="profile"/>'s branch and builds it, passing every line to <paramref name="onLine"/>.
    /// Cancelling ends the build and keeps the last good install.
    /// </summary>
    /// <exception cref="OperationCanceledException">The build was cancelled; the last good install is untouched.</exception>
    public async Task<BuildOutcome> BuildAsync(Profile profile, Action<string> onLine, CancellationToken ct)
    {
        var source = profile.Monado;
        if (source.Kind != SourceKind.Branch)
        {
            throw new ArgumentException("Only a branch profile is built by Mirador.", nameof(profile));
        }

        lock (building)
        {
            if (!building.Add(profile.Id))
            {
                throw new MiradorException("This profile is being built already.");
            }
        }

        try
        {
            return await RunAsync(profile.Id, source, onLine, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (building)
            {
                building.Remove(profile.Id);
            }
        }
    }

    private async Task<BuildOutcome> RunAsync(string profileId, BuildSource source, Action<string> onLine, CancellationToken ct)
    {
        var root = locator.BranchDir(profileId);
        var src = Path.Combine(root, "src");
        var build = Path.Combine(root, "build");
        var install = locator.BranchInstallDir(profileId);
        var fresh = install + ".new";
        var logs = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
        var logPath = Path.Combine(logs, $"build-{time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
        PruneLogs(logs);

        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var gate = new object();
        void Line(string line)
        {
            lock (gate)
            {
                log.WriteLine(line);
            }

            onLine(line);
        }

        if (BranchLink.Problem(source) is { } problem)
        {
            Line(problem);
            return new BuildOutcome(false, problem, null, null, logPath);
        }

        Line($"Building {source.Url} {source.Ref} for this profile.");

        // 1. The source: fetched shallow, the branch's tip checked out, anything left from an earlier build removed.
        var commit = await FetchAsync(src, source, Line, ct).ConfigureAwait(false);
        if (commit is null)
        {
            const string fetchFailed = "The branch couldn't be fetched. Check the link and the branch, and the network. The build log has git's own words.";
            return new BuildOutcome(false, fetchFailed, null, null, logPath);
        }

        // 2. The recipe, into a new install folder: the last good one stays until this one is built. The configure cache
        //    starts afresh, so an option taken out of the profile (or another fork's) doesn't linger; objects are kept.
        DeleteFolder(fresh);
        TryDelete(Path.Combine(build, "CMakeCache.txt"));
        var optionsFile = Path.Combine(root, "cmake-options.txt");
        await File.WriteAllLinesAsync(optionsFile, source.Options, CancellationToken.None).ConfigureAwait(false);
        var arguments = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tools.Recipe,
            "-Source", src, "-Build", build, "-Install", fresh, "-ExtraCMakeOptionsFile", optionsFile,
        };
        if (tools.ExistingToolsDir is { } toolsDir)
        {
            arguments.AddRange(["-Tools", toolsDir]);
        }

        int code;
        try
        {
            code = await runner.RunAsync(new CommandSpec(tools.PowerShell, arguments, root), Line, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Line("Cancelled. The last good build stays.");
            TryDeleteFolder(fresh);
            throw;
        }
        catch (MiradorException e)
        {
            Line(e.Message);
            return new BuildOutcome(false, e.Message, commit, null, logPath);
        }

        if (code != 0)
        {
            TryDeleteFolder(fresh);
            var failed = $"The build failed (exit code {code}). The last good build stays; the build log says what went wrong.";
            Line(failed);
            return new BuildOutcome(false, failed, commit, null, logPath);
        }

        // 3. The new install replaces the last one: moved aside whole first, which fails cleanly (and keeps it) while
        //    Monado runs from it, where deleting it would stop half way.
        var old = install + ".old";
        try
        {
            DeleteFolder(old);
            if (Directory.Exists(install))
            {
                Directory.Move(install, old);
            }

            Directory.Move(fresh, install);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(install) && Directory.Exists(old))
            {
                Directory.Move(old, install);
            }

            var busy = $"The new build couldn't replace the last one ({e.Message.TrimEnd('.')}). Stop Monado and build again.";
            Line(busy);
            return new BuildOutcome(false, busy, commit, null, logPath);
        }

        TryDeleteFolder(old);

        // 4. The checks: they warn, they don't undo the build.
        var result = await checks.RunAsync(install, build, tools.Ctest, commit, Line, ct).ConfigureAwait(false);
        result.Save(install);
        var done = $"Built {commit[..Math.Min(10, commit.Length)]}. {result.Summary()}";
        Line(done);
        return new BuildOutcome(true, done, commit, result, logPath);
    }

    /// <summary>The commit checked out, or null when git failed.</summary>
    private async Task<string?> FetchAsync(string src, BuildSource source, Action<string> line, CancellationToken ct)
    {
        Directory.CreateDirectory(src);

        // Never a prompt for a name or password (a private repository just fails), and no helper of the user's asked.
        var environment = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" };
        async Task<int> Git(params string[] args)
        {
            line("> git " + string.Join(' ', args));
            return await runner.RunAsync(
                new CommandSpec(tools.Git, ["-c", "credential.helper=", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", .. args], src, environment),
                line,
                ct).ConfigureAwait(false);
        }

        if (!Directory.Exists(Path.Combine(src, ".git")) && await Git("init", "-q").ConfigureAwait(false) != 0)
        {
            return null;
        }

        RemoveStaleLocks(Path.Combine(src, ".git"), line);

        // The URL is set every time: the profile's link may have changed since the last build.
        await Git("remote", "remove", "origin").ConfigureAwait(false);
        if (await Git("remote", "add", "origin", source.Url!).ConfigureAwait(false) != 0
            || await Git("fetch", "--depth", "1", "--no-tags", "origin", source.Ref!).ConfigureAwait(false) != 0
            || await Git("checkout", "-q", "--force", "--detach", "FETCH_HEAD").ConfigureAwait(false) != 0
            || await Git("clean", "-q", "-ffdx").ConfigureAwait(false) != 0)
        {
            return null;
        }

        string? commit = null;
        var code = await runner.RunAsync(
            new CommandSpec(tools.Git, ["rev-parse", "HEAD"], src, environment),
            l =>
            {
                line(l);
                if (l.Length == 40 && l.All(char.IsAsciiHexDigitLower))
                {
                    commit = l;
                }
            },
            ct).ConfigureAwait(false);
        return code == 0 ? commit : null;
    }

    /// <summary>
    /// The lock files a git that was ended mid-command left (a cancelled build kills the process tree, so git can't
    /// remove its own): with one there, every later fetch or checkout of the profile fails. Only one build of a profile
    /// runs at a time, so no git of Mirador's holds them now.
    /// </summary>
    internal static void RemoveStaleLocks(string gitDir, Action<string> line)
    {
        foreach (var name in new[] { "index.lock", "shallow.lock", "config.lock", "HEAD.lock", "FETCH_HEAD.lock", "packed-refs.lock" })
        {
            var path = Path.Combine(gitDir, name);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    line($"Removed {name}, left by a git that was stopped.");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                line($"Couldn't remove {path}: {e.Message}");
            }
        }
    }

    private static void PruneLogs(string logs)
    {
        foreach (var old in Directory.GetFiles(logs, "build-*.log").OrderDescending(StringComparer.Ordinal).Skip(KeptLogs - 1))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            DeleteFolder(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A program the build started may still be letting go of it; the next build clears it.
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
