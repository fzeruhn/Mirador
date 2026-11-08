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

        public void Bundled(byte[] dll, string version)
        {
            Bundled(dll);
            File.WriteAllText(Path.Combine(Bundle, "SOURCE.txt"), Line(version));
            File.WriteAllText(Path.Combine(Bundle, "LICENSE.txt"), "bundle licence");
        }

        public string DownloadDir(string version) => Path.Combine(Runtime, "download", version, "OpenComposite");

        /// <summary>An unpacked release under the download folder, as <see cref="OpenCompositeDownload.FetchAsync"/> leaves it.</summary>
        public string Release(string version, byte[] dll)
        {
            var root = DownloadDir(version);
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllBytes(Path.Combine(root, "bin", Dll), dll);
            File.WriteAllBytes(Path.Combine(root, "bin", "version.txt"), [7]);
            File.WriteAllText(Path.Combine(root, "SOURCE.txt"), Line(version));
            File.WriteAllText(Path.Combine(root, "LICENSE.txt"), "licence " + version);
            return root;
        }

        public static string Line(string version) => $"OpenComposite {version}, commit 75342495640501953fc2c17f73eeb4d124e8528e\n";

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

        // Windows (or a virus scanner looking at the new DLL) may hold the freed image for a moment: "in use" until then.
        Assert.True(SpinWait.SpinUntil(() => d.Copy().Update() == CopyOutcome.Updated, TimeSpan.FromSeconds(5)));
        Assert.Equal([9, 9], File.ReadAllBytes(d.CopyBin(Dll)));
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
        Assert.Equal("OpenComposite: cff07db, bundled", d.Copy().VersionLine());

        File.WriteAllText(Path.Combine(d.Bundle, "SOURCE.txt"), "OpenComposite 2026.11.06, commit 75342495640501953fc2c17f73eeb4d124e8528e\n");
        Assert.Equal("2026.11.06", d.Copy().BundleSource?.Version);
        Assert.Equal("OpenComposite: 2026.11.06 (7534249), bundled", d.Copy().VersionLine());
    }

    [Fact]
    public void Launch_keeps_a_newer_download()
    {
        using var d = new Dirs();
        d.Bundled([1], "2026.01.01");
        var release = d.Release("2026.11.06", [2, 2]);
        Assert.Equal(CopyOutcome.Updated, d.Copy().Install(release));

        Assert.Equal(CopyOutcome.Current, d.Copy().Update());

        Assert.Equal([2, 2], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.11.06", d.Copy().CopySource?.Version);
        Assert.True(Directory.Exists(d.DownloadDir("2026.11.06")));
    }

    [Fact]
    public void A_newer_bundle_beats_an_older_download()
    {
        using var d = new Dirs();
        d.Bundled([1], "2026.01.01");
        d.Release("2026.05.05", [2, 2]);
        d.Copy().Update();
        Assert.Equal([2, 2], File.ReadAllBytes(d.CopyBin(Dll)));

        d.Bundled([3, 3, 3], "2026.12.01");

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());
        Assert.Equal([3, 3, 3], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.12.01", d.Copy().CopySource?.Version);
        Assert.False(d.Copy().HoldsDownload);
    }

    [Fact]
    public void Update_takes_the_newest_unpacked_download()
    {
        using var d = new Dirs();
        d.Bundled([1], "2026.01.01");
        d.Release("2026.03.03", [3]);
        d.Release("2026.10.10", [4]);
        d.Release("2026.02.02", [5]);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.Equal([4], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.True(d.Copy().HoldsDownload);
    }

    [Fact]
    public void A_half_unpacked_part_folder_is_never_a_candidate()
    {
        using var d = new Dirs();
        d.Bundled([1, 2, 3], "2026.01.01");
        d.Copy().Update();

        // A crash mid-unpack: SOURCE.txt (extracted first) is there, the DLL (last) is truncated.
        var part = Path.Combine(d.Runtime, "download", "2026.11.06.part", "OpenComposite");
        Directory.CreateDirectory(Path.Combine(part, "bin"));
        File.WriteAllBytes(Path.Combine(part, "bin", Dll), [9, 9]);
        File.WriteAllText(Path.Combine(part, "SOURCE.txt"), Dirs.Line("2026.11.06"));

        Assert.Equal(CopyOutcome.Current, d.Copy().Update());

        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.01.01", d.Copy().CopySource?.Version);
        Assert.False(d.Copy().HoldsDownload);
    }

    [Fact]
    public void Same_version_other_bytes_is_repaired()
    {
        using var d = new Dirs();
        d.Bundled([1, 2, 3], "2026.01.01");
        d.Copy().Update();
        File.WriteAllBytes(d.CopyBin(Dll), [9, 9]);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
    }

    [Fact]
    public void No_record_counts_as_older()
    {
        using var d = new Dirs();
        d.Bundled([1, 2, 3], "2026.01.01");
        Directory.CreateDirectory(Path.Combine(d.Runtime, "bin"));
        File.WriteAllBytes(d.CopyBin(Dll), [8, 8, 8, 8]);

        Assert.Null(d.Copy().CopySource);
        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.01.01", d.Copy().CopySource?.Version);
    }

    [Fact]
    public void Install_writes_bin_source_and_licences()
    {
        using var d = new Dirs();
        d.Bundled([1], "2026.01.01");
        d.Copy().Update();
        File.WriteAllText(d.CopyBin("applist.json"), "mine");
        var release = d.Release("2026.11.06", [2, 2]);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Install(release));

        Assert.Equal([2, 2], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal([7], File.ReadAllBytes(d.CopyBin("version.txt")));
        Assert.Equal("licence 2026.11.06", File.ReadAllText(Path.Combine(d.Runtime, "LICENSE.txt")));
        Assert.Equal("2026.11.06", d.Copy().CopySource?.Version);
        Assert.True(d.Copy().HoldsDownload);
        Assert.Equal("mine", File.ReadAllText(d.CopyBin("applist.json")));
        Assert.Equal(CopyOutcome.Current, d.Copy().Install(release));
    }

    [Fact]
    public void Use_bundled_puts_the_bundle_back_and_drops_downloads()
    {
        using var d = new Dirs();
        d.Bundled([1], "2026.01.01");
        d.Copy().Update();
        d.Copy().Install(d.Release("2026.11.06", [2, 2]));
        Assert.True(d.Copy().HoldsDownload);

        Assert.Equal(CopyOutcome.Updated, d.Copy().UseBundled());

        Assert.Equal([1], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.01.01", d.Copy().CopySource?.Version);
        Assert.Equal("bundle licence", File.ReadAllText(Path.Combine(d.Runtime, "LICENSE.txt")));
        Assert.False(Directory.Exists(d.Copy().DownloadRoot));
        Assert.False(d.Copy().HoldsDownload);
        Assert.Equal(CopyOutcome.Current, d.Copy().Update());
    }

    [Fact]
    public void Held_dll_install_finishes_at_next_update()
    {
        using var d = new Dirs();
        d.Bundled([1], "2026.01.01");
        d.Copy().Update();
        var release = d.Release("2026.11.06", [2, 2]);

        using (new FileStream(d.CopyBin(Dll), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(CopyOutcome.InUse, d.Copy().Install(release));
        }

        Assert.Equal([1], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.01.01", d.Copy().CopySource?.Version);
        Assert.True(Directory.Exists(release));

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());
        Assert.Equal([2, 2], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.11.06", d.Copy().CopySource?.Version);
    }

    [Fact]
    public void Log_file_is_under_local_app_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "OpenComposite", "logs", "opencomposite.log"), OpenCompositeCopy.LogFile);
    }
}
