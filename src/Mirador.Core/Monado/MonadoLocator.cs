using Mirador.Core.Profiles;

namespace Mirador.Core.Monado;

/// <summary>Finds the files of a Monado build: the one shipped with Mirador, or one in a build folder.</summary>
/// <param name="bundledRoot">The folder that holds Mirador's own <c>runtime</c> and <c>server</c> folders.</param>
public sealed class MonadoLocator(string bundledRoot)
{
    private static readonly string[] ManifestNames = ["openxr_monado-dev.json", "openxr_monado.json"];

    /// <summary>Finds the build <paramref name="source"/> names and checks that every file it returns exists (the stand-in folder excepted).</summary>
    /// <exception cref="MiradorException">The build is not there or is incomplete; the message says what to do.</exception>
    public MonadoInstall Locate(BuildSource source) =>
        source.Kind == SourceKind.Bundled ? LocateBundled() : LocateFolder(source.Folder);

    private MonadoInstall LocateBundled()
    {
        var root = Path.GetFullPath(bundledRoot);
        var runtimeDir = Path.Combine(root, "runtime");
        var manifest = Path.Combine(runtimeDir, "openxr_monado.json");
        if (!File.Exists(manifest))
        {
            throw new MiradorException("This Mirador has no bundled Monado. Pick a Monado build folder in the profile.");
        }

        var dll = ReadRuntimeDll(manifest, "Reinstall Mirador, or pick a Monado build folder in the profile.");

        var service = Path.Combine(root, "server", "monado-service.exe");
        if (!File.Exists(service))
        {
            throw new MiradorException(
                $"The Monado that comes with Mirador is incomplete: {service} is missing. Reinstall Mirador, or pick a Monado build folder in the profile.");
        }

        return new MonadoInstall(manifest, dll, service, StandInDirFor(dll), IsBundled: true);
    }

    private static MonadoInstall LocateFolder(string? folderGiven)
    {
        if (string.IsNullOrWhiteSpace(folderGiven))
        {
            throw new MiradorException("No Monado build folder is set. Pick a Monado build folder in the profile.");
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderGiven));
        if (!Directory.Exists(folder))
        {
            throw new MiradorException($"The Monado build folder {folder} doesn't exist. Pick another folder in the profile.");
        }

        var manifest = ManifestNames.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists)
            ?? throw new MiradorException(
                $"The folder {folder} has no {ManifestNames[0]} or {ManifestNames[1]}: is it a Monado build folder? Pick the folder Monado was built in.");

        var dll = ReadRuntimeDll(manifest, "Rebuild Monado, or pick another folder in the profile.");

        var service = Path.Combine(folder, "src", "xrt", "targets", "service", "monado-service.exe");
        if (!File.Exists(service))
        {
            throw new MiradorException(
                $"The Monado build in {folder} has no monado-service.exe (expected at {service}). Build Monado completely, or pick another folder in the profile.");
        }

        return new MonadoInstall(manifest, dll, service, StandInDirFor(dll), IsBundled: false);
    }

    /// <summary>
    /// Where the runtime DLL looks for <c>monado-service.exe</c> when a game finds no service: <c>service\</c> below the
    /// DLL's folder. Monado's <c>ipc_connect_pipe()</c> first tries a path that never exists (the DLL's file name with
    /// <c>\monado-service.exe</c> after it; upstream since 55506067, 2026-02), then this one, and never the DLL's own
    /// folder. Older Monado tried the DLL's own folder first, then this one, so the stand-in works here with both.
    /// </summary>
    private static string StandInDirFor(string runtimeDll) => Path.Combine(Path.GetDirectoryName(runtimeDll)!, "service");

    private static string ReadRuntimeDll(string manifest, string whatToDo)
    {
        var problem = RuntimeManifest.Validate(manifest);
        if (problem is not null)
        {
            throw new MiradorException($"{problem} {whatToDo}");
        }

        return RuntimeManifest.ReadLibraryPath(manifest);
    }
}
