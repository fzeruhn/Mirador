using System.Text.Json.Serialization;

namespace Mirador.Core;

/// <summary>Mirador's own settings, in <c>settings.json</c>.</summary>
/// <param name="CloseToTray">Closing the window keeps Mirador in the tray (otherwise it quits).</param>
/// <param name="CheckForUpdates">Ask GitHub for a newer release at most once a day.</param>
/// <param name="LastUpdateCheck">When it last asked.</param>
/// <param name="BaseStationsOnAtStart">Power the selected base stations on when a session starts.</param>
/// <param name="BaseStationsOffAtStop">Power them off (they sleep) when the session ends.</param>
/// <param name="BaseStationsOnAtMiradorStart">Power the selected base stations on when Mirador starts, after its scan.</param>
/// <param name="BaseStationsOffAtMiradorQuit">Power them off when Mirador quits.</param>
/// <param name="BaseStationsState">Where the stations' states shown come from: the last scan, or listening while shown.</param>
/// <param name="StartMinimized">When Windows starts Mirador at sign-in, start in the notification area (or minimized, when it doesn't close to the tray) instead of showing the window.</param>
/// <remarks>
/// A file from before 2026-11-07 may still have <c>baseStationsOff</c> ("Off means: Sleep / Standby"); it is ignored,
/// since off always means sleep now, and goes at the next save.
/// </remarks>
public sealed record AppSettings(
    bool CloseToTray = true,
    bool CheckForUpdates = true,
    DateTimeOffset? LastUpdateCheck = null,
    bool BaseStationsOnAtStart = true,
    bool BaseStationsOffAtStop = true,
    bool BaseStationsOnAtMiradorStart = false,
    bool BaseStationsOffAtMiradorQuit = false,
    StationStateMode BaseStationsState = StationStateMode.LastScan,
    bool StartMinimized = true);

/// <summary>How often Mirador follows the base stations' states ("Station state tracking rate" on the Lighthouse page).</summary>
public enum StationStateMode
{
    /// <summary>"When Relevant": Mirador listens only for a scan (at its start, or "Scan") and around a command.</summary>
    LastScan,

    /// <summary>"Constant": Mirador listens to the stations all the time it runs.</summary>
    Live,
}

/// <summary>Keeps the <see cref="AppSettings"/> in one JSON file.</summary>
public sealed class AppSettingsStore(string path)
{
    /// <summary>The saved settings; a missing file gives the defaults, one that can't be read is kept as <c>.bad</c>.</summary>
    public AppSettings Load() => JsonFile.Load(path, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();

    public void Save(AppSettings settings) => JsonFile.Save(path, settings, AppSettingsJsonContext.Default.AppSettings);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
