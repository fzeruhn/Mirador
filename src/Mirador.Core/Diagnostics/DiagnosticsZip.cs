using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.Diagnostics;

/// <summary>
/// The zip a user saves to report a problem (Mirador.md §4.2 item 8): logs, the profile, the settings, the environment
/// Mirador gave Monado and the versions, plus what the headset module adds. Files are read as they are, even while a
/// log is being written; one that is missing is left out and named in <c>missing.txt</c>.
/// </summary>
public sealed class DiagnosticsZip : IDiagnosticsSink
{
    private readonly List<(string Entry, string? File, string? Text)> entries = [];

    /// <summary>The entries added so far, in order.</summary>
    public IReadOnlyList<string> EntryNames => entries.Select(e => e.Entry).ToList();

    public void AddFile(string entryName, string path) => entries.Add((entryName, path, null));

    public void AddText(string entryName, string text) => entries.Add((entryName, null, text));

    /// <summary>
    /// Gathers the usual entries: the newest <c>monado.log</c>, <c>mirador.log</c>, <c>profile.json</c> (the selected
    /// profile), <c>monado-settings.json</c>, <c>environment.txt</c> (what Mirador set at the last Start, one
    /// <c>NAME=value</c> a line), <c>versions.txt</c>, then the module's.
    /// </summary>
    /// <param name="lastEnvironment">The session's <c>LastMiradorEnvironment</c>; null before the first Start.</param>
    /// <param name="versions">Mirador's, Windows' and the profile's Monado's versions, one a line.</param>
    public static DiagnosticsZip Collect(
        AppPaths paths, Profile profile, IReadOnlyDictionary<string, string>? lastEnvironment, string versions, IHeadsetModule? module, ModuleContext ctx)
    {
        var zip = new DiagnosticsZip();
        zip.AddFile("monado.log", NewestMonadoLog(paths.LogsDir) ?? Path.Combine(paths.LogsDir, "monado-*.log"));
        zip.AddFile("mirador.log", paths.AppLogFile);
        zip.AddText("profile.json", System.Text.Json.JsonSerializer.Serialize(profile, DiagnosticsJsonContext.Default.Profile));
        zip.AddFile("monado-settings.json", paths.MonadoSettingsFile);
        zip.AddText(
            "environment.txt",
            lastEnvironment is null
                ? "Monado hasn't been started since Mirador started.\n"
                : string.Concat(lastEnvironment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}\n")));
        zip.AddText("versions.txt", versions);
        module?.AddDiagnostics(ctx, profile, zip);
        return zip;
    }

    /// <summary>Writes the zip to <paramref name="zipPath"/>, replacing a file there.</summary>
    /// <exception cref="IOException">The zip couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The zip couldn't be written there.</exception>
    public void Save(string zipPath)
    {
        var missing = new StringBuilder();
        var temp = zipPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (entry, file, text) in entries)
            {
                if (text is not null)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(entry).Open(), new UTF8Encoding(false));
                    writer.Write(text);
                    continue;
                }

                try
                {
                    using var source = new FileStream(file!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = archive.CreateEntry(entry).Open();
                    source.CopyTo(target);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    missing.Append(entry).Append(": ").Append(file).Append(" isn't there\n");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    missing.Append(entry).Append(": ").Append(file).Append(" couldn't be read (").Append(e.Message.Trim()).Append(")\n");
                }
            }

            if (missing.Length > 0)
            {
                using var writer = new StreamWriter(archive.CreateEntry("missing.txt").Open(), new UTF8Encoding(false));
                writer.Write(missing.ToString());
            }
        }

        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>The newest <c>monado-yyyyMMdd-HHmmss.log</c> (their names sort by time); null when there is none.</summary>
    public static string? NewestMonadoLog(string logsDir) =>
        Directory.Exists(logsDir)
            ? Directory.EnumerateFiles(logsDir, "monado-*.log").OrderBy(Path.GetFileName, StringComparer.Ordinal).LastOrDefault()
            : null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(Profile))]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
