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

        public World(bool bundle = true, params string[] runtimes)
        {
            if (bundle)
            {
                Directory.CreateDirectory(Path.Combine(BundleDir, "bin"));
                File.WriteAllBytes(Path.Combine(BundleDir, "bin", "vrclient_x64.dll"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(BundleDir, "bin", "version.txt"), []);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(PathsFile)!);
            var json = new JsonObject
            {
                ["jsonid"] = "vrpathreg",
                ["runtime"] = new JsonArray((runtimes.Length == 0 ? [SteamVr] : runtimes).Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["version"] = 1,
            };
            File.WriteAllText(PathsFile, json.ToJsonString());
            Games = new OpenVrGames(Paths, new OpenCompositeCopy(BundleDir, RuntimeDir));
        }

        public string BundleDir => dir.File("app", "opencomposite");

        public string RuntimeDir => dir.File("Mirador", "OpenComposite");

        public string PathsFile => dir.File("openvr", "openvrpaths.vrpath");

        /// <summary>The same file as another program sees it.</summary>
        public OpenVrPaths Paths => new(PathsFile, RuntimeDir);

        public OpenVrGames Games { get; }

        public string CopiedDll => Path.Combine(RuntimeDir, "bin", "vrclient_x64.dll");

        /// <summary>OpenComposite set, then SteamVR putting itself first again, as it does when it starts.</summary>
        public void SetThenSteamVrInFront()
        {
            Assert.Null(Games.UseMonado());
            Assert.Null(Paths.UseSteamVr());
        }

        public void Dispose() => dir.Dispose();
    }

    [Fact]
    public void Use_monado_copies_then_writes()
    {
        using var w = new World();

        Assert.Null(w.Games.UseMonado());

        Assert.True(File.Exists(w.CopiedDll));
        Assert.Equal(OpenVrTarget.Monado, w.Paths.Read().Target);
    }

    [Fact]
    public void No_copy_no_write()
    {
        using var w = new World();
        Directory.CreateDirectory(w.RuntimeDir);
        File.WriteAllText(Path.Combine(w.RuntimeDir, "bin"), "a file where the folder goes");
        var before = File.ReadAllText(w.PathsFile);

        var problem = w.Games.UseMonado();

        Assert.NotNull(problem);
        Assert.StartsWith("Mirador couldn't copy OpenComposite (", problem);
        Assert.EndsWith("); OpenVR games are unchanged.", problem);
        Assert.Equal(before, File.ReadAllText(w.PathsFile));
    }

    [Fact]
    public void Nothing_downloaded_no_write()
    {
        using var w = new World(bundle: false);
        var before = File.ReadAllText(w.PathsFile);

        Assert.False(w.Games.Available);
        Assert.Equal("Mirador has no OpenComposite yet: press \"Get OpenComposite\" first.", w.Games.UseMonado());
        Assert.Equal(before, File.ReadAllText(w.PathsFile));
    }

    [Fact]
    public void Held_copy_still_switches()
    {
        using var w = new World();
        w.SetThenSteamVrInFront();
        File.WriteAllBytes(Path.Combine(w.BundleDir, "bin", "vrclient_x64.dll"), [7]);

        using (new FileStream(w.CopiedDll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(w.Games.UseMonado());
        }

        Assert.Equal(OpenVrTarget.Monado, w.Paths.Read().Target);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(w.CopiedDll));
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
    public void Missing_copy_is_a_problem_while_named()
    {
        using var w = new World();
        w.Games.UseMonado();
        Directory.Delete(w.RuntimeDir, recursive: true); // a virus scanner took it, or the owner reset Mirador's folder

        var view = w.Games.View(true, null);

        Assert.Equal(
            "Mirador's copy of OpenComposite is missing, so OpenVR games can't start. Press \"Set OpenComposite\" to copy it again.",
            view.Problem);
        Assert.True(view.OffersOpenComposite);
        Assert.Null(w.Games.UseMonado());
        Assert.True(File.Exists(w.CopiedDll));
    }

    [Fact]
    public void Update_at_launch_never_switches()
    {
        using var w = new World();
        Assert.Null(w.Games.UpdateAtLaunch());
        Assert.False(File.Exists(w.CopiedDll));
        Assert.Equal(OpenVrTarget.SteamVr, w.Paths.Read().Target);

        w.SetThenSteamVrInFront();
        Assert.Null(w.Games.UpdateAtLaunch());
        Assert.Equal(OpenVrTarget.SteamVr, w.Paths.Read().Target);
    }

    [Fact]
    public void Update_at_launch_restores_a_missing_copy_while_named()
    {
        using var w = new World();
        w.Games.UseMonado();
        Directory.Delete(w.RuntimeDir, recursive: true);

        Assert.Null(w.Games.UpdateAtLaunch());

        Assert.True(File.Exists(w.CopiedDll));
    }

    [Fact]
    public void Update_at_launch_brings_the_copy_up_to_date()
    {
        using var w = new World();
        w.Games.UseMonado();
        File.WriteAllBytes(Path.Combine(w.BundleDir, "bin", "vrclient_x64.dll"), [8, 8]);
        using (new FileStream(w.CopiedDll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal("OpenComposite will be updated when no game uses it.", w.Games.UpdateAtLaunch());
        }

        Assert.Null(w.Games.UpdateAtLaunch());
        Assert.Equal([8, 8], File.ReadAllBytes(w.CopiedDll));
    }
}
