using System.Text.Json.Serialization;

namespace Mirador.Core;

/// <summary>Mirador's own settings, in <c>settings.json</c>.</summary>
/// <param name="CloseToTray">Closing the window keeps Mirador in the tray (otherwise it quits).</param>
/// <param name="CheckForUpdates">Ask GitHub for a newer release at most once a day.</param>
/// <param name="LastUpdateCheck">When it last asked.</param>
/// <param name="BaseStationsOnAtStart">Switch the selected base stations on when a session starts.</param>
/// <param name="BaseStationsOffAtStop">Switch them off again when the session ends.</param>
/// <param name="BaseStationsOff">How they are switched off.</param>
public sealed record AppSettings(
    bool CloseToTray = true,
    bool CheckForUpdates = true,
    DateTimeOffset? LastUpdateCheck = null,
    bool BaseStationsOnAtStart = true,
    bool BaseStationsOffAtStop = true,
    OffMode BaseStationsOff = OffMode.Sleep);

/// <summary>How a base station is switched off.</summary>
public enum OffMode
{
    /// <summary>Asleep: the station takes a moment to wake up again.</summary>
    Sleep,

    /// <summary>Stand by: the station stays awake and starts again quickly.</summary>
    Standby,
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
