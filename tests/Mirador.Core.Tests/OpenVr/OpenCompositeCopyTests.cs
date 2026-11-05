using System.Runtime.InteropServices;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class OpenCompositeCopyTests
{
    private const string Dll = "vrclient_x64.dll";

    private sealed class Dirs : IDisposable
    {
        private readonly TempDir dir = new();

        public string Bundle => dir.File("app", "opencomposite");

        public string Runtime => dir.File("user", "Mirador", "OpenComposite");

        public string BundleBin(string name) => Path.Combine(Bundle, "bin", name);

        public string CopyBin(string name) => Path.Combine(Runtime, "bin", name);

        public OpenCompositeCopy Copy() => new(Bundle, Runtime);

        public void Bundled(byte[] dll)
        {
            Directory.CreateDirectory(Path.Combine(Bundle, "bin"));
            File.WriteAllBytes(BundleBin(Dll), dll);
            File.WriteAllBytes(BundleBin("version.txt"), []);
        }

        public void Dispose() => dir.Dispose();
    }

    [Fact]
    public void Copies_when_missing()
    {
        using var d = new Dirs();
        d.Bundled([1, 2, 3]);
        Assert.False(d.Copy().HasCopy);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.True(d.Copy().HasCopy);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal(0, new FileInfo(d.CopyBin("version.txt")).Length);
        Assert.False(File.Exists(d.CopyBin(Dll + ".new")));
    }

    [Fact]
    public void Current_when_same()
    {
        using var d = new Dirs();
        d.Bundled([1, 2, 3]);
        d.Copy().Update();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(d.CopyBin(Dll), old);
        File.SetLastWriteTimeUtc(d.CopyBin("version.txt"), old);

        Assert.Equal(CopyOutcome.Current, d.Copy().Update());

        Assert.Equal(old, File.GetLastWriteTimeUtc(d.CopyBin(Dll)));
        Assert.Equal(old, File.GetLastWriteTimeUtc(d.CopyBin("version.txt")));
    }

    [Fact]
    public void Updates_when_bundle_differs()
    {
        using var d = new Dirs();
        d.Bundled([1, 2, 3]);
        d.Copy().Update();
        d.Bundled([4, 5, 6, 7]);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.Equal([4, 5, 6, 7], File.ReadAllBytes(d.CopyBin(Dll)));
    }

    [Fact]
    public void Keeps_the_owner_s_files()
    {
        using var d = new Dirs();
        d.Bundled([1]);
        d.Copy().Update();
        foreach (var name in new[] { "applist.json", "apps-config.json", "opencomposite.ini" })
        {
            File.WriteAllText(d.CopyBin(name), name);
        }

        d.Bundled([2]);
        d.Copy().Update();

        foreach (var name in new[] { "applist.json", "apps-config.json", "opencomposite.ini" })
        {
            Assert.Equal(name, File.ReadAllText(d.CopyBin(name)));
        }
    }

    [Fact]
    public void Held_dll_is_in_use_and_left()
    {
        using var d = new Dirs();
        d.Bundled([1, 2, 3]);
        d.Copy().Update();
        d.Bundled([9, 9]);

        using (new FileStream(d.CopyBin(Dll), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(CopyOutcome.InUse, d.Copy().Update());
        }

        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.False(File.Exists(d.CopyBin(Dll + ".new")));
        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());
        Assert.Equal([9, 9], File.ReadAllBytes(d.CopyBin(Dll)));
    }

    [Fact]
    public void Loaded_dll_is_in_use_and_left()
    {
        // A game has the DLL loaded, not just open: Windows then refuses to replace the file with "access denied".
        using var d = new Dirs();
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
        d.Bundled(File.ReadAllBytes(system));
        d.Copy().Update();
        d.Bundled([9, 9]);

        var handle = NativeLibrary.Load(d.CopyBin(Dll));
        try
        {
            Assert.Equal(CopyOutcome.InUse, d.Copy().Update());
        }
        finally
        {
            NativeLibrary.Free(handle);
        }

        Assert.Equal(File.ReadAllBytes(system), File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());
    }

    [Fact]
    public void No_bundle()
    {
        using var d = new Dirs();

        Assert.False(d.Copy().HasBundle);
        Assert.Equal(CopyOutcome.NoBundle, d.Copy().Update());
        Assert.False(Directory.Exists(d.Runtime));
    }

    [Fact]
    public void Commit_reads_first_line()
    {
        using var d = new Dirs();
        Directory.CreateDirectory(d.Bundle);
        Assert.Null(d.Copy().Commit());
        Assert.Equal("OpenComposite: not in this build", d.Copy().VersionLine());

        File.WriteAllText(Path.Combine(d.Bundle, "SOURCE.txt"), "cff07db75c4823afe93ed7027b03d5f7bc86f164\nOpenComposite from …\n");

        Assert.Equal("cff07db75c4823afe93ed7027b03d5f7bc86f164", d.Copy().Commit());
        Assert.Equal("OpenComposite: cff07db75c4823afe93ed7027b03d5f7bc86f164", d.Copy().VersionLine());
    }

    [Fact]
    public void Log_file_is_under_local_app_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "OpenComposite", "logs", "opencomposite.log"), OpenCompositeCopy.LogFile);
    }
}
