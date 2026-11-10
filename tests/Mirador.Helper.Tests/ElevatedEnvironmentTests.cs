using Mirador.Helper.ElevatedMonado;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class ElevatedEnvironmentTests
{
    private static readonly UserFolders Folders = new(
        UserProfile: @"C:\Users\finn",
        AppData: @"C:\Users\finn\AppData\Roaming",
        LocalAppData: @"C:\Users\finn\AppData\Local",
        Temp: @"C:\Users\finn\AppData\Local\Temp");

    private const string ServerDir = @"C:\Program Files\Mirador\monado\server";
    private const string SteamVrPath = @"C:\Program Files (x86)\Steam\steamapps\common\SteamVR";

    private static ElevatedStart Start(
        int lh = 3000, int scale = 100, int comp = 6, bool exit = false, string module = "") =>
        new(lh, scale, comp, exit, module);

    private static IReadOnlyDictionary<string, string> Build(ElevatedStart? start = null, string module = "", params string[] testLines) =>
        ElevatedEnvironment.Build(start ?? Start(module: module), Folders, ServerDir, SteamVrPath, testLines);

    [Fact]
    public void Headless_adds_the_null_compositor_and_the_simulated_headset_and_nothing_else()
    {
        var plain = Build();
        var headless = Build(Start() with { Headless = true });

        Assert.False(plain.ContainsKey("XRT_COMPOSITOR_NULL"));
        Assert.False(plain.ContainsKey("SIMULATED_ENABLE"));
        Assert.Equal("1", headless["XRT_COMPOSITOR_NULL"]);
        Assert.Equal("1", headless["SIMULATED_ENABLE"]);
        Assert.Equal(plain.Count + 2, headless.Count);
    }

    [Fact]
    public void Headless_with_a_headset_module_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Build(Start(module: DreamAirEnvironment.ModuleId) with { Headless = true }));
    }

    [Fact]
    public void No_user_path()
    {
        var env = Build();

        // PATH is System32, Windows, System32\Wbem and the server folder only: never a folder the user can write to.
        var system32 = Environment.SystemDirectory;
        var expected = $"{system32};{Path.GetDirectoryName(system32)};{Path.Combine(system32, "wbem")};{ServerDir}";
        Assert.Equal(expected, env["PATH"]);
        Assert.DoesNotContain(Folders.UserProfile, env["PATH"]);
    }

    [Fact]
    public void Users_temp_for_the_pipe_name()
    {
        var env = Build();

        Assert.Equal(Folders.Temp, env["TEMP"]);
        Assert.Equal(Folders.Temp, env["TMP"]);

        // Monado names its pipe from this temp, so the user's games find it.
        Assert.Equal(
            MonadoPipeName.Prefix + Folders.Temp + @"\" + MonadoPipeName.Suffix,
            MonadoPipeName.NameFor(env, AnsiCodePage.RoundTrip));
    }

    [Fact]
    public void Temp_and_pipe_name_come_from_the_users_block()
    {
        // The user's environment block, as CreateEnvironmentBlock gives it (names in Windows' own case).
        var block = new Dictionary<string, string>
        {
            ["USERPROFILE"] = @"C:\Users\finn",
            ["APPDATA"] = @"C:\Users\finn\AppData\Roaming",
            ["LOCALAPPDATA"] = @"C:\Users\finn\AppData\Local",
            ["TEMP"] = @"C:\Users\finn\AppData\Local\Temp",
            ["TMP"] = @"C:\Users\finn\AppData\Local\Temp",
            ["SystemRoot"] = @"C:\WINDOWS",
        };

        var folders = UserFolders.FromBlock(block);
        var env = ElevatedEnvironment.Build(Start(), folders, ServerDir, SteamVrPath, []);

        Assert.Equal(block["TEMP"], env["TEMP"]);
        Assert.Equal(block["TMP"], env["TMP"]);
        Assert.Equal(block["USERPROFILE"], env["USERPROFILE"]);
        Assert.Equal(block["APPDATA"], env["APPDATA"]);
        Assert.Equal(block["LOCALAPPDATA"], env["LOCALAPPDATA"]);

        // The name Mirador waits for (built from the same block) is the one the elevated Monado opens; never the
        // helper's own C:\Windows temp.
        var name = MonadoPipeName.NameFor(env, AnsiCodePage.RoundTrip);
        Assert.Equal(MonadoPipeName.NameFor(block, AnsiCodePage.RoundTrip), name);
        Assert.Equal(@"\\.\pipe\C:\Users\finn\AppData\Local\Temp\/monado_comp_ipc", name);
        Assert.DoesNotContain(@"C:\Windows", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Temp_follows_the_blocks_order()
    {
        // TMP wins over TEMP, as GetTempPath2 and MonadoPipeName read them; without either, the profile folder.
        var both = UserFolders.FromBlock(new Dictionary<string, string>
        {
            ["USERPROFILE"] = @"C:\Users\u",
            ["temp"] = @"D:\TempB",
            ["tmp"] = @"D:\TempA",
        });
        Assert.Equal(@"D:\TempA", both.Temp);

        var temp = new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\u", ["TEMP"] = @"D:\TempB", ["TMP"] = "" };
        Assert.Equal(@"D:\TempB", UserFolders.FromBlock(temp).Temp);
        Assert.Equal(
            MonadoPipeName.NameFor(temp, AnsiCodePage.RoundTrip),
            MonadoPipeName.NameFor(ElevatedEnvironment.Build(Start(), UserFolders.FromBlock(temp), ServerDir, SteamVrPath, []), AnsiCodePage.RoundTrip));

        var none = UserFolders.FromBlock(new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\u" });
        Assert.Equal(@"C:\Users\u", none.Temp);
        Assert.Equal(@"C:\Users\u\AppData\Local", none.LocalAppData);
    }

    [Theory]
    [InlineData(500, 100, 6)]     // lighthouse below range
    [InlineData(1500, 100, 6)]    // lighthouse off its step
    [InlineData(3000, 999, 6)]    // scale above range
    [InlineData(3000, 102, 6)]    // scale off its step
    [InlineData(3000, 100, 0)]    // compositor below range
    [InlineData(3000, 100, 21)]   // compositor above range
    public void Settings_out_of_range_refused(int lh, int scale, int comp) =>
        Assert.Throws<ArgumentException>(() => Build(Start(lh, scale, comp)));

    [Fact]
    public void Unknown_module_refused() =>
        Assert.Throws<ArgumentException>(() => Build(Start(module: "vive")));

    [Fact]
    public void The_dream_airs_elevated_table_is_exactly_its_eight_variables()
    {
        var expected = new Dictionary<string, string>
        {
            ["STEAMVR_LH_ENABLE"] = "1",
            ["DREAMAIR_VK_LAYER"] = "1",
            ["DISABLE_RTSS_LAYER"] = "1",
            ["VK_LOADER_LAYERS_ALLOW"] = "VK_LAYER_DREAMAIR_nvapi_direct",
            ["XRT_COMPOSITOR_FORCE_VK_DISPLAY"] = "0",
            ["XRT_COMPOSITOR_USE_PRESENT_WAIT"] = "1",
            ["U_PACING_COMP_PRESENT_TO_DISPLAY_OFFSET_MS"] = "8.9",
            ["DREAMAIR_LOG"] = "info",
        };

        Assert.Equal(expected, DreamAirEnvironment.FixedEnvironment.ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(8, DreamAirEnvironment.FixedEnvironment.Count);
        Assert.Equal(
            ["DREAMAIR_VK_LAYER", "DISABLE_RTSS_LAYER", "VK_LOADER_LAYERS_ALLOW", "XRT_COMPOSITOR_FORCE_VK_DISPLAY", "XRT_COMPOSITOR_USE_PRESENT_WAIT"],
            DirectModeEnvironment.Variables.Select(p => p.Key));
    }

    [Fact]
    public void Dreamair_adds_its_fixed_table()
    {
        var env = Build(module: "dreamair");

        foreach (var (name, value) in DreamAirEnvironment.FixedEnvironment)
        {
            Assert.Equal(value, env[name]);
        }
    }

    [Fact]
    public void No_vk_or_xr_variables_but_layers_allow()
    {
        var env = Build(module: "dreamair");

        var vk = env.Keys.Where(k => k.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(["VK_LOADER_LAYERS_ALLOW"], vk);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("XR_", StringComparison.OrdinalIgnoreCase));

        // A profile with no module carries no VK_* or XR_* at all.
        var plain = Build();
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("VK_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("XR_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Relaxed_connection_security_set() =>
        Assert.Equal("1", Build()["IPC_RELAXED_CONNECTION_SECURITY"]);

    [Fact]
    public void Steamvr_path_is_set()
    {
        var env = Build();

        Assert.Equal(SteamVrPath, env["STEAMVR_PATH"]);
    }

    [Fact]
    public void Test_lines_only_when_given()
    {
        Assert.False(Build().ContainsKey("XRT_COMPOSITOR_NULL"));

        var env = Build(Start(), "", "XRT_COMPOSITOR_NULL=1", "SIMULATED_ENABLE=1");
        Assert.Equal("1", env["XRT_COMPOSITOR_NULL"]);
        Assert.Equal("1", env["SIMULATED_ENABLE"]);
    }

    [Fact]
    public void Settings_are_written()
    {
        var env = Build(Start(lh: 5000, scale: 150, comp: 11, exit: true));

        Assert.Equal("5000", env["LH_DISCOVER_WAIT_MS"]);
        Assert.Equal("150", env["XRT_COMPOSITOR_SCALE_PERCENTAGE"]);
        Assert.Equal("11", env["U_PACING_COMP_MIN_TIME_MS"]);
        Assert.Equal("1", env["IPC_EXIT_ON_DISCONNECT"]);
    }

    [Fact]
    public void Users_folders_are_set_but_system_folders_are_the_machines()
    {
        var env = Build();

        Assert.Equal(Folders.UserProfile, env["USERPROFILE"]);
        Assert.Equal(Folders.AppData, env["APPDATA"]);
        Assert.Equal(Folders.LocalAppData, env["LOCALAPPDATA"]);
        Assert.False(string.IsNullOrEmpty(env["SystemRoot"]));
        Assert.False(string.IsNullOrEmpty(env["ProgramData"]));
    }
}
