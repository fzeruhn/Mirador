using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mirador.App.Services;
using Mirador.Core.DirectMode;

namespace Mirador.App.ViewModels;

/// <summary>One row of the Installed page: a part and its version, or why it isn't there.</summary>
public sealed record InstalledItem(string Name, string Detail);

/// <summary>
/// The Installed page: what Mirador has installed and at which version: Mirador itself, the bundled Monado,
/// OpenComposite and the direct-mode layer, then the build tools a branch build installs when it needs them.
/// </summary>
public sealed partial class InstalledViewModel(AppHost host) : ObservableObject
{
    public ObservableCollection<InstalledItem> Parts { get; } = [];

    public ObservableCollection<InstalledItem> BuildTools { get; } = [];

    /// <summary>Why the build tools couldn't be read, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    public bool HasProblem => Problem is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Reads everything again: the parts at once, the build tools (vswhere) off the UI thread.</summary>
    public async Task RefreshAsync()
    {
        var versions = Ipc.MiradorMonado.Read(host.Locator.MiradorRoot);
        Parts.Clear();
        Parts.Add(new InstalledItem("Mirador", AppInfo.Version));
        Parts.Add(new InstalledItem(
            "Bundled Monado",
            versions.Current is not { } current ? "Not installed: the Profiles page installs it."
            : versions.Previous is { } previous ? $"{current} (can roll back to {previous})"
            : current));
        Parts.Add(new InstalledItem(
            "OpenComposite",
            host.OpenComposite.Source is { } source && source.Describe() is { Length: > 0 } described ? described
            : host.OpenComposite.IsInstalled ? "Installed, version unknown"
            : "Not installed: Home's OpenVR card gets it."));
        Parts.Add(new InstalledItem(
            "Direct-mode layer",
            File.Exists(Path.Combine(host.AppDir, "layer", DirectModeLayer.DllName))
                ? $"Came with Mirador {AppInfo.Version}"
                : "Not in this copy of Mirador."));

        IsLoading = true;
        try
        {
            var report = await Task.Run(() => host.BuildToolsSetup.CheckAsync(CancellationToken.None));
            BuildTools.Clear();
            foreach (var part in report.Parts)
            {
                BuildTools.Add(new InstalledItem(part.Name, part.Detail));
            }

            Problem = null;
        }
        catch (Exception e)
        {
            if (e is not Core.MiradorException)
            {
                AppLog.Write("Reading the build tools failed", e);
            }

            Problem = $"Mirador couldn't read the build tools ({e.Message.Trim().TrimEnd('.')}).";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
