using Mirador.Core.Native;

namespace Mirador.Core.Monado;

/// <summary>Finds the named pipe that <c>monado-service</c> opens when it is ready for applications.</summary>
public interface IMonadoPipe
{
    /// <summary>The pipe name a service started with <paramref name="env"/> opens.</summary>
    string NameFor(IReadOnlyDictionary<string, string> env);

    /// <summary>Whether a pipe of this name exists, whether or not it is busy.</summary>
    bool Exists(string name);
}

/// <summary>
/// Monado builds its pipe name from its own environment: <c>\\.\pipe\</c>, the runtime folder, <c>/monado_comp_ipc</c>.
/// The environment Mirador hands the service therefore decides the name.
/// </summary>
public sealed class MonadoPipe : IMonadoPipe
{
    private const string Prefix = @"\\.\pipe\";
    private const string Suffix = "/monado_comp_ipc";

    /// <summary>
    /// The runtime folder is <c>XDG_RUNTIME_DIR</c>, else <c>XDG_CACHE_HOME</c>, else the temp path. Monado joins it
    /// to the file name with a slash whether or not the folder already ends in a separator, and the temp path always
    /// ends in a backslash, so the usual name has <c>\/</c> in it.
    /// </summary>
    public string NameFor(IReadOnlyDictionary<string, string> env) =>
        Prefix + (Get(env, "XDG_RUNTIME_DIR") ?? Get(env, "XDG_CACHE_HOME") ?? TempPathFrom(env)) + Suffix;

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

    public bool Exists(string name) => Win32.PipeExists(name);

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
