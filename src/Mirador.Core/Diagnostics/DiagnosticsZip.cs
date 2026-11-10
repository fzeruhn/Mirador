using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Mirador.Core.Modules;
using Mirador.Core.Profiles;

namespace Mirador.Core.Diagnostics;

/// <summary>The OpenVR files the zip takes: the user's paths file and OpenComposite's log.</summary>
public sealed record OpenVrFiles(string PathsFile, string LogFile);

/// <summary>
/// The zip a user saves to report a problem (Mirador.md §4.2 item 8): logs, the profile, the settings, the environment
/// Mirador gave Monado and the versions, plus what the headset module adds. Files are read as they are, even while a
/// log is being written; one that is missing is left out and named in <c>missing.txt</c>.
/// </summary>
public sealed class DiagnosticsZip : IDiagnosticsSink
{
    private readonly List<(string Entry, string? File, string? Text)> entries = [];
    private readonly List<string> refused = [];

    /// <summary>The entries added so far, in order.</summary>
    public IReadOnlyList<string> EntryNames => entries.Select(e => e.Entry).ToList();

    /// <summary>Adds a file; a name that isn't a plain relative one, or is taken already, is left out and noted.</summary>
    public void AddFile(string entryName, string path) => Add(entryName, path, null);

    /// <summary>Adds a text; a name that isn't a plain relative one, or is taken already, is left out and noted.</summary>
    public void AddText(string entryName, string text) => Add(entryName, null, text);

    /// <summary>Notes, in <c>missing.txt</c>, something that couldn't be added, such as a log that doesn't exist yet.</summary>
    public void AddMissing(string entryName, string why) => refused.Add($"{entryName}: {why}");

    /// <summary>
    /// Gathers the usual entries: the newest <c>monado.log</c>, <c>mirador.log</c>, the newest Verify run's
    /// <c>verify.log</c> when there is one, <c>profile.json</c> (the selected
    /// profile), <c>monado-settings.json</c>, <c>environment.txt</c> (what Mirador set at the last Start, one
    /// <c>NAME=value</c> a line), <c>versions.txt</c>, then the module's.
    /// </summary>
    /// <param name="lastEnvironment">The session's <c>LastMiradorEnvironment</c>; null before the first Start.</param>
    /// <param name="versions">Mirador's, Windows' and the profile's Monado's versions, one a line.</param>
    /// <param name="openVr">The OpenVR paths file and OpenComposite's log, added as <c>openvrpaths.vrpath</c> and
    /// <c>opencomposite.log</c> (Mirador.md §4.6 item 23); null leaves them out.</param>
    public static DiagnosticsZip Collect(
        AppPaths paths, Profile profile, IReadOnlyDictionary<string, string>? lastEnvironment, string versions, IHeadsetModule? module, ModuleContext ctx,
        OpenVrFiles? openVr = null)
    {
        var zip = new DiagnosticsZip();
        if (NewestMonadoLog(paths.LogsDir) is { } monadoLog)
        {
            zip.AddFile("monado.log", monadoLog);
        }
        else
        {
            zip.AddMissing("monado.log", $"no Monado log yet in {paths.LogsDir}");
        }

        zip.AddFile("mirador.log", paths.AppLogFile);
        if (NewestVerifyLog(paths.LogsDir) is { } verifyLog)
        {
            zip.AddFile("verify.log", verifyLog);
        }

        zip.AddText("profile.json", JsonFile.Text(profile, DiagnosticsJsonContext.Default.Profile));
        zip.AddFile("monado-settings.json", paths.MonadoSettingsFile);
        zip.AddText(
            "environment.txt",
            lastEnvironment is null
                ? "Monado hasn't been started since Mirador started.\n"
                : string.Concat(lastEnvironment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}\n")));
        zip.AddText("versions.txt", versions);
        if (openVr is not null)
        {
            zip.AddFile("openvrpaths.vrpath", openVr.PathsFile);
            zip.AddFile("opencomposite.log", openVr.LogFile);
        }

        module?.AddDiagnostics(ctx, profile, zip);
        return zip;
    }

    /// <summary>Writes the zip to <paramref name="zipPath"/>, replacing a file there; nothing is left behind when it fails.</summary>
    /// <exception cref="IOException">The zip couldn't be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The zip couldn't be written there.</exception>
    public void Save(string zipPath)
    {
        var missing = new StringBuilder();
        foreach (var line in refused)
        {
            missing.Append(line).Append('\n');
        }

        var temp = zipPath + ".tmp";
        try
        {
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

                    // Opening the source is the part that may fail for the source's reasons; writing failures are the zip's.
                    FileStream source;
                    try
                    {
                        source = new FileStream(file!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                    {
                        missing.Append(entry).Append(": ").Append(file).Append(" isn't there\n");
                        continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        missing.Append(entry).Append(": ").Append(file).Append(" couldn't be read (").Append(e.Message.Trim()).Append(")\n");
                        continue;
                    }

                    using (source)
                    using (var target = archive.CreateEntry(entry).Open())
                    {
                        source.CopyTo(target);
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
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private void Add(string entryName, string? file, string? text)
    {
        var name = entryName.Replace('\\', '/');
        var plain = name.Length > 0
            && !Path.IsPathRooted(name)
            && !name.Contains(':')
            && name.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        if (!plain || name == "missing.txt" || entries.Any(e => string.Equals(e.Entry, name, StringComparison.OrdinalIgnoreCase)))
        {
            refused.Add($"{entryName}: left out (not a plain relative name, or taken already)");
            return;
        }

        entries.Add((name, file, text));
    }

    /// <summary>The newest <c>monado-yyyyMMdd-HHmmss.log</c> (their names sort by time); null when there is none.</summary>
    public static string? NewestMonadoLog(string logsDir) =>
        Directory.Exists(logsDir)
            ? Directory.EnumerateFiles(logsDir, "monado-*.log").OrderBy(Path.GetFileName, StringComparer.Ordinal).LastOrDefault()
            : null;

    /// <summary>The prefix of a "Verify build for Windows" run's own log, <c>verify-run-yyyyMMdd-HHmmss.log</c>.</summary>
    public const string VerifyLogPrefix = "verify-run-";

    /// <summary>The newest Verify run's log (not the Monado logs a run leaves, <c>verify-monado-*.log</c>); null when there is none.</summary>
    public static string? NewestVerifyLog(string logsDir) =>
        Directory.Exists(logsDir)
            ? Directory.EnumerateFiles(logsDir, VerifyLogPrefix + "*.log").OrderBy(Path.GetFileName, StringComparer.Ordinal).LastOrDefault()
            : null;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(Profile))]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
