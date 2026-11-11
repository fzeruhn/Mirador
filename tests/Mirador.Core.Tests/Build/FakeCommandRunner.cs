using Mirador.Core.Build;

namespace Mirador.Core.Tests.Build;

/// <summary>Records the commands and answers each with the lines and exit code <see cref="Answer"/> gives.</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<CommandSpec> Commands { get; } = [];

    /// <summary>The lines and exit code for a command; by default no lines and 0.</summary>
    public Func<CommandSpec, (string[] Lines, int Code)> Answer { get; set; } = _ => ([], 0);

    /// <summary>Runs before the answer: a test can do what the command would (write files) or wait.</summary>
    public Func<CommandSpec, CancellationToken, Task>? Effect { get; set; }

    public async Task<int> RunAsync(CommandSpec spec, Action<string> onLine, CancellationToken ct)
    {
        Commands.Add(spec);
        ct.ThrowIfCancellationRequested();
        if (Effect is not null)
        {
            await Effect(spec, ct);
        }

        var (lines, code) = Answer(spec);
        foreach (var line in lines)
        {
            onLine(line);
        }

        return code;
    }
}
