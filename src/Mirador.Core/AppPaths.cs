using System.Globalization;

namespace Mirador.Core;

/// <summary>Where Mirador keeps its data: files and folders under one root.</summary>
public sealed class AppPaths(string root)
{
    public string Root { get; } = root;

    public string ProfilesFile { get; } = Path.Combine(root, "profiles.json");
    public string MonadoSettingsFile { get; } = Path.Combine(root, "monado-settings.json");
    public string SettingsFile { get; } = Path.Combine(root, "settings.json");
    public string SessionFile { get; } = Path.Combine(root, "session.json");
    public string BaseStationsFile { get; } = Path.Combine(root, "basestations.json");
    public string DreamAirFile { get; } = Path.Combine(root, "dreamair.json");

    public string LogsDir { get; } = Path.Combine(root, "logs");
    public string AppLogFile { get; } = Path.Combine(root, "logs", "mirador.log");

    public string GeometryDir { get; } = Path.Combine(root, "geometry");
    public string ChecksDir { get; } = Path.Combine(root, "checks");
    public string BackupsDir { get; } = Path.Combine(root, "calibration-backups");

    /// <summary>Where branch profiles are built: <c>Builds\&lt;profile id&gt;\</c> with <c>src</c>, <c>build</c> and <c>install</c>.</summary>
    public string BuildsDir { get; } = Path.Combine(root, "Builds");

    /// <summary>Mirador's own copies of the build tools (Mirador.md §4.11 item 49): <c>vcpkg</c>, <c>cmake</c>, <c>ninja</c>, <c>git</c>.</summary>
    public string ToolsDir { get; } = Path.Combine(root, "tools");

    /// <summary>Where OpenLamina's installer is downloaded to, and deleted from once it has run.</summary>
    public string DownloadDir { get; } = Path.Combine(root, "download");

    /// <summary>The log file for a Monado session that starts at <paramref name="now"/>.</summary>
    public string NewMonadoLogFile(DateTime now) =>
        Path.Combine(LogsDir, $"monado-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");

    /// <summary><c>%LOCALAPPDATA%\Mirador</c>.</summary>
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mirador"));
}
