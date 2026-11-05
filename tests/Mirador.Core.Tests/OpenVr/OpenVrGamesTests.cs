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
            Settings = new AppSettingsStore(dir.File("settings.json"));
            Games = new OpenVrGames(Paths, new OpenCompositeCopy(BundleDir, RuntimeDir), Settings);
        }

        public string BundleDir => dir.File("app", "opencomposite");

        public string RuntimeDir => dir.File("Mirador", "OpenComposite");

        public string PathsFile => dir.File("openvr", "openvrpaths.vrpath");

        /// <summary>The same file as another program sees it.</summary>
        public OpenVrPaths Paths => new(PathsFile, RuntimeDir);

        public AppSettingsStore Settings { get; }

        public OpenVrGames Games { get; }

        public string CopiedDll => Path.Combine(RuntimeDir, "bin", "vrclient_x64.dll");

        public void Dispose() => dir.Dispose();
    }

    [Fact]
    public void Use_monado_copies_then_writes_and_remembers()
    {
        using var w = new World();

        Assert.Null(w.Games.UseMonado());

        Assert.True(File.Exists(w.CopiedDll));
        Assert.Equal(OpenVrTarget.Monado, w.Paths.Read().Target);
        Assert.True(w.Settings.Load().OpenVrOnMonado);
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
        Assert.False(w.Settings.Load().OpenVrOnMonado);
    }

    [Fact]
    public void No_bundle_no_write()
    {
        using var w = new World(bundle: false);
        var before = File.ReadAllText(w.PathsFile);

        Assert.False(w.Games.Available);
        Assert.Equal("OpenComposite isn't in this Mirador build.", w.Games.UseMonado());
        Assert.Equal(before, File.ReadAllText(w.PathsFile));
    }

    [Fact]
    public void Held_copy_still_switches()
    {
        using var w = new World();
        w.Games.UseMonado();
        w.Games.UseSteamVr();
        File.WriteAllBytes(Path.Combine(w.BundleDir, "bin", "vrclient_x64.dll"), [7]);

        using (new FileStream(w.CopiedDll, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(w.Games.UseMonado());
        }

        Assert.Equal(OpenVrTarget.Monado, w.Paths.Read().Target);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(w.CopiedDll));
    }

    [Fact]
    public void Use_steamvr_forgets()
    {
        using var w = new World();
        w.Games.UseMonado();

        Assert.Null(w.Games.UseSteamVr());

        Assert.Equal(OpenVrTarget.SteamVr, w.Paths.Read().Target);
        Assert.False(w.Settings.Load().OpenVrOnMonado);
    }

    [Fact]
    public void View_lines()
    {
        using (var w = new World())
        {
            Assert.Equal(new OpenVrView("OpenVR games: SteamVR", true, null, null), w.Games.View(true, null));
            w.Games.UseMonado();
            Assert.Equal(new OpenVrView("OpenVR games: Monado (OpenComposite)", false, null, null), w.Games.View(true, null));
        }

        using (var w = new World(true, @"D:\OpenComposite"))
        {
            Assert.Equal(new OpenVrView(@"OpenVR games: another runtime (D:\OpenComposite)", true, null, null), w.Games.View(true, null));
        }

        using (var w = new World())
        {
            File.WriteAllText(w.PathsFile, """{ "jsonid": "vrpathreg", "runtime": [], "version": 1 }""");
            Assert.Equal(new OpenVrView("No OpenVR runtime is set", true, null, null), w.Games.View(true, null));
        }
    }

    [Fact]
    public void Unreadable_file_is_a_problem()
    {
        using var w = new World();
        File.WriteAllText(w.PathsFile, "{ nope");

        var view = w.Games.View(true, null);

        Assert.Equal("OpenVR games: unknown, the paths file can't be read", view.Line);
        Assert.NotNull(view.Problem);
        Assert.Contains("openvrpaths.vrpath", view.Problem);
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
    public void Notice_when_switched_back_by_someone_else()
    {
        using var w = new World();
        w.Games.UseMonado();

        Assert.Null(w.Paths.UseSteamVr()); // SteamVR registering itself again, say

        Assert.Equal(
            "Something put SteamVR back in front for OpenVR games (SteamVR does this when it starts or updates). Press \"Use Monado for OpenVR games\" to switch back.",
            w.Games.View(true, null).Warning);
    }

    [Fact]
    public void Notice_names_another_folder()
    {
        using var w = new World();
        w.Games.UseMonado();
        File.WriteAllText(w.PathsFile, """{ "runtime": ["D:\\Other"] }""");

        Assert.Equal(
            "Something put D:\\Other back in front for OpenVR games (SteamVR does this when it starts or updates). Press \"Use Monado for OpenVR games\" to switch back.",
            w.Games.View(true, null).Warning);
    }

    [Fact]
    public void No_notice_after_own_switch_to_steamvr()
    {
        using var w = new World();
        w.Games.UseMonado();
        w.Games.UseSteamVr();

        Assert.Null(w.Games.View(true, null).Warning);
    }

    [Fact]
    public void Missing_copy_is_a_problem_while_named()
    {
        using var w = new World();
        w.Games.UseMonado();
        Directory.Delete(w.RuntimeDir, recursive: true); // a virus scanner took it, or the owner reset Mirador's folder

        var view = w.Games.View(true, null);

        Assert.Equal(
            "Mirador's copy of OpenComposite is missing, so OpenVR games can't start. Press \"Use Monado for OpenVR games\" to copy it again.",
            view.Problem);
        Assert.True(view.OffersMonado);
        Assert.Null(w.Games.UseMonado());
        Assert.True(File.Exists(w.CopiedDll));
        Assert.Null(w.Games.View(true, null).Problem);
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
    public void Update_at_launch()
    {
        using var w = new World();
        Assert.Null(w.Games.UpdateAtLaunch()); // no copy yet: nothing to do
        Assert.False(File.Exists(w.CopiedDll));

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
