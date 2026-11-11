using System.Runtime.InteropServices;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class OpenCompositeCopyTests
{
    private const string Dll = "vrclient_x64.dll";

    private sealed class Dirs : IDisposable
    {
        private readonly TempDir dir = new();

        public string Runtime => dir.File("user", "Mirador", "OpenComposite");

        public string CopyBin(string name) => Path.Combine(Runtime, "bin", name);

        public OpenCompositeCopy Copy() => new(Runtime);

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
        d.Release("1.0.0", [1, 2, 3]);
        Assert.False(d.Copy().HasCopy);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.True(d.Copy().HasCopy);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal([7], File.ReadAllBytes(d.CopyBin("version.txt")));
        Assert.False(File.Exists(d.CopyBin(Dll + ".new")));
    }

    [Fact]
    public void Current_when_same()
    {
        using var d = new Dirs();
        d.Release("1.0.0", [1, 2, 3]);
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
        d.Release("1.0.0", [1, 2, 3]);
        d.Copy().Update();
        d.Release("1.0.0", [4, 5, 6, 7]);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.Equal([4, 5, 6, 7], File.ReadAllBytes(d.CopyBin(Dll)));
    }

    [Fact]
    public void Keeps_the_owner_s_files()
    {
        using var d = new Dirs();
        d.Release("1.0.0", [1]);
        d.Copy().Update();
        foreach (var name in new[] { "applist.json", "apps-config.json", "opencomposite.ini" })
        {
            File.WriteAllText(d.CopyBin(name), name);
        }

        d.Release("1.0.0", [2]);
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
        d.Release("1.0.0", [1, 2, 3]);
        d.Copy().Update();
        d.Release("1.0.0", [9, 9]);

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
        d.Release("1.0.0", File.ReadAllBytes(system));
        d.Copy().Update();
        d.Release("1.0.0", [9, 9]);

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
    public void Nothing_downloaded_is_none_yet()
    {
        using var d = new Dirs();

        Assert.False(d.Copy().HasSomethingToCopy);
        Assert.Equal(CopyOutcome.NoneYet, d.Copy().Update());
        Assert.False(Directory.Exists(Path.Combine(d.Runtime, "bin")));
    }

    [Fact]
    public void The_newest_download_is_copied()
    {
        using var d = new Dirs();
        d.Release("2026.11.06", [6]);
        d.Release("2026.11.20", [20]);

        Assert.True(d.Copy().HasSomethingToCopy);
        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.Equal([20], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.True(d.Copy().HoldsDownload);
        Assert.Equal("OpenComposite: 2026.11.20 (7534249)", d.Copy().VersionLine());
    }

    [Fact]
    public void Installing_keeps_the_release_in_use_and_one_before_it()
    {
        using var d = new Dirs();
        d.Release("2026.11.01", [1]);
        d.Release("2026.11.06", [6]);
        var newest = d.Release("2026.11.20", [20]);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Install(newest));

        Assert.False(Directory.Exists(Path.Combine(d.Runtime, "download", "2026.11.01")));
        Assert.True(Directory.Exists(d.DownloadDir("2026.11.06")));
        Assert.True(Directory.Exists(d.DownloadDir("2026.11.20")));
    }

    [Fact]
    public void Roll_back_goes_to_the_release_before_and_drops_the_newer_download()
    {
        using var d = new Dirs();
        d.Release("2026.11.06", [6]);
        d.Copy().Install(d.Release("2026.11.20", [20]));

        Assert.Equal(CopyOutcome.Updated, d.Copy().RollBack());

        Assert.Equal([6], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.11.06", d.Copy().CopySource!.Version);
        Assert.False(Directory.Exists(d.DownloadDir("2026.11.20")));

        // The launch doesn't bring the newer one back.
        Assert.Equal(CopyOutcome.Current, d.Copy().Update());
        Assert.Equal([6], File.ReadAllBytes(d.CopyBin(Dll)));

        // Nothing earlier now.
        Assert.Equal(CopyOutcome.NothingEarlier, d.Copy().RollBack());
    }

    [Fact]
    public void The_version_line_names_the_copys_release()
    {
        using var d = new Dirs();
        Assert.Equal("OpenComposite: none yet", d.Copy().VersionLine());

        d.Copy().Install(d.Release("2026.11.06", [1]));

        Assert.Equal("OpenComposite: 2026.11.06 (7534249)", d.Copy().VersionLine());
    }

    [Fact]
    public void Launch_keeps_a_newer_download()
    {
        using var d = new Dirs();
        d.Release("2026.01.01", [1]);
        var release = d.Release("2026.11.06", [2, 2]);
        Assert.Equal(CopyOutcome.Updated, d.Copy().Install(release));

        Assert.Equal(CopyOutcome.Current, d.Copy().Update());

        Assert.Equal([2, 2], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.11.06", d.Copy().CopySource?.Version);
        Assert.True(Directory.Exists(d.DownloadDir("2026.11.06")));
    }

    [Fact]
    public void Update_takes_the_newest_unpacked_download()
    {
        using var d = new Dirs();
        d.Release("2026.01.01", [1]);
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
        d.Release("2026.01.01", [1, 2, 3]);
        d.Copy().Update();

        // A crash mid-unpack: SOURCE.txt (extracted first) is there, the DLL (last) is truncated.
        var part = Path.Combine(d.Runtime, "download", "2026.11.06.part", "OpenComposite");
        Directory.CreateDirectory(Path.Combine(part, "bin"));
        File.WriteAllBytes(Path.Combine(part, "bin", Dll), [9, 9]);
        File.WriteAllText(Path.Combine(part, "SOURCE.txt"), Dirs.Line("2026.11.06"));

        Assert.Equal(CopyOutcome.Current, d.Copy().Update());

        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
        Assert.Equal("2026.01.01", d.Copy().CopySource?.Version);
    }

    [Fact]
    public void Same_version_other_bytes_is_repaired()
    {
        using var d = new Dirs();
        d.Release("2026.01.01", [1, 2, 3]);
        d.Copy().Update();
        File.WriteAllBytes(d.CopyBin(Dll), [9, 9]);

        Assert.Equal(CopyOutcome.Updated, d.Copy().Update());

        Assert.Equal([1, 2, 3], File.ReadAllBytes(d.CopyBin(Dll)));
    }

    [Fact]
    public void No_record_counts_as_older()
    {
        using var d = new Dirs();
        d.Release("2026.01.01", [1, 2, 3]);
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
        d.Release("2026.01.01", [1]);
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
    public void Held_dll_install_finishes_at_next_update()
    {
        using var d = new Dirs();
        d.Release("2026.01.01", [1]);
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
