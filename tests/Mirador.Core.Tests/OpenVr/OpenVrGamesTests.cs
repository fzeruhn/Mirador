using System.Text.Json.Nodes;
using Mirador.Core.OpenVr;

namespace Mirador.Core.Tests.OpenVr;

public class OpenVrGamesTests
{
    private const string SteamVr = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR";
    private const string PimaxRuntime = @"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json";

    private sealed class World : IDisposable
    {
        private readonly TempDir dir = new();

        public World(bool installed = true, params string[] runtimes)
        {
            if (installed)
            {
                Directory.CreateDirectory(Path.Combine(InstallDir, "bin"));
                File.WriteAllBytes(Path.Combine(InstallDir, "bin", "vrclient_x64.dll"), [1, 2, 3]);
                File.WriteAllText(Path.Combine(InstallDir, "SOURCE.txt"), "OpenComposite 2026.11.06, commit 75342495640501953fc2c17f73eeb4d124e8528e\n");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PathsFile)!);
            var json = new JsonObject
            {
                ["jsonid"] = "vrpathreg",
                ["runtime"] = new JsonArray((runtimes.Length == 0 ? [SteamVr] : runtimes).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["version"] = 1,
            };
            File.WriteAllText(PathsFile, json.ToJsonString());
            Games = new OpenVrGames(Paths, new OpenCompositeInstall(InstallDir, new Build.FakeCommandRunner()));
        }

        /// <summary>OpenComposite as its installer puts it.</summary>
        public string InstallDir => dir.File("Programs", "OpenComposite");

        public string PathsFile => dir.File("openvr", "openvrpaths.vrpath");

        /// <summary>The same file as another program sees it.</summary>
        public OpenVrPaths Paths => new(PathsFile, InstallDir);

        public OpenVrGames Games { get; }

        /// <summary>OpenComposite set, then SteamVR putting itself first again, as it does when it starts.</summary>
        public void SetThenSteamVrInFront()
        {
            Assert.Null(Games.UseMonado());
            var json = JsonNode.Parse(File.ReadAllText(PathsFile))!;
            json["runtime"] = new JsonArray(JsonValue.Create(SteamVr), JsonValue.Create(InstallDir));
            File.WriteAllText(PathsFile, json.ToJsonString());
        }

        public void Dispose() => dir.Dispose();
    }

    [Fact]
    public void Use_monado_puts_the_installed_opencomposite_first()
    {
        using var w = new World();

        Assert.True(w.Games.Available);
        Assert.Null(w.Games.UseMonado());

        Assert.Equal(OpenVrTarget.Monado, w.Paths.Read().Target);
    }

    [Fact]
    public void Not_installed_no_write()
    {
        using var w = new World(installed: false);
        var before = File.ReadAllText(w.PathsFile);

        Assert.False(w.Games.Available);
        Assert.Equal("OpenComposite isn't installed yet: press \"Get OpenComposite\" first.", w.Games.UseMonado());
        Assert.Equal(before, File.ReadAllText(w.PathsFile));
    }

    [Fact]
    public void A_dll_a_game_holds_still_switches()
    {
        using var w = new World();
        w.SetThenSteamVrInFront();

        using (new FileStream(Path.Combine(w.InstallDir, "bin", "vrclient_x64.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(w.Games.UseMonado());
        }

        Assert.Equal(OpenVrTarget.Monado, w.Paths.Read().Target);
    }

    [Fact]
    public void OpenComposite_in_front_needs_no_button()
    {
        using var w = new World();
        w.Games.UseMonado();

        Assert.Equal(new OpenVrView("OpenComposite", false, null, null), w.Games.View(true, null));
    }

    [Fact]
    public void Unreadable_file_is_a_problem()
    {
        using var w = new World();
        File.WriteAllText(w.PathsFile, "{ nope");

        var view = w.Games.View(true, null);

        Assert.Equal("Unknown", view.Runtime);
        Assert.False(view.OffersOpenComposite);
        Assert.NotNull(view.Problem);
        Assert.Contains("openvrpaths.vrpath", view.Problem);
    }

    [Fact]
    public void SteamVR_in_front_offers_the_button()
    {
        using var w = new World();
        w.SetThenSteamVrInFront();

        Assert.Equal(new OpenVrView("SteamVR", true, null, null), w.Games.View(true, null));
    }

    [Fact]
    public void Another_folder_in_front_by_its_path()
    {
        using var w = new World();
        w.Games.UseMonado();
        File.WriteAllText(w.PathsFile, """{ "runtime": ["D:\\Other"] }""");

        Assert.Equal(@"D:\Other", w.Games.View(true, null).Runtime);
    }

    [Fact]
    public void Nothing_set_on_a_new_install()
    {
        using var w = new World();
        File.WriteAllText(w.PathsFile, """{ "jsonid": "vrpathreg", "runtime": [], "version": 1 }""");

        Assert.Equal(new OpenVrView("None set", true, null, null), w.Games.View(true, null));
    }

    [Fact]
    public void Warning_when_openxr_is_not_monado()
    {
        using var w = new World();
        w.Games.UseMonado();

        Assert.Equal(
            "OpenVR games will go to PiOpenXR_64.json, not Monado: set Monado as OpenXR runtime too.",
            w.Games.View(false, PimaxRuntime).Warning);
        Assert.Equal(
            "No OpenXR runtime is set, so OpenVR games won't start: set Monado as OpenXR runtime too.",
            w.Games.View(false, null).Warning);
        Assert.Null(w.Games.View(true, PimaxRuntime).Warning);
    }

    [Fact]
    public void Uninstalled_while_named_is_a_problem()
    {
        using var w = new World();
        w.Games.UseMonado();
        Directory.Delete(w.InstallDir, recursive: true); // uninstalled through Windows' Settings

        var view = w.Games.View(true, null);

        Assert.Equal(
            "OpenComposite isn't installed any more, so OpenVR games can't start. Press \"Get OpenComposite\" to install it again.",
            view.Problem);
        Assert.False(w.Games.Available);
    }
}
