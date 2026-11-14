using System.Text;
using System.Text.Json.Nodes;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class OpenVrPathsTests
{
    private const string SteamVr = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR";

    /// <summary>Finn's file on 2026-11-04, as SteamVR wrote it (Mirador.md §4.6).</summary>
    private const string FinnsFile = """
        {
        	"config" :
        	[
        		"C:\\Program Files (x86)\\Steam\\config"
        	],
        	"external_drivers" :
        	[
        		"C:\\Users\\finn\\sboys3\\CustomHeadset-1.2.0-Pimax-Windows\\PimaxNative",
        		"C:\\Program Files\\Pimax\\SteamVRSupport\\drivers\\aapvr"
        	],
        	"jsonid" : "vrpathreg",
        	"log" :
        	[
        		"C:\\Program Files (x86)\\Steam\\logs"
        	],
        	"runtime" :
        	[
        		"C:\\Program Files (x86)\\Steam\\steamapps\\common\\SteamVR"
        	],
        	"version" : 1
        }
        """;

    private static string Ours(TempDir dir) => dir.File("Mirador", "OpenComposite");

    private static OpenVrPaths With(TempDir dir, string? content, string? ours = null)
    {
        var file = dir.File("openvr", "openvrpaths.vrpath");
        if (content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }

        return new OpenVrPaths(file, ours ?? Ours(dir));
    }

    private static JsonObject Json(TempDir dir) =>
        JsonNode.Parse(File.ReadAllText(dir.File("openvr", "openvrpaths.vrpath")))!.AsObject();

    private static string[] Runtimes(TempDir dir) =>
        Json(dir)["runtime"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static string File3(params string[] runtimes) =>
        new JsonObject
        {
            ["jsonid"] = "vrpathreg",
            ["runtime"] = new JsonArray(runtimes.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
            ["version"] = 1,
        }.ToJsonString();

    [Fact]
    public void Finn_s_file_reads_as_steamvr()
    {
        using var dir = new TempDir();

        var state = With(dir, FinnsFile).Read();

        Assert.Equal(new OpenVrState(OpenVrTarget.SteamVr, SteamVr, null), state);
    }

    [Fact]
    public void Use_monado_puts_ours_first_and_keeps_the_rest()
    {
        using var dir = new TempDir();
        var paths = With(dir, FinnsFile);
        var before = JsonNode.Parse(FinnsFile)!.AsObject();

        Assert.Null(paths.UseMonado());

        Assert.Equal([Ours(dir), SteamVr], Runtimes(dir));
        var after = Json(dir);
        foreach (var key in new[] { "config", "log", "external_drivers", "jsonid", "version" })
        {
            Assert.True(JsonNode.DeepEquals(before[key], after[key]), key);
        }

        Assert.Equal(6, after.Count);
        Assert.Equal(OpenVrTarget.Monado, paths.Read().Target);
    }

    [Fact]
    public void Use_monado_twice_keeps_one_entry()
    {
        using var dir = new TempDir();
        var paths = With(dir, File3(SteamVr));

        Assert.Null(paths.UseMonado());
        Assert.Null(paths.UseMonado());

        Assert.Equal([Ours(dir), SteamVr], Runtimes(dir));
    }

    [Fact]
    public void Missing_file_is_created_with_three_keys()
    {
        using var dir = new TempDir();
        var paths = With(dir, null);
        Assert.Equal(new OpenVrState(OpenVrTarget.None, null, null), paths.Read());

        Assert.Null(paths.UseMonado());

        var json = Json(dir);
        Assert.Equal(["jsonid", "runtime", "version"], json.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("vrpathreg", json["jsonid"]!.GetValue<string>());
        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.Equal([Ours(dir)], Runtimes(dir));
        Assert.False(File.Exists(dir.File("openvr", "openvrpaths.vrpath.tmp")));
    }

    [Fact]
    public void Unknown_keys_survive()
    {
        using var dir = new TempDir();
        var paths = With(dir, """{ "runtime": ["C:\\SteamVR"], "future": { "a": [1, 2] }, "version": 1 }""");

        Assert.Null(paths.UseMonado());

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "a": [1, 2] }"""), Json(dir)["future"]));
        Assert.Equal([Ours(dir), @"C:\SteamVR"], Runtimes(dir));
    }

    [Theory]
    [InlineData("{ \"runtime\": ")]
    [InlineData("{ \"runtime\": \"x\" }")]
    [InlineData("[]")]
    [InlineData("{ \"runtime\": [1] }")]
    [InlineData("{ \"runtime\": [\"C:\\\\a\"], \"runtime\": [\"C:\\\\b\"] }")]
    public void Unparsable_file_is_never_overwritten(string content)
    {
        using var dir = new TempDir();
        var paths = With(dir, content);
        var bytes = File.ReadAllBytes(dir.File("openvr", "openvrpaths.vrpath"));

        var state = paths.Read();
        var monado = paths.UseMonado();

        Assert.Equal(OpenVrTarget.None, state.Target);
        Assert.NotNull(state.Problem);
        Assert.Contains("openvrpaths.vrpath", state.Problem);
        Assert.Equal(state.Problem, monado);
        Assert.Equal(bytes, File.ReadAllBytes(dir.File("openvr", "openvrpaths.vrpath")));
    }

    [Fact]
    public void Bom_file_reads()
    {
        using var dir = new TempDir();
        var paths = With(dir, null);
        var file = dir.File("openvr", "openvrpaths.vrpath");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, FinnsFile, new UTF8Encoding(true));

        Assert.Equal(OpenVrTarget.SteamVr, paths.Read().Target);
        Assert.Null(paths.UseMonado());
        Assert.Equal([Ours(dir), SteamVr], Runtimes(dir));
    }

    [Fact]
    public void Non_ascii_folder_round_trips()
    {
        using var dir = new TempDir();
        const string zoe = @"C:\Users\Zoë Ann\AppData\Local\Mirador\OpenComposite";
        var paths = With(dir, FinnsFile, zoe);

        Assert.Null(paths.UseMonado());

        Assert.Equal([zoe, SteamVr], Runtimes(dir));
        Assert.Equal(OpenVrTarget.Monado, paths.Read().Target);
        var sameFolder = new OpenVrPaths(dir.File("openvr", "openvrpaths.vrpath"), "c:/users/zoë ann/appdata/local/mirador/opencomposite/");
        Assert.Equal(OpenVrTarget.Monado, sameFolder.Read().Target);
    }

    [Fact]
    public void Other_runtime_is_named()
    {
        using var dir = new TempDir();

        var state = With(dir, File3(@"D:\OpenComposite", SteamVr)).Read();

        Assert.Equal(new OpenVrState(OpenVrTarget.Other, @"D:\OpenComposite", null), state);
    }

    [Fact]
    public void Empty_runtime_list_is_none()
    {
        using var dir = new TempDir();

        Assert.Equal(new OpenVrState(OpenVrTarget.None, null, null), With(dir, File3()).Read());
    }

    [Theory]
    [InlineData(@"C:\Mirador\OpenComposite", @"c:/mirador/opencomposite/", true)]
    [InlineData(@"C:\Mirador\OpenComposite\", @"C:\Mirador\OpenComposite", true)]
    [InlineData(@"C:\Mirador\OpenComposite", @"C:\Mirador\OpenComposite2", false)]
    public void Same_path(string a, string b, bool same)
    {
        Assert.Equal(same, OpenVrPaths.SamePath(a, b));
    }
}
