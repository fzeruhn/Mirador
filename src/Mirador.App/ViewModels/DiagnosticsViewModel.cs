using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Diagnostics;
using Mirador.Core.OpenVr;
using Mirador.Core.Profiles;
using Mirador.Core.Session;
using Mirador.Pimax;

namespace Mirador.App.ViewModels;

/// <summary>
/// Diagnostics: Monado's and Mirador's logs as they grow, the versions, the display check for a Dream Air, and the
/// zip to send with a problem. The page is kept while Mirador runs, so a check under way survives leaving it.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    /// <summary>The most lines each log box holds.</summary>
    public const int MaxLines = 2000;

    private readonly AppHost host;
    private readonly DispatcherQueue ui;
    private readonly object gate = new();
    private readonly Queue<string> monado = new();
    private readonly Queue<string> mirador = new();
    private readonly DispatcherQueueTimer flush;
    private bool monadoChanged;
    private bool miradorChanged;

    public DiagnosticsViewModel(AppHost host, DispatcherQueue ui)
    {
        this.host = host;
        this.ui = ui;
        flush = ui.CreateTimer();
        flush.Interval = TimeSpan.FromMilliseconds(250);
        flush.Tick += (_, _) => Flush();
    }

    /// <summary>
    /// Raised on the UI thread with the new text of the boxes that changed (null for one that didn't): the page puts
    /// it in, unless the user is selecting there, and follows the end only of a box that was at its end.
    /// </summary>
    public event Action<string?, string?>? LogsChanged;

    [ObservableProperty]
    public partial string Versions { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDisplayCommand))]
    public partial bool CanCheckDisplay { get; set; }

    [ObservableProperty]
    public partial bool IsDreamAir { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckResult))]
    public partial string? CheckResult { get; set; }

    [ObservableProperty]
    public partial bool CheckPassed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaved))]
    public partial string? Saved { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDisplayCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Whether a profile has a "Verify build for Windows" result.</summary>
    [ObservableProperty]
    public partial bool HasVerify { get; set; }

    /// <summary>The newest Verify result's profile, time and outcome.</summary>
    [ObservableProperty]
    public partial string VerifySummary { get; set; } = "";

    /// <summary>Each step of that result, a line each.</summary>
    [ObservableProperty]
    public partial string VerifySteps { get; set; } = "";

    /// <summary>The newest Verify run's own log (the Verify tab).</summary>
    [ObservableProperty]
    public partial string VerifyLog { get; set; } = "";

    public bool HasCheckResult => CheckResult is not null;

    public bool HasSaved => Saved is not null;

    public bool HasMessage => Message is not null;

    public void Attach()
    {
        host.Session.LogLine += OnMonadoLine;
        host.Session.Changed += OnSessionChanged;
        AppLog.LineWritten += OnMiradorLine;

        // What is on disk first (2,000 lines rather than the 200 kept in memory); lines from now on come as events.
        var monadoLines = ReadTail(DiagnosticsZip.NewestMonadoLog(host.Paths.LogsDir));
        var miradorLines = ReadTail(host.Paths.AppLogFile);
        lock (gate)
        {
            monado.Clear();
            foreach (var line in monadoLines)
            {
                Add(monado, line);
            }

            mirador.Clear();
            foreach (var line in miradorLines)
            {
                Add(mirador, line);
            }

            monadoChanged = miradorChanged = true;
        }

        flush.Start();
        Flush();
        Refresh();
    }

    public void Detach()
    {
        host.Session.LogLine -= OnMonadoLine;
        host.Session.Changed -= OnSessionChanged;
        AppLog.LineWritten -= OnMiradorLine;
        flush.Stop();
    }

    public void DismissMessage() => Message = null;

    public void DismissCheck() => CheckResult = null;

    public void DismissSaved() => Saved = null;

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(host.Paths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{host.Paths.LogsDir}\"") { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Message = $"Mirador couldn't open {host.Paths.LogsDir} ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task CheckDisplayAsync()
    {
        if (Profile() is not { } profile || host.Modules.GetValueOrDefault(PimaxModule.ModuleId) is not PimaxModule module)
        {
            return;
        }

        IsBusy = true;
        CheckResult = null;
        try
        {
            // Through the session: a Start waits for the check, and recovery restarts Pimax if Mirador goes away meanwhile.
            var (check, pimaxProblem) = await host.Session.RunWhileStoppedAsync(
                PimaxModule.ModuleId, (save, ct) => module.CheckDisplayAsync(host.Context, profile, save, ct), CancellationToken.None);
            CheckPassed = check.Pass;
            var verdict = check.Pass
                ? (check.Message == "Direct mode works." ? check.Message : $"Direct mode works: {Sentence(check.Message)}")
                : check.Finished
                    ? $"Direct mode doesn't work: {Sentence(check.Message)}{(check.WhatToDo is null ? "" : " What to do: " + Sentence(check.WhatToDo))}"
                    : Sentence(check.Message);
            CheckResult = pimaxProblem is null ? verdict : $"{verdict} {pimaxProblem}";
        }
        catch (MiradorException e)
        {
            CheckPassed = false;
            CheckResult = e.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private bool CanRunCheck() => CanCheckDisplay && !IsBusy;

    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        if (Profile() is not { } profile)
        {
            return;
        }

        var path = await Pickers.SaveFileAsync($"mirador-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}", "Zip archive", ".zip");
        if (path is null)
        {
            return;
        }

        Saved = null;
        Message = null;
        try
        {
            var module = profile.ModuleId is { } id ? host.Modules.GetValueOrDefault(id) : null;
            var versions = AppInfo.Versions(profile, host.AppDir);
            var environment = host.Session.LastMiradorEnvironment;
            var openVr = new OpenVrFiles(OpenVrPaths.DefaultFile, OpenCompositeCopy.LogFile);
            await Task.Run(() => DiagnosticsZip.Collect(host.Paths, profile, environment, versions, module, host.Context, openVr).Save(path));
            Saved = $"Saved {path}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't save {path} ({e.Message.Trim().TrimEnd('.')}).";
        }
    }

    private static string Sentence(string s) => s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? s : s + ".";

    /// <summary>The last <see cref="MaxLines"/> lines of a file that may be being written; none when it can't be read.</summary>
    private static List<string> ReadTail(string? path)
    {
        var lines = new Queue<string>();
        if (path is null)
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > MaxLines)
                {
                    lines.Dequeue();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return [.. lines];
    }

    private Profile? Profile()
    {
        try
        {
            return host.Profiles.Load().Selected;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't read its profiles ({e.Message.Trim().TrimEnd('.')}).";
            return null;
        }
    }

    private void Refresh()
    {
        var profile = Profile();
        // The display check is the Pimax module's: it stops Pimax's software around it.
        IsDreamAir = profile?.ModuleId == PimaxModule.ModuleId;
        CanCheckDisplay = IsDreamAir && host.Session.State == SessionState.Stopped;
        Versions = profile is null ? $"Mirador {AppInfo.Version}" : AppInfo.Versions(profile, host.AppDir).TrimEnd();
        RefreshVerify();
    }

    /// <summary>
    /// The newest Verify result of any profile (the run the user most likely came here from, the Profiles page points
    /// here when one fails), and the newest run's log.
    /// </summary>
    private void RefreshVerify()
    {
        IReadOnlyList<Profile> profiles;
        try
        {
            profiles = host.Profiles.Load().Profiles;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            profiles = [];
        }

        var newest = profiles
            .Select(p => (p.Name, Result: VerifyResult.Load(host.VerifyResultFile(p.Id))))
            .Where(r => r.Result is not null)
            .MaxBy(r => r.Result!.At);
        HasVerify = newest.Result is not null;
        if (newest.Result is { } result)
        {
            VerifySummary = $"{newest.Name}, {result.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}: {result.Summary()}";
            VerifySteps = string.Join(Environment.NewLine, result.Steps.Select(s => $"{s.Outcome switch
            {
                VerifyOutcome.Passed => "Passed",
                VerifyOutcome.Failed => "Failed",
                _ => "Skipped",
            }}: {s.Name}. {s.Detail}"));
        }

        var log = ReadTail(DiagnosticsZip.NewestVerifyLog(host.Paths.LogsDir));
        VerifyLog = log.Count > 0 ? string.Join(Environment.NewLine, log) : "No Verify run yet. Verify build for Windows is on the Profiles page.";
    }

    private void OnSessionChanged() => ui.TryEnqueue(Refresh);

    private void OnMonadoLine(string line)
    {
        lock (gate)
        {
            Add(monado, line);
            monadoChanged = true;
        }
    }

    private void OnMiradorLine(string line)
    {
        lock (gate)
        {
            Add(mirador, line);
            miradorChanged = true;
        }
    }

    private static void Add(Queue<string> lines, string line)
    {
        lines.Enqueue(line);
        while (lines.Count > MaxLines)
        {
            lines.Dequeue();
        }
    }

    /// <summary>Hands new lines to the page, at most four times a second: a busy log doesn't redraw at every line.</summary>
    private void Flush()
    {
        string? monadoText = null, miradorText = null;
        lock (gate)
        {
            if (monadoChanged)
            {
                monadoText = string.Join('\n', monado);
                monadoChanged = false;
            }

            if (miradorChanged)
            {
                miradorText = string.Join('\n', mirador);
                miradorChanged = false;
            }
        }

        if (monadoText is not null || miradorText is not null)
        {
            LogsChanged?.Invoke(monadoText, miradorText);
        }
    }
}
