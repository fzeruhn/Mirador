namespace Mirador.Helper.Tests;

/// <summary>
/// A test that needs a real Monado install (Monado's layout: <c>openxr_monado.json</c> and <c>bin\</c>). It is skipped,
/// not failed, unless the environment variable <c>MIRADOR_TEST_MONADO_INSTALL</c> names one.
/// </summary>
public sealed class MonadoInstallFactAttribute : FactAttribute
{
    public const string Variable = "MIRADOR_TEST_MONADO_INSTALL";

    public MonadoInstallFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } folder
            || !File.Exists(Path.Combine(folder, "openxr_monado.json")))
        {
            Skip = $"Needs a Monado install: set {Variable} to one (a branch build's install folder, for example).";
        }
    }
}
