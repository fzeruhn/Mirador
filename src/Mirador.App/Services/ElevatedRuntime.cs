namespace Mirador.App.Services;

/// <summary>
/// What Mirador's elevated steps share. The runtime itself is set only by the helper service, never by an elevated
/// <c>Mirador.exe</c>: the old <c>--set-runtime</c> step, which took any manifest, is gone (Mirador.md §4.11 item 51).
/// </summary>
public static class ElevatedRuntime
{
    /// <summary>The exit code Windows' "the user cancelled" stands for.</summary>
    public const int PromptCancelled = 1223;
}
