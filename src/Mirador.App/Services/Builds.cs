using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Profiles;

namespace Mirador.App.Services;

/// <summary>One build of a profile's branch: its lines so far and how it ended.</summary>
public sealed class BuildRun
{
    private readonly List<string> lines = [];

    internal CancellationTokenSource Cancel { get; } = new();

    /// <summary>The build's lines so far (the last <see cref="Builds.ShownLines"/>).</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines)
            {
                return [.. lines];
            }
        }
    }

    /// <summary>How it ended; null while it runs.</summary>
    public BuildOutcome? Outcome { get; internal set; }

    /// <summary>Whether it was cancelled.</summary>
    public bool Cancelled { get; internal set; }

    internal void Add(string line)
    {
        lock (lines)
        {
            lines.Add(line);
            if (lines.Count > Builds.ShownLines)
            {
                lines.RemoveRange(0, lines.Count - Builds.ShownLines);
            }
        }
    }
}

/// <summary>
/// The branch builds of this Mirador (Mirador_Plan.md Task 61), kept here rather than in the Profiles page so that a
/// build goes on while the owner looks at another page. <see cref="Changed"/> is raised on a background thread.
/// </summary>
public sealed class Builds(MonadoBuilder builder)
{
    /// <summary>How many of a build's last lines the page shows; the log file has them all.</summary>
    public const int ShownLines = 400;

    private readonly Dictionary<string, BuildRun> runs = [];

    /// <summary>A build started, wrote a line or ended: the profile's id.</summary>
    public event Action<string>? Changed;

    /// <summary>The profile's running or last build in this Mirador's run, or null.</summary>
    public BuildRun? RunOf(string profileId)
    {
        lock (runs)
        {
            return runs.GetValueOrDefault(profileId);
        }
    }

    /// <summary>Whether any profile's build is running (the build tools aren't changed meanwhile).</summary>
    public bool AnyRunning
    {
        get
        {
            lock (runs)
            {
                return runs.Values.Any(r => r is { Outcome: null, Cancelled: false });
            }
        }
    }

    /// <summary>The newest build log of a profile, from this or an earlier run of Mirador.</summary>
    public string? LastLog(string profileId) => builder.LastLog(profileId);

    /// <summary>Starts building <paramref name="profile"/>'s branch unless it is being built.</summary>
    public void Start(Profile profile)
    {
        BuildRun run;
        lock (runs)
        {
            if (runs.GetValueOrDefault(profile.Id) is { Outcome: null, Cancelled: false })
            {
                return;
            }

            run = new BuildRun();
            runs[profile.Id] = run;
        }

        Changed?.Invoke(profile.Id);
        _ = Task.Run(async () =>
        {
            try
            {
                run.Outcome = await builder.BuildAsync(profile, line =>
                {
                    run.Add(line);
                    Changed?.Invoke(profile.Id);
                }, run.Cancel.Token);
            }
            catch (OperationCanceledException)
            {
                run.Cancelled = true;
            }
            catch (Exception e)
            {
                // Whatever went wrong, the build has ended: the page must not stay on "Building…".
                AppLog.Write("A branch build failed", e);
                run.Add(e.Message);
                run.Outcome = new BuildOutcome(false, e.Message, null, null, builder.LastLog(profile.Id) ?? "");
            }

            Changed?.Invoke(profile.Id);
        });
    }

    /// <summary>Cancels the profile's running build; the last good build stays.</summary>
    public void Cancel(string profileId) => RunOf(profileId)?.Cancel.Cancel();
}
