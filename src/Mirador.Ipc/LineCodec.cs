using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mirador.Ipc;

/// <summary>
/// The helper's wire format: one JSON object per line, ended by <c>\n</c>, at most <see cref="MaxLineBytes"/> bytes
/// before the newline. Anything else (a longer line, bytes that are not a message, an input that stops mid-line)
/// throws <see cref="InvalidDataException"/> and the caller drops the connection.
/// </summary>
public static class LineCodec
{
    /// <summary>The longest line, not counting its newline.</summary>
    public const int MaxLineBytes = 4096;

    /// <summary>Writes <paramref name="value"/> as one line and flushes.</summary>
    /// <exception cref="InvalidDataException">The value is longer than <see cref="MaxLineBytes"/> as JSON; nothing is written.</exception>
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());
        if (json.Length > MaxLineBytes)
        {
            throw new InvalidDataException($"A message is longer than {MaxLineBytes} bytes.");
        }

        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        await stream.WriteAsync(line, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one line and parses it. Reads no further than the newline, and never more than
    /// <see cref="MaxLineBytes"/> + 1 bytes, however much the other side sends.
    /// </summary>
    /// <returns>The value, or null when the stream ended before a single byte or the line is the JSON <c>null</c>.</returns>
    /// <exception cref="InvalidDataException">The line is too long, ends the stream early, or is not a valid <typeparamref name="T"/>.</exception>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var typeInfo = TypeInfo<T>();
        var line = new byte[MaxLineBytes];
        var one = new byte[1];
        var count = 0;
        while (true)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                if (count == 0)
                {
                    return default;
                }

                throw new InvalidDataException("A message ended before its newline.");
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            if (count == MaxLineBytes)
            {
                throw new InvalidDataException($"A message is longer than {MaxLineBytes} bytes.");
            }

            line[count++] = one[0];
        }

        try
        {
            return JsonSerializer.Deserialize(new ReadOnlySpan<byte>(line, 0, count), typeInfo);
        }
        catch (JsonException e)
        {
            // Not e.Message: it may quote what the other side sent.
            throw new InvalidDataException("A message is not valid.", e);
        }
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        HelperJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not part of the helper protocol.");
}
