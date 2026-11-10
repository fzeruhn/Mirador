using System.Security.Cryptography;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public class SteamVrDriverCheckTests
{
    /// <summary>The real signer subject of SteamVR's <c>driver_lighthouse.dll</c> (Get-AuthenticodeSignature, 2026-11-07).</summary>
    private const string ValveSubject = "CN=Valve Corp., O=Valve Corp., L=Bellevue, S=Washington, C=US";

    private static readonly IReadOnlyDictionary<string, string> NoneApproved = new Dictionary<string, string>();

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

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, _ => "CN=Valve Corp.evil, O=Valve Corp.", NoneApproved);
        using (hold)
        {
            Assert.NotNull(refusal);
            Assert.Contains("driver_lighthouse.dll", refusal.Error);
        }
    }

    /// <summary>Makes a SteamVR folder with Lighthouse driver DLLs, each with its own bytes; returns the SteamVR root.</summary>
    private static string MakeSteamVr(TempDir dir, params string[] dlls)
    {
        var steamVr = Path.Combine(dir.Path, "SteamVR");
        var win64 = Win64(steamVr);
        Directory.CreateDirectory(win64);
        foreach (var dll in dlls)
        {
            File.WriteAllBytes(Path.Combine(win64, dll), Bytes(dll));
        }

        return steamVr;
    }

    private static string Win64(string steamVr) => Path.Combine(steamVr, "drivers", "lighthouse", "bin", "win64");

    private static byte[] Bytes(string dll) => [0x4D, 0x5A, .. System.Text.Encoding.UTF8.GetBytes(dll)];

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Valve signs <c>driver_lighthouse.dll</c> and <c>vrcamera_api.dll</c>, nothing else (as on Finn's PC).</summary>
    private static string? ValveSignsTheDriver(string path) =>
        Path.GetFileName(path) is "driver_lighthouse.dll" or "vrcamera_api.dll" ? ValveSubject : null;

    [Fact]
    public void Unsigned_dll_refused()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "crashhandler.dll");

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, _ => null, NoneApproved);
        using (hold)
        {
            Assert.NotNull(refusal);
            Assert.Contains("crashhandler.dll", refusal.Error);
        }
    }

    [Fact]
    public void Other_signer_refused()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll");

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, _ => "CN=Someone Else, O=Else Inc.", NoneApproved);
        using (hold)
        {
            Assert.NotNull(refusal);
            Assert.Contains("driver_lighthouse.dll", refusal.Error);
        }
    }

    [Fact]
    public void Unsigned_dll_with_the_approved_hash_passes()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "ArcturusPerception.dll", "AitH264Capture.dll");
        var approved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArcturusPerception.dll"] = Sha256(Bytes("ArcturusPerception.dll")),
            ["AitH264Capture.dll"] = Sha256(Bytes("AitH264Capture.dll")),
        };

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, approved);
        using (hold)
        {
            Assert.Null(refusal);

            // Held like the signed ones: the approved copy can't be swapped before Monado loads it.
            Assert.ThrowsAny<IOException>(() => File.Delete(Path.Combine(Win64(steamVr), "ArcturusPerception.dll")));
        }
    }

    [Fact]
    public void Unsigned_dll_with_another_hash_refused_as_changed()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "ArcturusPerception.dll", "AitH264Capture.dll");
        var approved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArcturusPerception.dll"] = Sha256(Bytes("an older ArcturusPerception.dll")),
            ["AitH264Capture.dll"] = Sha256(Bytes("AitH264Capture.dll")),
        };

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, approved);
        using (hold)
        {
            Assert.NotNull(refusal);
            Assert.False(refusal.Ok);
            Assert.Equal(HelperRefusal.SteamVrChanged, refusal.Refusal);
            Assert.Contains("ArcturusPerception.dll", refusal.Error);
            Assert.DoesNotContain("AitH264Capture.dll", refusal.Error);
        }

        // A refusal holds nothing.
        File.Delete(Path.Combine(Win64(steamVr), "ArcturusPerception.dll"));
    }

    [Fact]
    public void Unsigned_dll_not_in_the_list_refused_as_changed()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "ArcturusPerception.dll", "AitH264Capture.dll", "AitUVCExtApi.dll");
        var approved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ArcturusPerception.dll"] = Sha256(Bytes("ArcturusPerception.dll")),
        };

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, approved);
        using (hold)
        {
            Assert.NotNull(refusal);
            Assert.Equal(HelperRefusal.SteamVrChanged, refusal.Refusal);

            // Every file that isn't as allowed is named, not just the first.
            Assert.Contains("AitH264Capture.dll", refusal.Error);
            Assert.Contains("AitUVCExtApi.dll", refusal.Error);
            Assert.DoesNotContain("ArcturusPerception.dll", refusal.Error);
        }
    }

    [Fact]
    public void Valve_signed_dll_needs_no_line()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "vrcamera_api.dll");

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, NoneApproved);
        using (hold)
        {
            Assert.Null(refusal);
        }
    }

    [Fact]
    public void A_line_for_another_file_name_approves_nothing()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "ArcturusPerception.dll");
        var approved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The right bytes under another name: a copy renamed into the folder is not what was allowed.
            ["Other.dll"] = Sha256(Bytes("ArcturusPerception.dll")),
        };

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, approved);
        using (hold)
        {
            Assert.Equal(HelperRefusal.SteamVrChanged, refusal?.Refusal);
        }
    }

    [Fact]
    public void A_missing_driver_folder_is_not_a_change()
    {
        using var dir = new TempDir();
        var steamVr = Path.Combine(dir.Path, "SteamVR");
        Directory.CreateDirectory(steamVr);

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, _ => ValveSubject, NoneApproved);
        using (hold)
        {
            Assert.NotNull(refusal);
            Assert.Null(refusal.Refusal); // fails the Start as before, with no fallback
        }
    }

    [Fact]
    public void Held_files_cant_be_replaced()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "libedid.dll");
        var dll = Path.Combine(Win64(steamVr), "driver_lighthouse.dll");

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, _ => ValveSubject, NoneApproved);
        Assert.Null(refusal);

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

        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, _ => ValveSubject, NoneApproved);
        using (hold)
        {
            Assert.NotNull(refusal);
        }
    }

    [Fact]
    public void Consent_records_each_file_valve_didnt_sign_with_its_sha256()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "vrcamera_api.dll", "ArcturusPerception.dll", "AitH264Capture.dll");
        var files = SteamVrDriverCheck.UnsignedFiles(steamVr, ValveSignsTheDriver);

        Assert.Equal(2, files.Count);
        Assert.Equal(Sha256(Bytes("ArcturusPerception.dll")), files["ArcturusPerception.dll"]);
        Assert.Equal(Sha256(Bytes("AitH264Capture.dll")), files["AitH264Capture.dll"]);
        // What the consent records is exactly what the helper then accepts.
        var (hold, refusal) = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, files);
        using (hold)
        {
            Assert.Null(refusal);
        }
    }

    [Fact]
    public void Consent_without_a_driver_folder_records_nothing()
    {
        using var dir = new TempDir();
        var steamVr = Path.Combine(dir.Path, "SteamVR");
        Directory.CreateDirectory(steamVr);

        Assert.Empty(SteamVrDriverCheck.UnsignedFiles(steamVr, _ => null));
    }

    [Fact]
    public void Consent_that_cant_open_a_file_fails_instead_of_allowing_part()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "ArcturusPerception.dll", "AitH264Capture.dll");

        // Another program writing the file: it can't be held and hashed, so no list is made at all.
        using (File.Open(Path.Combine(Win64(steamVr), "AitH264Capture.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var e = Assert.ThrowsAny<IOException>(() => SteamVrDriverCheck.UnsignedFiles(steamVr, ValveSignsTheDriver));
            Assert.Contains("AitH264Capture.dll", e.Message);
        }
    }

    /// <summary>Makes a junction (no administrator needed) at <paramref name="link"/> pointing at <paramref name="target"/>.</summary>
    private static void Junction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "mklink /J failed");
    }

    [Fact]
    public void Steamvr_reached_through_a_junction_runs_from_the_real_folder()
    {
        using var dir = new TempDir();
        var real = Path.Combine(dir.Path, "real-library");
        Directory.CreateDirectory(real);
        var steamVrReal = Path.Combine(real, "SteamVR");
        Directory.CreateDirectory(Win64(steamVrReal));
        File.WriteAllBytes(Path.Combine(Win64(steamVrReal), "driver_lighthouse.dll"), Bytes("driver_lighthouse.dll"));
        var link = Path.Combine(dir.Path, "linked-library");
        Junction(link, real);

        var check = SteamVrDriverCheck.Verify(Path.Combine(link, "SteamVR"), ValveSignsTheDriver, NoneApproved);
        using (check.Hold)
        {
            Assert.Null(check.Refusal);
            Assert.NotNull(check.SteamVrPath);

            // The folder the held files are really in, not the junction a user could point elsewhere after the check.
            Assert.DoesNotContain("linked-library", check.SteamVrPath);
            Assert.EndsWith(@"\real-library\SteamVR", check.SteamVrPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(check.SteamVrPath.StartsWith(@"\\?\", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(Win64(check.SteamVrPath), "driver_lighthouse.dll")));

            // And that is the folder Monado is told.
            var env = Mirador.Helper.ElevatedMonado.ElevatedEnvironment.Build(
                new ElevatedStart(3000, 100, 6, false, "pimax"),
                new Mirador.Helper.ElevatedMonado.UserFolders(@"C:\Users\u", @"C:\Users\u\AppData\Roaming", @"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Local\Temp"),
                @"C:\Program Files\Mirador\monado\server",
                check.SteamVrPath);
            Assert.Equal(check.SteamVrPath, env["STEAMVR_PATH"]);
        }
    }

    [Fact]
    public void Steamvr_path_is_the_real_folder_without_a_junction_too()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "ArcturusPerception.dll");
        var approved = new Dictionary<string, string> { ["ArcturusPerception.dll"] = Sha256(Bytes("ArcturusPerception.dll")) };

        var check = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, approved);
        using (check.Hold)
        {
            Assert.Null(check.Refusal);
            Assert.EndsWith(@"\SteamVR", check.SteamVrPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(Win64(check.SteamVrPath!), "ArcturusPerception.dll")));
        }
    }

    [Fact]
    public void A_dll_reached_through_a_link_elsewhere_is_refused()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll", "ArcturusPerception.dll");
        var realWin64 = Path.Combine(dir.Path, "SteamVR", "drivers", "lighthouse", "bin", "win64");

        // As if ArcturusPerception.dll were a link to a file in a folder of the user's.
        var check = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, NoneApproved, held =>
            Path.GetFileName(held.Name) == "ArcturusPerception.dll"
                ? @"\\?\C:\Users\someone\Downloads\ArcturusPerception.dll"
                : @"\\?\" + Path.Combine(realWin64, Path.GetFileName(held.Name)));
        using (check.Hold)
        {
            Assert.NotNull(check.Refusal);
            Assert.Null(check.Refusal.Refusal); // a plain refusal: no running as the user instead
            Assert.Contains("ArcturusPerception.dll", check.Refusal.Error);
            Assert.Null(check.SteamVrPath);
        }

        // A refusal holds nothing.
        File.Delete(Path.Combine(Win64(steamVr), "ArcturusPerception.dll"));
    }

    [Fact]
    public void A_driver_whose_real_folder_isnt_laid_out_as_steamvr_is_refused()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll");

        var check = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, NoneApproved, held =>
            @"\\?\C:\elsewhere\win64\" + Path.GetFileName(held.Name));
        using (check.Hold)
        {
            Assert.NotNull(check.Refusal);
            Assert.Null(check.Refusal.Refusal);
            Assert.Null(check.SteamVrPath);
        }
    }

    [Fact]
    public void A_unc_final_path_keeps_its_two_backslashes()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "driver_lighthouse.dll");

        var check = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, NoneApproved, held =>
            @"\\?\UNC\server\share\SteamVR\drivers\lighthouse\bin\win64\" + Path.GetFileName(held.Name));
        using (check.Hold)
        {
            Assert.Null(check.Refusal);
            Assert.Equal(@"\\server\share\SteamVR", check.SteamVrPath);
        }
    }

    [Fact]
    public void No_driver_lighthouse_dll_is_refused()
    {
        using var dir = new TempDir();
        var steamVr = MakeSteamVr(dir, "vrcamera_api.dll");

        var check = SteamVrDriverCheck.Verify(steamVr, ValveSignsTheDriver, NoneApproved);
        using (check.Hold)
        {
            Assert.NotNull(check.Refusal);
            Assert.Null(check.Refusal.Refusal);
            Assert.Contains("driver_lighthouse.dll", check.Refusal.Error);
        }
    }
}
