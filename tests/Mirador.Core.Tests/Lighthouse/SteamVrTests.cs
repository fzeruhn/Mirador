using Mirador.Ipc;

namespace Mirador.Core.Tests.Lighthouse;

public class SteamVrTests
{
    /// <summary>The way Steam writes <c>SteamPath</c> to the registry: lower case, forward slashes.</summary>
    private const string SteamPath = "c:/program files (x86)/steam";

    private const string VdfFile = @"c:\program files (x86)\steam\steamapps\libraryfolders.vdf";
    private const string SteamVrInSecondLibrary = @"D:\SteamLibrary\steamapps\common\SteamVR";

    // A real libraryfolders.vdf: tabs between key and value, backslashes doubled, other keys that have to be ignored.
    private const string TwoLibraries = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"1234567890123456789"
        		"totalsize"		"0"
        		"update_clean_bytes_tally"		"1342558977"
        		"time_last_update_verified"		"1760000000"
        		"apps"
        		{
        			"228980"		"0"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"apps"
        		{
        			"250820"		"5326438291"
        		}
        	}
        }
        """;

    private static string Library(string index, string path, params string[] apps) =>
        $"\t\"{index}\"\n\t{{\n\t\t\"path\"\t\t\"{path.Replace(@"\", @"\\")}\"\n\t\t\"apps\"\n\t\t{{\n"
        + string.Concat(apps.Select(app => $"\t\t\t\"{app}\"\t\t\"1\"\n"))
        + "\t\t}\n\t}\n";

    private static string Vdf(params string[] libraries) =>
        "\"libraryfolders\"\n{\n" + string.Concat(libraries) + "}\n";

    private static string? Find(string? vdf, string[] dirsThatExist, string? steamVrEnv = null, string? steamPath = SteamPath) =>
        SteamVr.FindInstall(
            steamPath,
            steamVrEnv,
            path => path == VdfFile ? vdf : null,
            dir => dirsThatExist.Contains(dir, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Finds_steamvr_in_the_library_that_lists_its_app_id()
    {
        var found = Find(TwoLibraries, [SteamVrInSecondLibrary]);

        Assert.Equal(SteamVrInSecondLibrary, found);
    }

    [Fact]
    public void Reads_libraryfolders_vdf_under_steams_own_folder()
    {
        string? asked = null;

        SteamVr.FindInstall(SteamPath, null, path => { asked = path; return null; }, _ => true);

        Assert.Equal(VdfFile, asked);
    }

    [Fact]
    public void Steamvr_path_wins_and_is_used_as_it_is()
    {
        var read = false;

        var found = SteamVr.FindInstall(
            SteamPath, @"E:\Games\SteamVR", path => { read = true; return TwoLibraries; }, _ => false);

        Assert.Equal(@"E:\Games\SteamVR", found);
        Assert.False(read);
    }

    [Fact]
    public void Steamvr_path_works_without_a_steam_install()
    {
        var found = SteamVr.FindInstall(null, "E:/Games/SteamVR", _ => null, _ => false);

        Assert.Equal(@"E:\Games\SteamVR", found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_steamvr_path_counts_as_not_set(string? env)
    {
        var found = Find(TwoLibraries, [SteamVrInSecondLibrary], steamVrEnv: env);

        Assert.Equal(SteamVrInSecondLibrary, found);
    }

    [Fact]
    public void A_missing_libraryfolders_file_gives_null()
    {
        Assert.Null(Find(null, [SteamVrInSecondLibrary]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_steam_path_or_steamvr_path_there_is_nothing_to_find(string? steamPath)
    {
        var asked = false;

        var found = SteamVr.FindInstall(steamPath, null, _ => { asked = true; return TwoLibraries; }, _ => true);

        Assert.Null(found);
        Assert.False(asked);
    }

    [Fact]
    public void A_steam_path_that_windows_cannot_use_gives_null()
    {
        Assert.Null(SteamVr.FindInstall("c:/steam\0", null, _ => TwoLibraries, _ => true));
    }

    [Fact]
    public void The_first_library_with_steamvr_wins_in_file_order()
    {
        var vdf = Vdf(
            Library("0", @"C:\Steam", "250820"),
            Library("1", @"D:\SteamLibrary", "250820"));

        var found = Find(vdf, [@"C:\Steam\steamapps\common\SteamVR", SteamVrInSecondLibrary]);

        Assert.Equal(@"C:\Steam\steamapps\common\SteamVR", found);
    }

    [Fact]
    public void A_library_whose_steamvr_folder_is_gone_is_skipped()
    {
        var vdf = Vdf(
            Library("0", @"C:\Steam", "250820"),
            Library("1", @"D:\SteamLibrary", "250820"));

        var found = Find(vdf, [SteamVrInSecondLibrary]);

        Assert.Equal(SteamVrInSecondLibrary, found);
    }

    [Fact]
    public void A_library_without_the_app_id_is_skipped_even_if_a_steamvr_folder_is_there()
    {
        var vdf = Vdf(
            Library("0", @"C:\Steam", "228980"),
            Library("1", @"D:\SteamLibrary", "250820"));

        var found = Find(vdf, [@"C:\Steam\steamapps\common\SteamVR", SteamVrInSecondLibrary]);

        Assert.Equal(SteamVrInSecondLibrary, found);
    }

    [Fact]
    public void Steamvr_listed_but_not_installed_anywhere_gives_null()
    {
        Assert.Null(Find(TwoLibraries, []));
    }

    [Fact]
    public void No_library_lists_steamvr_gives_null()
    {
        var vdf = Vdf(Library("0", @"C:\Steam", "228980"));

        Assert.Null(Find(vdf, [@"C:\Steam\steamapps\common\SteamVR"]));
    }

    [Fact]
    public void Key_order_inside_a_library_does_not_matter()
    {
        const string appsFirst = """
            "libraryfolders"
            {
                "0"
                {
                    "apps" { "250820" "1" }
                    "path" "D:\\SteamLibrary"
                }
            }
            """;

        Assert.Equal(SteamVrInSecondLibrary, Find(appsFirst, [SteamVrInSecondLibrary]));
    }

    [Fact]
    public void A_library_without_a_path_is_skipped()
    {
        const string noPath = """
            "libraryfolders"
            {
                "0" { "apps" { "250820" "1" } }
                "1" { "path" "D:\\SteamLibrary" "apps" { "250820" "1" } }
            }
            """;

        Assert.Equal(SteamVrInSecondLibrary, Find(noPath, [SteamVrInSecondLibrary]));
    }

    [Fact]
    public void Windows_line_endings_are_fine()
    {
        var crlf = TwoLibraries.Replace("\r\n", "\n").Replace("\n", "\r\n"); // whatever the checkout made of the source's line endings

        Assert.Equal(SteamVrInSecondLibrary, Find(crlf, [SteamVrInSecondLibrary]));
    }

    [Fact]
    public void The_answer_is_a_full_path_with_backslashes_whatever_the_file_says()
    {
        var vdf = Vdf(Library("0", @"D:\SteamLibrary\", "250820")).Replace(@"D:\\SteamLibrary\\", "D:/SteamLibrary/");

        var found = Find(vdf, [SteamVrInSecondLibrary]);

        Assert.Equal(SteamVrInSecondLibrary, found);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a vdf file")]
    [InlineData("\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"")] // cut off
    [InlineData("\"libraryfolders\" { \"0\" { \"path\" \"D:\\\\SteamLibrary\" } } }")] // one brace too many
    [InlineData("\"libraryfolders\" \"just a string\"")]
    [InlineData("\"libraryfolders\" { \"0\" \"also a string\" }")]
    [InlineData("\"libraryfolders\" { \"0\" { \"apps\" \"a string\" \"path\" \"D:\\\\SteamLibrary\" } }")]
    public void A_file_that_cannot_be_used_gives_null_and_does_not_throw(string vdf)
    {
        Assert.Null(Find(vdf, [SteamVrInSecondLibrary]));
    }

    [Fact]
    public void Driver_lighthouse_dll_is_in_the_drivers_folder_of_steamvr()
    {
        var dll = SteamVr.DriverLighthouse(SteamVrInSecondLibrary);

        Assert.Equal(@"D:\SteamLibrary\steamapps\common\SteamVR\drivers\lighthouse\bin\win64\driver_lighthouse.dll", dll);
    }
}
