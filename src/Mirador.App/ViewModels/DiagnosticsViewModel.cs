using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Mirador.App.Services;
using Mirador.Core.Diagnostics;
using Mirador.Core.Profiles;
using Mirador.Core.Session;
using Mirador.DreamAir;

namespace Mirador.App.ViewModels;

/// <summary>
/// Diagnostics: Monado's and Mirador's logs as they grow, the versions, the display check for a Dream Air, and the
/// zip to send with a problem.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    /// <summary>The most lines each log box holds.</summary>
    public const int MaxLines = 2000;

    private readonly AppHost host;
    private readonly DispatcherQueue ui;
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

    /// <summary>Raised on the UI thread after a log box got new lines, so the page can scroll it to the end.</summary>
    public event Action? LogsGrew;

    [ObservableProperty]
    public partial string MonadoLog { get; set; } = "";

    [ObservableProperty]
    public partial string MiradorLog { get; set; } = "";

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
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasCheckResult => CheckResult is not null;

    public bool HasMessage => Message is not null;

    public void Attach()
    {
        lock (monado)
        {
            monado.Clear();
            foreach (var line in host.Session.LogTail)
            {
                Add(monado, line);
            }

            mirador.Clear();
            foreach (var line in AppLog.Tail)
            {
                Add(mirador, line);
            }

            monadoChanged = miradorChanged = true;
        }

        host.Session.LogLine += OnMonadoLine;
        host.Session.Changed += OnSessionChanged;
        AppLog.LineWritten += OnMiradorLine;
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

    [RelayCommand(CanExecute = nameof(CanCheckDisplay))]
    private async Task CheckDisplayAsync()
    {
        if (Profile() is not { } profile || host.Modules.GetValueOrDefault(DreamAirModule.ModuleId) is not DreamAirModule module)
        {
            return;
        }

        IsBusy = true;
        CanCheckDisplay = false;
        CheckResult = null;
        try
        {
            var (check, pimaxProblem) = await module.CheckDisplayAsync(host.Context, profile, CancellationToken.None);
            CheckPassed = check.Pass;
            var verdict = check.Pass
                ? $"Direct mode works: {check.Message}"
                : check.Finished
                    ? $"Direct mode doesn't work: {check.Message}{(check.WhatToDo is null ? "" : " What to do: " + check.WhatToDo)}"
                    : check.Message;
            CheckResult = pimaxProblem is null ? verdict : $"{verdict} {pimaxProblem}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

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

        IsBusy = true;
        try
        {
            var module = profile.ModuleId is { } id ? host.Modules.GetValueOrDefault(id) : null;
            var versions = AppInfo.Versions(profile, host.AppDir);
            var environment = host.Session.LastMiradorEnvironment;
            await Task.Run(() => DiagnosticsZip.Collect(host.Paths, profile, environment, versions, module, host.Context).Save(path));
            Message = null;
            CheckResult = $"Saved {path}.";
            CheckPassed = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Mirador couldn't save {path} ({e.Message.Trim().TrimEnd('.')}).";
        }
        finally
        {
            IsBusy = false;
        }
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
        IsDreamAir = profile?.ModuleId == DreamAirModule.ModuleId;
        CanCheckDisplay = IsDreamAir && !IsBusy && host.Session.State == SessionState.Stopped;
        Versions = profile is null ? $"Mirador {AppInfo.Version}" : AppInfo.Versions(profile, host.AppDir).TrimEnd();
    }

    private void OnSessionChanged() => ui.TryEnqueue(Refresh);

    private void OnMonadoLine(string line)
    {
        lock (monado)
        {
            Add(monado, line);
            monadoChanged = true;
        }
    }

    private void OnMiradorLine(string line)
    {
        lock (monado)
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

    /// <summary>Puts new lines into the boxes, at most four times a second: a busy log doesn't redraw at every line.</summary>
    private void Flush()
    {
        string? monadoText = null, miradorText = null;
        lock (monado)
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

        if (monadoText is not null)
        {
            MonadoLog = monadoText;
        }

        if (miradorText is not null)
        {
            MiradorLog = miradorText;
        }

        if (monadoText is not null || miradorText is not null)
        {
            LogsGrew?.Invoke();
        }
    }
}
