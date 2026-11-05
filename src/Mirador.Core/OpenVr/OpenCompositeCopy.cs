using System.Security.Cryptography;

namespace Mirador.Core.OpenVr;

/// <summary>What <see cref="OpenCompositeCopy.Update"/> did.</summary>
public enum CopyOutcome
{
    /// <summary>The copy matched the bundle already; nothing was written.</summary>
    Current,

    /// <summary>The copy was made or brought up to date.</summary>
    Updated,

    /// <summary>A game holds the copy's DLL: the old copy stays, and the next try may succeed.</summary>
    InUse,

    /// <summary>This Mirador has no bundled OpenComposite (a dev run).</summary>
    NoBundle,
}

/// <summary>
/// Mirador's per-user copy of the bundled OpenComposite (Mirador.md §4.6 item 20). OpenComposite writes
/// <c>applist.json</c> next to its DLL at every game start and ends the game if it can't, so it can't run from the
/// admin-only install folder. Only <c>bin\vrclient_x64.dll</c> and <c>bin\version.txt</c> are ever written; the files
/// OpenComposite and the owner put there (<c>applist.json</c>, <c>apps-config.json</c>, <c>opencomposite.ini</c>) stay.
/// </summary>
/// <param name="bundleDir">The bundled OpenComposite: <c>{app}\opencomposite</c>, with <c>bin\</c> and <c>SOURCE.txt</c>.</param>
/// <param name="runtimeDir">The copy: the OpenVR runtime folder, with <c>bin\</c> below it.</param>
public sealed class OpenCompositeCopy(string bundleDir, string runtimeDir)
{
    private const string Dll = "vrclient_x64.dll";

    private static readonly string[] Files = ["version.txt", Dll];

    /// <summary>OpenComposite's own log: <c>%LOCALAPPDATA%\OpenComposite\logs\opencomposite.log</c>.</summary>
    public static string LogFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenComposite", "logs", "opencomposite.log");

    /// <summary>The copy's folder, the one the OpenVR paths file names.</summary>
    public string RuntimeDir => runtimeDir;

    /// <summary>Whether this Mirador has OpenComposite in its bundle.</summary>
    public bool HasBundle => File.Exists(Path.Combine(bundleDir, "bin", Dll));

    /// <summary>Whether the copy's DLL is there.</summary>
    public bool HasCopy => File.Exists(Path.Combine(runtimeDir, "bin", Dll));

    /// <summary>
    /// Brings the copy up to date with the bundle: each file is written when it is missing or its content differs,
    /// through a <c>.new</c> file moved into place. IO problems other than a held DLL are thrown.
    /// </summary>
    public CopyOutcome Update()
    {
        if (!HasBundle)
        {
            return CopyOutcome.NoBundle;
        }

        var target = Directory.CreateDirectory(Path.Combine(runtimeDir, "bin")).FullName;
        var updated = false;
        foreach (var name in Files)
        {
            var source = Path.Combine(bundleDir, "bin", name);
            var dest = Path.Combine(target, name);
            if (File.Exists(dest) && SameContent(source, dest))
            {
                continue;
            }

            var staged = dest + ".new";
            File.Copy(source, staged, overwrite: true);
            try
            {
                File.Move(staged, dest, overwrite: true);
            }
            catch (Exception e) when (File.Exists(dest) && (e is IOException or UnauthorizedAccessException))
            {
                // A game has the DLL open or loaded (a loaded one is "access denied"): keep the old one for now.
                File.Delete(staged);
                return CopyOutcome.InUse;
            }

            updated = true;
        }

        return updated ? CopyOutcome.Updated : CopyOutcome.Current;
    }

    /// <summary>The OpenComposite commit the bundle was built from: the first line of its <c>SOURCE.txt</c>.</summary>
    public string? Commit()
    {
        var source = Path.Combine(bundleDir, "SOURCE.txt");
        if (!File.Exists(source))
        {
            return null;
        }

        var first = File.ReadLines(source).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    /// <summary>The line for <c>versions.txt</c>: <c>OpenComposite: &lt;commit&gt;</c>.</summary>
    public string VersionLine() => $"OpenComposite: {Commit() ?? "not in this build"}";

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
        {
            return false;
        }

        using var first = File.OpenRead(a);
        using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }
}
