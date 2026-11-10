using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mirador.Core;

/// <summary>Reading and writing the small JSON files Mirador keeps, so that no store ever loses or half-writes one.</summary>
public static class JsonFile
{
    /// <summary>
    /// Reads <paramref name="path"/>. Returns null when the file is missing. A file that is not valid JSON of the
    /// right shape, or that <paramref name="accept"/> turns down (by returning null), is renamed to
    /// <c>&lt;path&gt;.bad</c> (replacing an older one) and null is returned too, so the caller can start from defaults.
    /// </summary>
    public static T? Load<T>(string path, JsonTypeInfo<T> typeInfo, Func<T, T?>? accept = null) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        // Shared with writers and deleters, so that a save can move a new file over this one while it is being read.
        string text;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            text = reader.ReadToEnd();
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(text, typeInfo);
        }
        catch (JsonException)
        {
            value = null;
        }

        if (value is not null && accept is not null)
        {
            value = accept(value);
        }

        if (value is null)
        {
            File.Move(path, path + ".bad", overwrite: true);
        }

        return value;
    }

    /// <summary>
    /// Copies <paramref name="path"/> to <c>&lt;path&gt;.bad</c> (replacing an older one) and keeps the file: for a file
    /// that loaded only in part, so that what was left out isn't lost when the file is next saved.
    /// </summary>
    public static void KeepCopyAsBad(string path) => File.Copy(path, path + ".bad", overwrite: true);

    private const int MoveAttempts = 10;
    private const int MoveRetryMilliseconds = 20;

    /// <summary>
    /// Writes <paramref name="value"/> to <c>&lt;path&gt;.tmp</c> and onto the disk, then moves it over
    /// <paramref name="path"/>: a crash or power cut leaves the old file or the new one, never one half written or empty,
    /// and a reader never finds the file missing. If a reader holds the old file the move is tried up to 10 times, 20 ms
    /// apart; after that the exception is thrown.
    /// </summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Write(stream, value, typeInfo);
            stream.Flush(flushToDisk: true);
        }

        // A POSIX-style replace first: it works while readers hold the old file (they share delete, as Load does), who
        // keep reading the old bytes.
        if (OperatingSystem.IsWindows() && PosixReplace(temp, path))
        {
            return;
        }

        // Elsewhere (a file system without it) the plain move is atomic too, but Windows refuses it for a moment while
        // a reader has the old file open, so it is tried again a few times before the failure is passed on.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryMilliseconds);
            }
        }
    }

    /// <summary>
    /// Renames <paramref name="from"/> over <paramref name="to"/> with <c>FILE_RENAME_FLAG_POSIX_SEMANTICS</c> (Windows 10
    /// 1607 and newer, NTFS): the old file's name goes at once even while it is open. False when Windows refuses it,
    /// for the caller to fall back on.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool PosixReplace(string from, string to)
    {
        const uint delete = 0x00010000, synchronize = 0x00100000;
        const uint shareAll = 0x7, openExisting = 3;
        const int fileRenameInfoEx = 22;
        const uint replaceIfExists = 0x1, posixSemantics = 0x2;

        using var handle = CreateFileW(Path.GetFullPath(from), delete | synchronize, shareAll, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        // FILE_RENAME_INFO on x64: Flags at 0, RootDirectory at 8, FileNameLength at 16, FileName at 20.
        var name = Path.GetFullPath(to);
        var nameBytes = name.Length * sizeof(char);
        var buffer = new byte[Math.Max(24, 20 + nameBytes + sizeof(char))];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, replaceIfExists | posixSemantics);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), nameBytes);
        System.Text.Encoding.Unicode.GetBytes(name, buffer.AsSpan(20));
        return SetFileInformationByHandle(handle, fileRenameInfoEx, buffer, (uint)buffer.Length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int infoClass, byte[] info, uint size);

    /// <summary><paramref name="value"/> as the JSON <see cref="Save"/> writes.</summary>
    public static string Text<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var stream = new MemoryStream();
        Write(stream, value, typeInfo);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes the JSON for people to read too: "Finn's builds" and "Zoë" stay as they are instead of becoming
    /// escaped character codes (only quotes, backslashes and control characters are escaped). The files are Mirador's own, never
    /// put into a web page.
    /// </summary>
    private static void Write<T>(Stream stream, T value, JsonTypeInfo<T> typeInfo)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = typeInfo.Options.WriteIndented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        JsonSerializer.Serialize(writer, value, typeInfo);
    }
}
