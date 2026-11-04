namespace Mirador.Core.Monado;

/// <summary>What to start: the program, its command line, where it runs and the environment it gets.</summary>
/// <param name="FileName">The program to run: a full path, or a name that Windows finds on its own search path.</param>
/// <param name="Arguments">The whole command line after the program name, as one string.</param>
/// <param name="WorkingDirectory">An existing folder to run in.</param>
/// <param name="Environment">The complete environment of the new process: nothing is inherited from Mirador.</param>
public sealed record ProcessSpec(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
