using System.Text;

namespace Mirador.Core;

/// <summary>
/// A log file that takes one line at a time. Each line is flushed as it is written, so the file is current when someone
/// opens it, and the last lines are kept in memory for showing in the app. When a line would take the file past
/// <paramref name="maxBytes"/>, the file moves to <c>&lt;path&gt;.1</c> (replacing an older one) and a new file starts.
/// When another program holds either file so that it can't be moved, the lines keep going to the same file and the
/// move is tried again once the file has grown by another tenth of <paramref name="maxBytes"/>. Safe to use from
/// several threads.
/// </summary>
/// <param name="path">The file. Its folder is created when the first line is written.</param>
/// <param name="maxBytes">The size at which the file rolls over.</param>
public sealed class TextLog(string path, long maxBytes = 5_000_000) : IDisposable
{
    private const int TailLines = 200;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object gate = new();
    private readonly Queue<string> tail = new();
    private FileStream? file;
    private long size;
    private long rollAgainAt;
    private bool disposed;

    /// <summary>The last 200 lines written, oldest first: a copy that later writes don't change.</summary>
    public IReadOnlyList<string> Tail
    {
        get
        {
            lock (gate)
            {
                return tail.ToArray();
            }
        }
    }

    /// <summary>Appends <paramref name="line"/> (a line break is added; nothing else is: no time, no prefix).</summary>
    /// <exception cref="IOException">The file could not be written. The line is still in <see cref="Tail"/>.</exception>
    public void Write(string line)
    {
        var bytes = Utf8.GetBytes(line + Environment.NewLine);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            tail.Enqueue(line);
            if (tail.Count > TailLines)
            {
                tail.Dequeue();
            }

            Open();
            if (size > 0 && size + bytes.Length > maxBytes && size >= rollAgainAt)
            {
                Roll();
            }

            file!.Write(bytes);
            file.Flush();
            size += bytes.Length;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            file?.Dispose();
            file = null;
        }
    }

    private void Open()
    {
        if (file is not null)
        {
            return;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        // Others may read the file while it is being written; the app's "open log" and the user's editor must work.
        file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        size = file.Length;
    }

    private void Roll()
    {
        file!.Dispose();
        file = null;
        try
        {
            File.Move(path, path + ".1", overwrite: true);
            rollAgainAt = 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A viewer that follows the log (or holds the .1) without letting it be renamed. Losing every line from now
            // on would be worse than a log over its size: keep writing here, and try again a little later.
            rollAgainAt = size + Math.Max(1, maxBytes / 10);
        }

        Open();
    }
}
