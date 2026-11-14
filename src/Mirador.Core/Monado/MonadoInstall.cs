namespace Mirador.Core.Monado;

/// <summary>The files of one Monado build that Mirador works with. All paths are full paths.</summary>
/// <param name="Manifest">The OpenXR runtime manifest that tells OpenXR apps which DLL is the runtime.</param>
/// <param name="RuntimeDll">The runtime DLL the manifest points to.</param>
/// <param name="ServiceExe">The real <c>monado-service.exe</c>.</param>
/// <param name="StandInDir">The folder where the stand-in for <c>monado-service.exe</c> goes (it need not exist yet).</param>
/// <param name="IsBundled">
/// True for a Monado Mirador installed in its admin-only folder (<c>{app}\monado</c>: Mirador's Monado, or the build an
/// older Mirador came with), whose stand-in the installer or the helper placed; false for one in a folder the user can
/// write (a build folder, a branch built here).
/// </param>
/// <param name="Approved">
/// For a branch or build folder: the approved copy of this very build, which is what Monado runs from (as administrator
/// when the profile asks, else as the user) and what "Set Monado" makes the OpenXR runtime; null when the build isn't
/// approved as it is now.
/// </param>
public sealed record MonadoInstall(string Manifest, string RuntimeDll, string ServiceExe, string StandInDir, bool IsBundled, Mirador.Ipc.ApprovedBuild? Approved = null)
{
    /// <summary>The manifests that make this build the OpenXR runtime: its own, and its approved copy's.</summary>
    public IEnumerable<string> RuntimeManifests => Approved is { } a ? [Manifest, a.Manifest] : [Manifest];

    /// <summary>The folders a game's stand-in of this build runs from: its own, and its approved copy's.</summary>
    public IEnumerable<string> StandInDirs => Approved is { } a ? [StandInDir, a.StandInDir] : [StandInDir];
}
