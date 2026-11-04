namespace Mirador.Core.Monado;

/// <summary>The files of one Monado build that Mirador works with. All paths are full paths.</summary>
/// <param name="Manifest">The OpenXR runtime manifest that tells OpenXR apps which DLL is the runtime.</param>
/// <param name="RuntimeDll">The runtime DLL the manifest points to.</param>
/// <param name="ServiceExe">The real <c>monado-service.exe</c>.</param>
/// <param name="StandInDir">The folder where the stand-in for <c>monado-service.exe</c> goes (it need not exist yet).</param>
/// <param name="IsBundled">True for the Monado shipped with Mirador, false for one in a build folder.</param>
public sealed record MonadoInstall(string Manifest, string RuntimeDll, string ServiceExe, string StandInDir, bool IsBundled);
