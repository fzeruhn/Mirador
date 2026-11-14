using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.Core.Monado;

/// <summary>
/// Finds the files of a profile's Monado: the bundled Monado, a branch Mirador built, or a build or install folder.
/// </summary>
/// <param name="bundledRoot">
/// <c>{app}\monado</c>: the bundled Monado's versions (<see cref="MiradorMonado"/>).
/// </param>
/// <param name="buildsRoot">Where branch profiles are built (<see cref="AppPaths.BuildsDir"/>), one folder per profile.</param>
/// <param name="approvedCopy">
/// For a branch or build folder: the approved copy of the build as it is now, or null. Called with the profile and the
/// build found, also on the UI thread (Home, the Profiles page's checks), so it should hash a build only when its files
/// changed. Null: approved copies aren't looked up.
/// </param>
public sealed class MonadoLocator(string bundledRoot, string? buildsRoot = null, Func<Profile, MonadoInstall, ApprovedBuild?>? approvedCopy = null)
{
    private static readonly string[] ManifestNames = ["openxr_monado-dev.json", "openxr_monado.json"];

    /// <summary>The root of Mirador's Monado's versions.</summary>
    public string MiradorRoot => Path.GetFullPath(bundledRoot);

    /// <summary>Where branch profiles are built, when this locator knows.</summary>
    public string? BuildsRoot => buildsRoot;

    /// <summary>
    /// Finds <paramref name="profile"/>'s Monado and checks that every file it returns exists (the stand-in folder
    /// excepted); with its approved copy (<see cref="MonadoInstall.Approved"/>) when this build is the one approved, with
    /// "Run as administrator" on or off.
    /// </summary>
    /// <exception cref="MiradorException">The build is not there or is incomplete; the message says what to do.</exception>
    public MonadoInstall Locate(Profile profile)
    {
        var install = Locate(profile.Monado, profile.Id);
        return !install.IsBundled && approvedCopy?.Invoke(profile, install) is { } approved
            ? install with { Approved = approved }
            : install;
    }

    /// <summary>
    /// Finds the build <paramref name="source"/> names. A branch's build is <paramref name="profileId"/>'s, so it needs
    /// one; the other kinds don't.
    /// </summary>
    /// <exception cref="MiradorException">The build is not there or is incomplete; the message says what to do.</exception>
    public MonadoInstall Locate(BuildSource source, string? profileId = null) => source.Kind switch
    {
        SourceKind.Mirador => LocateMiradors(),
        SourceKind.Branch => LocateBranch(profileId),
        _ => LocateFolder(source.Folder),
    };

    /// <summary>The install folder of <paramref name="profileId"/>'s branch build (it need not exist yet).</summary>
    public string BranchInstallDir(string profileId) => Path.Combine(BranchDir(profileId), "install");

    /// <summary>The folder of <paramref name="profileId"/>'s branch build: its source, build and install folders.</summary>
    public string BranchDir(string profileId)
    {
        if (buildsRoot is null)
        {
            throw new InvalidOperationException("This locator doesn't know where branches are built.");
        }

        // A profile id is a GUID's digits or one of the default profiles' names: never a path.
        if (profileId.Length == 0 || profileId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        {
            throw new ArgumentException($"Not a profile id: {profileId}", nameof(profileId));
        }

        return Path.Combine(buildsRoot, profileId);
    }

    private MonadoInstall LocateMiradors()
    {
        var root = MiradorRoot;
        var current = MiradorMonado.Read(root).Current
            ?? throw new MiradorException("The bundled Monado isn't installed yet. Install it on the Profiles page.");
        var folder = MiradorMonado.FolderOf(root, current);
        return LocateInstall(folder, isMiradors: true)
            ?? throw new MiradorException($"The bundled Monado {current} is incomplete ({folder}). Install it again on the Profiles page.");
    }

    private MonadoInstall LocateBranch(string? profileId)
    {
        if (profileId is null || buildsRoot is null)
        {
            throw new MiradorException("This profile's branch isn't built yet. Build it on the Profiles page.");
        }

        var install = BranchInstallDir(profileId);
        return LocateInstall(install, isMiradors: false)
            ?? throw new MiradorException("This profile's branch isn't built yet. Build it on the Profiles page.");
    }

    /// <summary>
    /// Monado's Windows install layout (<c>cmake --install</c>): the manifest at the top, its DLL and the service in
    /// <c>bin</c>; the stand-in goes in <c>bin\service</c>, where the runtime DLL looks. Null when it isn't one.
    /// </summary>
    private static MonadoInstall? LocateInstall(string folder, bool isMiradors)
    {
        var manifest = Path.Combine(folder, "openxr_monado.json");
        var service = Path.Combine(folder, "bin", "monado-service.exe");
        if (!File.Exists(manifest) || !File.Exists(service))
        {
            return null;
        }

        var dll = ReadRuntimeDll(manifest, isMiradors ? "Install the bundled Monado again on the Profiles page." : "Build it again, or pick another folder in the profile.");
        return new MonadoInstall(manifest, dll, service, StandInDirFor(dll), IsBundled: isMiradors);
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

        if (LocateInstall(folder, isMiradors: false) is { } installed)
        {
            return installed;
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
    /// Where the runtime DLL looks for the service it was built to start when a game finds no service: <c>service\</c>
    /// below the DLL's folder. A runtime that looks in its own folder first also finds the stand-in here, as long as it
    /// was built to start the stand-in's own name (<see cref="StandIn.FileName"/>).
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
