using System.Text.Json.Nodes;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class RestoreOpenVrCommandTests
{
    private const string SteamVr = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR";

    private sealed class World : IDisposable
    {
        private readonly TempDir dir = new();

        public World(params string[] runtimes)
        {
            Directory.CreateDirectory(Path.Combine(RuntimeDir, "bin"));
            File.WriteAllBytes(Path.Combine(RuntimeDir, "bin", "vrclient_x64.dll"), [1]);
            File.WriteAllText(Path.Combine(RuntimeDir, "bin", "opencomposite.ini"), "haptics=off");
            Directory.CreateDirectory(Path.GetDirectoryName(PathsFile)!);
            var list = runtimes.Select(r => r == "ours" ? RuntimeDir : r).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray();
            File.WriteAllText(PathsFile, new JsonObject { ["runtime"] = new JsonArray(list), ["version"] = 1 }.ToJsonString());
        }

        public string RuntimeDir => dir.File("Mirador", "OpenComposite");

        public string PathsFile => dir.File("openvr", "openvrpaths.vrpath");

        public OpenVrPaths Paths => new(PathsFile, RuntimeDir);

        public int Run() => RestoreOpenVrCommand.Run(Paths, RuntimeDir);

        public void Dispose()
        {
            if (File.Exists(PathsFile))
            {
                File.SetAttributes(PathsFile, FileAttributes.Normal);
            }

            dir.Dispose();
        }
    }

    [Fact]
    public void Restores_and_deletes_when_ours()
    {
        using var w = new World("ours", SteamVr);

        Assert.Equal(0, w.Run());

        Assert.Equal(new OpenVrState(OpenVrTarget.SteamVr, SteamVr, null), w.Paths.Read());
        Assert.False(Directory.Exists(w.RuntimeDir));
    }

    [Fact]
    public void Ours_further_down_the_list_is_removed_too()
    {
        using var w = new World(@"D:\Other", "ours");

        Assert.Equal(0, w.Run());

        Assert.False(w.Paths.NamesOurs());
        Assert.Equal(new OpenVrState(OpenVrTarget.Other, @"D:\Other", null), w.Paths.Read());
        Assert.False(Directory.Exists(w.RuntimeDir));
    }

    [Fact]
    public void Deletes_copy_when_not_ours()
    {
        using var w = new World(@"D:\Other", SteamVr);
        var before = File.ReadAllBytes(w.PathsFile);

        Assert.Equal(0, w.Run());

        Assert.Equal(before, File.ReadAllBytes(w.PathsFile));
        Assert.False(Directory.Exists(w.RuntimeDir));
    }

    [Fact]
    public void Keeps_copy_when_write_fails()
    {
        using var w = new World("ours", SteamVr);
        File.SetAttributes(w.PathsFile, FileAttributes.ReadOnly);

        Assert.Equal(1, w.Run());

        Assert.True(w.Paths.NamesOurs());
        Assert.True(File.Exists(Path.Combine(w.RuntimeDir, "bin", "vrclient_x64.dll")));
    }

    [Fact]
    public void Keeps_copy_when_file_unreadable()
    {
        using var w = new World();
        File.WriteAllText(w.PathsFile, "{ nope");

        Assert.Equal(1, w.Run());

        Assert.True(Directory.Exists(w.RuntimeDir));
    }

    [Fact]
    public void Missing_file_and_copy_are_fine()
    {
        using var w = new World();
        File.Delete(w.PathsFile);
        Directory.Delete(w.RuntimeDir, recursive: true);

        Assert.Equal(0, w.Run());

        Assert.False(File.Exists(w.PathsFile));
    }
}
