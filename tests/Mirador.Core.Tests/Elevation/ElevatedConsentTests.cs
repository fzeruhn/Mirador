using System.Security.Principal;
using Mirador.Core.Elevation;
using Mirador.Ipc;

namespace Mirador.Core.Tests.Elevation;

public class ElevatedConsentTests
{
    private static readonly SecurityIdentifier Alice = new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier Bob = new("S-1-5-21-1-2-3-1002");

    private const string HashA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string HashB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static readonly IReadOnlyDictionary<string, string> NoFiles = new Dictionary<string, string>();

    private sealed class FakeValues : IConsentValues
    {
        public HashSet<string> Sids { get; } = [];
        public Dictionary<string, string[]> FileLines { get; } = [];
        public Exception? Fails { get; set; }

        public bool Has(string sid) => Sids.Contains(sid);

        public void Set(string sid)
        {
            if (Fails is not null)
            {
                throw Fails;
            }

            Sids.Add(sid);
        }

        public void Delete(string sid) => Sids.Remove(sid);

        public string[]? Files(string sid) => FileLines.TryGetValue(sid, out var lines) ? lines : null;

        public void SetFiles(string sid, string[] lines)
        {
            if (Fails is not null)
            {
                throw Fails;
            }

            FileLines[sid] = lines;
        }

        public void DeleteFiles(string sid) => FileLines.Remove(sid);
    }

    [Fact]
    public void Nobody_is_allowed_at_first()
    {
        var consent = new ElevatedConsent(new FakeValues());

        Assert.False(consent.IsAllowed(Alice));
        Assert.False(consent.IsAllowed(Bob));
        Assert.Empty(consent.ApprovedFiles(Alice));
    }

    [Fact]
    public void Allow_then_revoke_one_sid_keeps_the_other()
    {
        var values = new FakeValues();
        var consent = new ElevatedConsent(values);

        consent.Allow(Alice, NoFiles);
        consent.Allow(Bob, NoFiles);
        consent.Revoke(Alice);

        Assert.False(consent.IsAllowed(Alice));
        Assert.True(consent.IsAllowed(Bob));
        Assert.Equal([Bob.Value], values.Sids);
    }

    [Fact]
    public void Revoking_a_sid_that_was_never_allowed_is_fine()
    {
        var consent = new ElevatedConsent(new FakeValues());

        consent.Revoke(Alice);

        Assert.False(consent.IsAllowed(Alice));
    }

    [Fact]
    public void Allow_records_unsigned_files_for_that_sid_only()
    {
        var values = new FakeValues();
        var consent = new ElevatedConsent(values);

        consent.Allow(Alice, new Dictionary<string, string> { ["ArcturusPerception.dll"] = HashA, ["AitH264Capture.dll"] = HashB });
        consent.Allow(Bob, NoFiles);

        // One line per file, "<file name>=<sha256>", in a value named after the SID.
        Assert.Equal(["AitH264Capture.dll=" + HashB, "ArcturusPerception.dll=" + HashA], values.FileLines[Alice.Value]);
        Assert.False(values.FileLines.ContainsKey(Bob.Value));

        var alices = consent.ApprovedFiles(Alice);
        Assert.Equal(2, alices.Count);
        Assert.Equal(HashA, alices["ArcturusPerception.dll"]);
        Assert.Equal(HashB, alices["aith264capture.dll"]); // file names compare as Windows does
        Assert.Empty(consent.ApprovedFiles(Bob));

        // Allowing again replaces the list.
        consent.Allow(Alice, new Dictionary<string, string> { ["ArcturusPerception.dll"] = HashB });
        Assert.Equal(["ArcturusPerception.dll=" + HashB], values.FileLines[Alice.Value]);
        consent.Allow(Alice, NoFiles);
        Assert.Empty(consent.ApprovedFiles(Alice));
        Assert.True(consent.IsAllowed(Alice));
    }

    [Fact]
    public void Revoke_deletes_the_files_too()
    {
        var values = new FakeValues();
        var consent = new ElevatedConsent(values);
        consent.Allow(Alice, new Dictionary<string, string> { ["ArcturusPerception.dll"] = HashA });
        consent.Allow(Bob, new Dictionary<string, string> { ["ArcturusPerception.dll"] = HashB });

        consent.Revoke(Alice);

        Assert.False(values.FileLines.ContainsKey(Alice.Value));
        Assert.Empty(consent.ApprovedFiles(Alice));
        Assert.Equal(HashB, consent.ApprovedFiles(Bob)["ArcturusPerception.dll"]);
    }

    [Theory]
    [InlineData("no equals sign")]
    [InlineData("=" + HashA)]                                     // no name
    [InlineData("ArcturusPerception.dll=")]                       // no hash
    [InlineData("ArcturusPerception.dll=ABCDEF")]                 // not a SHA-256
    [InlineData("ArcturusPerception.dll=" + HashA + "00")]        // too long
    [InlineData("ArcturusPerception.dll=0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")] // upper case
    [InlineData(@"..\ArcturusPerception.dll=" + HashA)]           // a path, not a name
    public void A_line_that_isnt_a_name_and_a_hash_approves_nothing(string line)
    {
        var values = new FakeValues();
        values.FileLines[Alice.Value] = [line];

        Assert.Empty(new ElevatedConsent(values).ApprovedFiles(Alice));
    }

    [Fact]
    public void Allow_refuses_a_value_that_isnt_a_sha256()
    {
        var consent = new ElevatedConsent(new FakeValues());

        Assert.Throws<ArgumentException>(() => consent.Allow(Alice, new Dictionary<string, string> { ["a.dll"] = "nope" }));
        Assert.Throws<ArgumentException>(() => consent.Allow(Alice, new Dictionary<string, string> { [@"sub\a.dll"] = HashA }));
        Assert.False(consent.IsAllowed(Alice));
    }

    [Fact]
    public void Allow_command_allows_the_running_user_and_exits_0()
    {
        var values = new FakeValues();
        var errors = new List<string>();

        var code = AllowElevatedCommand.Run(new ElevatedConsent(values), Alice, () => NoFiles, errors.Add);

        Assert.Equal(0, code);
        Assert.Equal([Alice.Value], values.Sids);
        Assert.Empty(errors);
    }

    [Fact]
    public void Allow_command_records_the_unsigned_files_it_found()
    {
        var values = new FakeValues();
        var errors = new List<string>();

        var code = AllowElevatedCommand.Run(
            new ElevatedConsent(values), Alice, () => new Dictionary<string, string> { ["ArcturusPerception.dll"] = HashA }, errors.Add);

        Assert.Equal(0, code);
        Assert.Equal(["ArcturusPerception.dll=" + HashA], values.FileLines[Alice.Value]);
        Assert.Empty(errors);
    }

    [Fact]
    public void Allow_command_that_cannot_write_exits_1_and_says_why()
    {
        var values = new FakeValues { Fails = new UnauthorizedAccessException("Attempted to perform an unauthorized operation.") };
        var errors = new List<string>();

        var code = AllowElevatedCommand.Run(new ElevatedConsent(values), Alice, () => NoFiles, errors.Add);

        Assert.Equal(1, code);
        Assert.Empty(values.Sids);
        var error = Assert.Single(errors);
        Assert.Contains("unauthorized operation", error);
    }

    [Fact]
    public void Allow_command_that_cannot_hash_the_files_records_nothing()
    {
        var values = new FakeValues();
        var errors = new List<string>();

        var code = AllowElevatedCommand.Run(
            new ElevatedConsent(values), Alice, () => throw new IOException("The disk went away."), errors.Add);

        Assert.Equal(1, code);
        Assert.Empty(values.Sids);
        Assert.Empty(values.FileLines);
        Assert.Contains("disk went away", Assert.Single(errors));
    }

    [Fact]
    public void Lighthouse_files_without_steamvr_are_none()
    {
        var files = AllowElevatedCommand.UnsignedLighthouseFiles(
            steamPath: null, readFile: _ => null, dirExists: _ => false, signer: _ => null);

        Assert.Empty(files);
    }

    [Fact]
    public void Lighthouse_files_are_found_as_the_helper_finds_them()
    {
        using var dir = new TempDir();
        var steam = dir.File("Steam");
        var steamVr = Path.Combine(steam, "steamapps", "common", "SteamVR");
        var win64 = Path.Combine(steamVr, "drivers", "lighthouse", "bin", "win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(
            Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\" { \"0\" { \"path\" \"" + steam.Replace(@"\", @"\\") + "\" \"apps\" { \"250820\" \"1\" } } }");
        File.WriteAllBytes(Path.Combine(win64, "driver_lighthouse.dll"), [0x4D, 0x5A, 1]);
        File.WriteAllBytes(Path.Combine(win64, "ArcturusPerception.dll"), [0x4D, 0x5A, 2]);
        var files = AllowElevatedCommand.UnsignedLighthouseFiles(steam, File.ReadAllText, Directory.Exists, ValveSignsTheDriver);

        var only = Assert.Single(files);
        Assert.Equal("ArcturusPerception.dll", only.Key);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 0x4D, 0x5A, 2 })), only.Value);
    }

    private static string? ValveSignsTheDriver(string path) =>
        Path.GetFileName(path) == "driver_lighthouse.dll" ? "CN=Valve Corp., O=Valve Corp., L=Bellevue, S=Washington, C=US" : null;

    [Fact]
    public void Allow_command_that_cant_open_a_driver_file_exits_1_and_records_nothing()
    {
        using var dir = new TempDir();
        var steam = dir.File("Steam");
        var win64 = Path.Combine(steam, "steamapps", "common", "SteamVR", "drivers", "lighthouse", "bin", "win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(
            Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            "\"libraryfolders\" { \"0\" { \"path\" \"" + steam.Replace(@"\", @"\\") + "\" \"apps\" { \"250820\" \"1\" } } }");
        File.WriteAllBytes(Path.Combine(win64, "driver_lighthouse.dll"), [0x4D, 0x5A, 1]);
        File.WriteAllBytes(Path.Combine(win64, "ArcturusPerception.dll"), [0x4D, 0x5A, 2]);
        File.WriteAllBytes(Path.Combine(win64, "AitH264Capture.dll"), [0x4D, 0x5A, 3]);
        var values = new FakeValues();
        values.Sids.Add(Alice.Value);
        values.FileLines[Alice.Value] = ["ArcturusPerception.dll=" + HashA];
        var errors = new List<string>();

        // Steam writing one of the files: the approval would be incomplete, so none is recorded and the step fails.
        int code;
        using (File.Open(Path.Combine(win64, "AitH264Capture.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            code = AllowElevatedCommand.Run(
                new ElevatedConsent(values),
                Alice,
                () => AllowElevatedCommand.UnsignedLighthouseFiles(steam, File.ReadAllText, Directory.Exists, ValveSignsTheDriver),
                errors.Add);
        }

        Assert.Equal(1, code);
        Assert.Equal(["ArcturusPerception.dll=" + HashA], values.FileLines[Alice.Value]); // the earlier list stays as it was
        var error = Assert.Single(errors);
        Assert.Contains("AitH264Capture.dll", error);
        Assert.Contains("not allowed", error);
    }
}

