using System.Text;
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
/// <remarks>
/// Monado is C and works through Windows' narrow functions: it reads the folder with <c>getenv</c> or
/// <c>GetTempPath2A</c> and creates the pipe with <c>CreateNamedPipeA</c>, so the folder goes through the ANSI code
/// page and back. A folder with characters that code page lacks (<c>C:\Users\Łukasz</c> on a Western Windows) gives a
/// pipe whose name has their best fit or <c>?</c> in their place; games, whose runtime DLL does the same, find it there,
/// and so must Mirador.
/// </remarks>
public sealed class MonadoPipe : IMonadoPipe
{
    private const string Prefix = @"\\.\pipe\";
    private const string Suffix = "/monado_comp_ipc";

    private readonly Func<string, string> throughAnsi;

    public MonadoPipe() : this(Win32.ThroughAnsiCodePage)
    {
    }

    /// <summary>For tests: <paramref name="throughAnsi"/> stands in for the trip through the ANSI code page and back.</summary>
    internal MonadoPipe(Func<string, string> throughAnsi) => this.throughAnsi = throughAnsi;

    /// <summary>
    /// The runtime folder is <c>XDG_RUNTIME_DIR</c>, else <c>XDG_CACHE_HOME</c>, else the temp path. Monado joins it
    /// to the file name with a slash whether or not the folder already ends in a separator, and the temp path always
    /// ends in a backslash, so the usual name has <c>\/</c> in it.
    /// </summary>
    public string NameFor(IReadOnlyDictionary<string, string> env)
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
