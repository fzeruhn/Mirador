using Microsoft.Extensions.Logging;

namespace Mirador.Helper.Tests;

/// <summary>Keeps every formatted log message so a test can check what was (and was not) written.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception);
        if (exception is not null)
        {
            text += " " + exception;
        }

        lock (entries)
        {
            entries.Add((logLevel, text));
        }
    }
}
