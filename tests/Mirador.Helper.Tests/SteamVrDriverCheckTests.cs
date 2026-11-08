using Mirador.Helper.ElevatedMonado;

namespace Mirador.Helper.Tests;

public class SteamVrDriverCheckTests
{
    /// <summary>The real signer subject of SteamVR's <c>driver_lighthouse.dll</c> (Get-AuthenticodeSignature, 2026-11-07).</summary>
    private const string ValveSubject = "CN=Valve Corp., O=Valve Corp., L=Bellevue, S=Washington, C=US";

    [Fact]
    public void The_real_valve_subject_is_accepted() => Assert.True(SteamVrDriverCheck.IsValve(ValveSubject));

    [Fact]
    public void Cn_need_not_be_the_first_rdn() =>
        Assert.True(SteamVrDriverCheck.IsValve("C=US, S=Washington, L=Bellevue, O=Valve Corp., CN=Valve Corp."));

    [Theory]
    [InlineData("CN=Valve Corp.evil, O=Valve Corp., L=Bellevue, S=Washington, C=US")] // only starts with Valve's CN
    [InlineData("CN=Valve Corp., O=Evil, L=Bellevue, S=Washington, C=US")]           // another organisation
    [InlineData("CN=Valve Corp., L=Bellevue, S=Washington, C=US")]                    // no O at all
    [InlineData("O=Valve Corp., L=Bellevue, C=US")]                                    // no CN at all
    [InlineData("CN=\"Valve Corp., O=Valve Corp.\"")]                                  // one quoted CN holding both
    [InlineData("CN=Valve Corp., CN=Evil, O=Valve Corp.")]                             // two CNs
    [InlineData("CN=Valve Corp. + O=Valve Corp.")]                                     // a multi-valued RDN
    [InlineData("cn=valve corp., o=valve corp.")]                                      // the name, but not Valve's spelling
    [InlineData("garbage ,,= =")]                                                      // not a distinguished name
    [InlineData("")]
    public void Anything_but_valves_exact_name_is_refused(string subject) =>
        Assert.False(SteamVrDriverCheck.IsValve(subject));

    [Fact]
    public void A_signer_that_only_starts_like_valve_fails_the_check()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll");

        var (hold, problem) = SteamVrDriverCheck.Verify(steamVr, _ => "CN=Valve Corp.evil, O=Valve Corp.");
        using (hold)
        {
            Assert.NotNull(problem);
            Assert.Contains("driver_lighthouse.dll", problem);
        }
    }

    /// <summary>Makes a SteamVR folder with Lighthouse driver DLLs; returns the SteamVR root.</summary>
    private static string MakeSteamVr(TempDir dir, params string[] dlls)
    {
        var steamVr = Path.Combine(dir.Path, "SteamVR");
        var win64 = Path.Combine(steamVr, "drivers", "lighthouse", "bin", "win64");
        Directory.CreateDirectory(win64);
        foreach (var dll in dlls)
        {
            File.WriteAllBytes(Path.Combine(win64, dll), [0x4D, 0x5A, 1, 2, 3]);
        }

        return steamVr;
    }

    [Fact]
    public void Unsigned_dll_refused()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "crashhandler.dll");

        var (hold, problem) = SteamVrDriverCheck.Verify(steamVr, _ => null);
        using (hold)
        {
            Assert.NotNull(problem);
            Assert.Contains("crashhandler.dll", problem);
        }
    }

    [Fact]
    public void Other_signer_refused()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll");

        var (hold, problem) = SteamVrDriverCheck.Verify(steamVr, _ => "CN=Someone Else, O=Else Inc.");
        using (hold)
        {
            Assert.NotNull(problem);
            Assert.Contains("driver_lighthouse.dll", problem);
        }
    }

    [Fact]
    public void Held_files_cant_be_replaced()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "libedid.dll");
        var dll = Path.Combine(steamVr, "drivers", "lighthouse", "bin", "win64", "driver_lighthouse.dll");

        var (hold, problem) = SteamVrDriverCheck.Verify(steamVr, _ => ValveSubject);
        Assert.Null(problem);

        // While held, the DLLs can't be written to, deleted or replaced.
        Assert.ThrowsAny<IOException>(() => File.Open(dll, FileMode.Open, FileAccess.Write, FileShare.None));
        Assert.ThrowsAny<IOException>(() => File.Delete(dll));

        hold.Dispose();

        // Freed again once the hold is released.
        File.Delete(dll);
        Assert.False(File.Exists(dll));
    }

    [Fact]
    public void A_missing_win64_folder_is_a_problem()
    {
        using var dir = new TempDir();
        var steamVr = Path.Combine(dir.Path, "SteamVR");
        Directory.CreateDirectory(steamVr);

        var (hold, problem) = SteamVrDriverCheck.Verify(steamVr, _ => ValveSubject);
        using (hold)
        {
            Assert.NotNull(problem);
        }
    }
}
