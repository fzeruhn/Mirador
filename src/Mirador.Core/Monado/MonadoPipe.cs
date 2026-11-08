using Mirador.Core.Native;
using Mirador.Ipc;

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
/// Monado builds its pipe name from its own environment (the rule is <see cref="MonadoPipeName"/>, shared with the
/// helper); the environment Mirador hands the service therefore decides the name.
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
    private readonly Func<string, string> throughAnsi;

    public MonadoPipe() : this(Win32.ThroughAnsiCodePage)
    {
    }

    /// <summary>For tests: <paramref name="throughAnsi"/> stands in for the trip through the ANSI code page and back.</summary>
    internal MonadoPipe(Func<string, string> throughAnsi) => this.throughAnsi = throughAnsi;

    /// <inheritdoc/>
    public string NameFor(IReadOnlyDictionary<string, string> env) => MonadoPipeName.NameFor(env, throughAnsi);

    /// <summary>What <c>GetTempPath2</c> returns for <paramref name="env"/>, with a backslash at the end.</summary>
    public static string TempPathFrom(IReadOnlyDictionary<string, string> env) => MonadoPipeName.TempPathFrom(env);

    public bool Exists(string name) => Win32.PipeExists(name);
}
