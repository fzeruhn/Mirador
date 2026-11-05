using System.Text.Json.Serialization;

namespace Mirador.Core;

/// <summary>Mirador's own settings, in <c>settings.json</c>.</summary>
/// <param name="CloseToTray">Closing the window keeps Mirador in the tray (otherwise it quits).</param>
/// <param name="CheckForUpdates">Ask GitHub for a newer release at most once a day.</param>
/// <param name="LastUpdateCheck">When it last asked.</param>
/// <param name="OpenVrOnMonado">The user switched OpenVR games to Monado (Mirador.md §4.6 item 22), so a paths file that
/// no longer names Mirador's copy first was changed by another program.</param>
public sealed record AppSettings(
    bool CloseToTray = true, bool CheckForUpdates = true, DateTimeOffset? LastUpdateCheck = null, bool OpenVrOnMonado = false);

/// <summary>Keeps the <see cref="AppSettings"/> in one JSON file.</summary>
public sealed class AppSettingsStore(string path)
{
    /// <summary>The saved settings; a missing file gives the defaults, one that can't be read is kept as <c>.bad</c>.</summary>
    public AppSettings Load() => JsonFile.Load(path, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();

    public void Save(AppSettings settings) => JsonFile.Save(path, settings, AppSettingsJsonContext.Default.AppSettings);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
