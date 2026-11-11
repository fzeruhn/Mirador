using Mirador.Core.Monado;

namespace Mirador.Core.Tests.Monado;

public class StandInTests
{
    [Fact]
    public void A_program_that_says_something_else_is_not_the_stand_in()
    {
        Assert.False(StandIn.IsStandIn(Environment.ProcessPath!)); // the test host
        Assert.False(StandIn.IsStandIn(Path.Combine(Environment.SystemDirectory, "ping.exe")));
    }

    [Fact]
    public void A_file_that_is_missing_or_no_program_is_not_the_stand_in()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("monado-service.exe"), "not a program");

        Assert.False(StandIn.IsStandIn(dir.File("missing.exe")));
        Assert.False(StandIn.IsStandIn(dir.File("monado-service.exe")));
        Assert.False(StandIn.IsStandIn(""));
    }

    private static MonadoInstall InstallIn(string runtimeDir) => new(
        Path.Combine(runtimeDir, "openxr_monado.json"), Path.Combine(runtimeDir, "openxr_monado.dll"),
        Path.Combine(runtimeDir, "..", "server", "monado-service.exe"), Path.Combine(runtimeDir, "service"), IsBundled: false);

    [Fact]
    public void A_stand_in_belongs_to_the_monado_it_sits_below_however_its_path_is_written()
    {
        using var dir = new TempDir();
        var runtime = dir.File("My Build", "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "service"));
        var standIn = Path.Combine(runtime, "service", StandIn.FileName);
        File.WriteAllText(standIn, "stand-in");

        Assert.True(StandIn.BelongsTo(standIn, InstallIn(runtime)));
        Assert.True(StandIn.BelongsTo(standIn.ToUpperInvariant(), InstallIn(runtime)));
        Assert.True(StandIn.BelongsTo(Path.Combine(runtime, "service", "..", "service", StandIn.FileName), InstallIn(runtime)));
    }

    [Fact]
    public void Another_builds_stand_in_does_not_belong()
    {
        using var dir = new TempDir();
        foreach (var build in new[] { "a", "b" })
        {
            Directory.CreateDirectory(dir.File(build, "service"));
            File.WriteAllText(dir.File(build, "service", StandIn.FileName), "stand-in " + build);
        }

        Assert.False(StandIn.BelongsTo(dir.File("a", "service", StandIn.FileName), InstallIn(dir.File("b"))));
        Assert.False(StandIn.BelongsTo(dir.File("a", "service", StandIn.FileName), InstallIn(dir.File("c")))); // nothing there
    }

    [Fact]
    public void The_stand_in_of_a_builds_approved_copy_belongs_to_that_build()
    {
        using var dir = new TempDir();
        var copy = dir.File("app", "builds", "S-1-5-21-1", "p1", "abc1234");
        Directory.CreateDirectory(Path.Combine(copy, "bin", "service"));
        var standIn = Path.Combine(copy, "bin", "service", StandIn.FileName);
        File.WriteAllText(standIn, "stand-in");
        var approved = new Mirador.Ipc.ApprovedBuild("S-1-5-21-1", "p1", "abc1234", copy, new Dictionary<string, string>());

        Assert.False(StandIn.BelongsTo(standIn, InstallIn(dir.File("user"))));
        Assert.True(StandIn.BelongsTo(standIn, InstallIn(dir.File("user")) with { Approved = approved }));
    }

    [Fact]
    public void Only_a_stand_in_caller_names_a_program()
    {
        Assert.Null(StandIn.ProgramOf(null, _ => throw new InvalidOperationException("not asked")));
        Assert.Null(StandIn.ProgramOf(42, _ => null));
        Assert.Null(StandIn.ProgramOf(42, _ => Environment.ProcessPath));
        if (File.Exists(StandInExeTests.Exe))
        {
            Assert.Equal(StandInExeTests.Exe, StandIn.ProgramOf(42, _ => StandInExeTests.Exe));
        }
    }
}
