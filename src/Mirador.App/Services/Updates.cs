using Mirador.Core;
using Mirador.Core.Diagnostics;
using Mirador.Core.OpenVr;

namespace Mirador.App.Services;

/// <summary>
/// What a check for updates found (Mirador.md §4.8 item 37).
/// </summary>
/// <param name="At">When it was made.</param>
/// <param name="Reached">Whether GitHub answered for both repositories.</param>
/// <param name="Mirador">This Mirador's version.</param>
/// <param name="NewerMirador">Mirador's latest full release when it is newer; null otherwise (also when GitHub couldn't be asked).</param>
/// <param name="OpenComposite">The OpenComposite in use: its release version (or commit); null when this build has none.</param>
/// <param name="NewerOpenComposite">OpenComposite's latest full release when it is newer; null otherwise.</param>
/// <param name="NewerMonado">
/// Mirador's Monado's newest weekly build when it isn't the one installed (Mirador.md §4.11 item 50); null otherwise, and when
/// no profile runs Mirador's Monado.
/// </param>
public sealed record UpdateReport(
    DateTimeOffset At,
    bool Reached,
    string Mirador,
    Release? NewerMirador,
    string? OpenComposite,
    Release? NewerOpenComposite,
    string? NewerMonado = null)
{
    /// <summary>The report in words, for the "Updates" card.</summary>
    public string Describe()
    {
        if (!Reached)
        {
            return "Couldn't reach GitHub.";
        }

        var parts = new List<string>
        {
            NewerMirador is { } m ? $"Mirador {Plain(m.Tag)} is out." : $"Mirador {Mirador} is up to date.",
        };
        if (NewerOpenComposite is { } o)
        {
            parts.Add($"OpenComposite {Plain(o.Tag)} is out.");
        }
        else if (OpenComposite is not null)
        {
            parts.Add($"OpenComposite {OpenComposite} is up to date.");
        }

        if (NewerMonado is { } monado)
        {
            parts.Add($"The bundled Monado {monado} is out: update it on the Profiles page.");
        }

        parts.Add($"Checked at {At.ToLocalTime():HH:mm}.");
        return string.Join(' ', parts);
    }

    /// <summary>A tag without its <c>v</c>.</summary>
    public static string Plain(string tag) => tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
}

/// <summary>
/// The check for newer releases of Mirador and of OpenComposite (Mirador.md §4.8 items 37–38): once a day at most at
/// launch when it is on, and "Check now". It only asks and reports; Mirador installs nothing by itself.
/// </summary>
public sealed class Updates(AppHost host)
{
    /// <summary>The last report made in this run; null before the first.</summary>
    public UpdateReport? Last { get; private set; }

    /// <summary>
    /// Why the last check couldn't be written down in the settings (so that the daily check would ask again at the next
    /// launch rather than in a day); null when it could.
    /// </summary>
    public string? RecordProblem { get; private set; }

    /// <summary>Raised, on whichever thread made the check, when <see cref="Last"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// The OpenComposite Mirador runs (the one place that says so): the per-user copy's, else the bundle's.
    /// </summary>
    private OpenCompositeSource? Installed() => host.OpenCompositeCopy.CopySource;

    /// <summary>Whether OpenComposite can be updated from the notice and the Updates card.</summary>
    public bool CanUpdateOpenComposite => true;

    /// <summary>
    /// The "Update" buttons: downloads <paramref name="release"/>'s Windows zip, checks it against
    /// <see cref="Release.WindowsZipSha256"/> and puts it in the per-user copy, then asks GitHub again.
    /// </summary>
    /// <returns>What happened, in words for the user.</returns>
    /// <exception cref="MiradorException">Why nothing changed.</exception>
    public async Task<string> UpdateOpenCompositeAsync(Release release)
    {
        var copy = host.OpenCompositeCopy;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mirador/{AppInfo.Version}");
        var folder = await OpenCompositeDownload.FetchAsync(http, release, copy.DownloadRoot, CancellationToken.None);

        CopyOutcome outcome;
        try
        {
            outcome = await Task.Run(() => copy.Install(folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MiradorException($"Mirador couldn't put OpenComposite in place ({e.Message.Trim().TrimEnd('.')})");
        }

        var message = outcome == CopyOutcome.InUse
            ? "OpenComposite will be updated when no game uses it."
            : $"OpenComposite {UpdateReport.Plain(release.Tag)} is in place.";
        AppLog.Write(message);
        await CheckAsync();
        return message;
    }

    /// <summary>
    /// "Get OpenComposite" (Mirador.md §4.11 item 56): the fork's latest release, downloaded and checked like an update.
    /// </summary>
    /// <returns>What happened, in words for the user.</returns>
    /// <exception cref="MiradorException">Why nothing changed.</exception>
    public async Task<string> GetOpenCompositeAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var (answered, latest) = await UpdateCheck.LatestAsync(http, UpdateCheck.OpenCompositeRepository, AppInfo.Version, CancellationToken.None);
        if (!answered)
        {
            throw new MiradorException("Mirador couldn't reach GitHub. Try again later.");
        }

        return latest is null
            ? throw new MiradorException("The OpenComposite fork has no release yet.")
            : await UpdateOpenCompositeAsync(latest);
    }

    /// <summary>"Roll back": the OpenComposite before the one in use, then asks GitHub again.</summary>
    /// <returns>What happened, in words for the user.</returns>
    /// <exception cref="MiradorException">Why nothing changed.</exception>
    public async Task<string> RollBackOpenCompositeAsync()
    {
        CopyOutcome outcome;
        try
        {
            outcome = await Task.Run(host.OpenCompositeCopy.RollBack);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MiradorException($"Mirador couldn't put the earlier OpenComposite back ({e.Message.Trim().TrimEnd('.')})");
        }

        var message = outcome switch
        {
            CopyOutcome.InUse => "A game is using OpenComposite. Close it and try again.",
            CopyOutcome.NothingEarlier => "There is no earlier OpenComposite to go back to.",
            _ => $"OpenComposite {host.OpenCompositeCopy.CopySource?.Describe()} is in place again.",
        };
        AppLog.Write(message);
        await CheckAsync();
        return message;
    }

    /// <summary>
    /// The newest weekly build of Mirador's Monado when a profile runs Mirador's Monado and a newer one than the installed is
    /// out; never installed from here (Mirador.md §4.11 item 50: "Never installed by itself").
    /// </summary>
    private async Task<string?> NewerMonadoAsync(HttpClient http)
    {
        try
        {
            if (!host.Profiles.Load().Profiles.Any(p => p.Monado.Kind == Core.Profiles.SourceKind.Mirador))
            {
                return null;
            }

            var newest = (await Ipc.MonadoReleaseList.FetchAsync(http, AppInfo.Version, CancellationToken.None))?.FirstOrDefault();
            var current = Ipc.MiradorMonado.Read(host.Locator.MiradorRoot).Current;
            return newest is not null && newest.Version != current ? newest.Version : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The daily check at launch: only when it is on and a day has passed since the last. Null when it didn't ask.</summary>
    public async Task<UpdateReport?> CheckDailyAsync()
    {
        AppSettings settings;
        try
        {
            settings = host.Settings.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't read the settings for the update check", ex);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (!settings.CheckForUpdates || (settings.LastUpdateCheck is { } last && now - last < TimeSpan.FromDays(1) && last <= now))
        {
            return null;
        }

        return await CheckAsync();
    }

    /// <summary>Asks GitHub about both. When both answered, the check is recorded as the daily one.</summary>
    public async Task<UpdateReport> CheckAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var installed = Installed();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var (mAnswered, mLatest) = await UpdateCheck.LatestAsync(http, UpdateCheck.MiradorRepository, AppInfo.Version, CancellationToken.None);
        var (oAnswered, oLatest) = await UpdateCheck.LatestAsync(http, UpdateCheck.OpenCompositeRepository, AppInfo.Version, CancellationToken.None);

        var newerMonado = await NewerMonadoAsync(http);
        var report = new UpdateReport(
            now,
            mAnswered && oAnswered,
            AppInfo.Version,
            mLatest is { } m && UpdateCheck.IsNewer(AppInfo.Version, m.Tag) ? m : null,
            installed is null ? null : installed.Version ?? installed.Describe(),
            oLatest is { } o && installed is not null && (installed.Version is null || UpdateCheck.IsNewer(installed.Version, o.Tag)) ? o : null,
            newerMonado);

        if (report.Reached)
        {
            RecordProblem = SettingsWrite(now);
        }

        // Offline or refused: not recorded, so the next launch asks again rather than in a day.
        Last = report;

        if (report.NewerMirador is { } newerMirador)
        {
            AppLog.Write($"Mirador {newerMirador.Tag} is out: {newerMirador.Url}");
        }

        if (report.NewerOpenComposite is { } newerOpenComposite)
        {
            AppLog.Write($"OpenComposite {newerOpenComposite.Tag} is out: {newerOpenComposite.Url}");
        }

        if (!report.Reached)
        {
            AppLog.Write("Update check: couldn't reach GitHub");
        }

        Changed?.Invoke();
        return report;
    }

    private string? SettingsWrite(DateTimeOffset now)
    {
        try
        {
            host.Settings.Save(host.Settings.Load() with { LastUpdateCheck = now });
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("Couldn't record the update check", ex);
            return $"Mirador couldn't record the update check ({ex.Message.Trim().TrimEnd('.')}).";
        }
    }
}
