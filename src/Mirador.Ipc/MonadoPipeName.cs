using System.Text;

namespace Mirador.Ipc;

/// <summary>
/// The one rule for the named pipe <c>monado-service</c> opens, shared by Mirador and the helper so the two can't
/// drift: <c>\\.\pipe\</c>, the runtime folder, <c>/monado_comp_ipc</c>. Monado builds the name from its own
/// environment, so the environment a service is started with decides the name.
/// </summary>
/// <remarks>
/// The runtime folder is <c>XDG_RUNTIME_DIR</c>, else <c>XDG_CACHE_HOME</c>, else the temp path (what
/// <c>GetTempPath2</c> returns). Monado joins it to the file name with a slash whether or not the folder already ends
/// in a separator, and the temp path always ends in a backslash, so the usual name has <c>\/</c> in it. Monado works
/// through the narrow functions, so a folder outside ASCII goes through the ANSI code page (<see cref="AnsiCodePage"/>).
/// </remarks>
public static class MonadoPipeName
{
    public const string Prefix = @"\\.\pipe\";
    public const string Suffix = "/monado_comp_ipc";

    /// <summary>The pipe name a service started with <paramref name="env"/> opens; <paramref name="throughAnsi"/> is the trip through the code page.</summary>
    public static string NameFor(IReadOnlyDictionary<string, string> env, Func<string, string> throughAnsi)
    {
        var folder = Get(env, "XDG_RUNTIME_DIR") ?? Get(env, "XDG_CACHE_HOME") ?? TempPathFrom(env);

        // Every ANSI code page Windows uses keeps ASCII as it is.
        return Prefix + (Ascii.IsValid(folder) ? folder : throughAnsi(folder)) + Suffix;
    }

    /// <summary>
    /// What <c>GetTempPath2</c> returns for <paramref name="env"/>: the first non-empty of <c>TMP</c>, <c>TEMP</c>,
    /// <c>USERPROFILE</c>, else the Windows folder, with a backslash at the end.
    /// </summary>
    public static string TempPathFrom(IReadOnlyDictionary<string, string> env)
    {
        var path = Get(env, "TMP") ?? Get(env, "TEMP") ?? Get(env, "USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.EndsWith('\\') ? path : path + '\\';
    }

    /// <summary>A variable's value, matching the name the way Windows does (ignoring case); empty counts as not set.</summary>
    private static string? Get(IReadOnlyDictionary<string, string> env, string name)
    {
        foreach (var (key, value) in env)
        {
            if (value.Length > 0 && string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }
}
