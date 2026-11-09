using Mirador.Ipc;

namespace Mirador.Helper.Tests;

/// <summary>Records what the handler asks of Mirador's Monado.</summary>
internal sealed class FakeMonadoInstaller : IMonadoInstaller
{
    public string? Current { get; set; }
    public List<string> Installed { get; } = [];
    public int RollBacks { get; private set; }

    public string? CurrentManifest() => Current;

    public bool IsMiradors(string manifest) => manifest.Contains(@"\monado-", StringComparison.Ordinal) || manifest.Contains("/monado-", StringComparison.Ordinal);

    public string? CurrentServerDir() => Current is null ? null : Path.Combine(Path.GetDirectoryName(Current)!, "bin");

    public Task<HelperReply> InstallAsync(string version, CancellationToken ct)
    {
        Installed.Add(version);
        return Task.FromResult(new HelperReply(true, Value: version));
    }

    public HelperReply RollBack()
    {
        RollBacks++;
        return new HelperReply(true, Value: "previous");
    }
}

public sealed class HelperHandlerMonadoTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly FakeRuntimeRegistry registry = new();
    private readonly FakeMonadoInstaller monado = new();

    public void Dispose() => temp.Dispose();

    private string Bundled => temp.File("monado", "runtime", "openxr_monado.json");

    private HelperHandler Handler(IMonadoInstaller? installer) =>
        new(new FakeServiceControl(), new FakeProcessControl(), registry, Bundled, "1.0", monado: installer);

    [Fact]
    public async Task Install_passes_only_the_version_name_on()
    {
        var reply = await Handler(monado).HandleAsync(new HelperRequest(HelperCommand.InstallMonado, Version: "monado-2026.11.10-1a2b3c4d5e"));

        Assert.True(reply.Ok);
        Assert.Equal(["monado-2026.11.10-1a2b3c4d5e"], monado.Installed);
    }

    [Fact]
    public async Task Install_and_roll_back_without_an_installer_are_refused()
    {
        Assert.False((await Handler(null).HandleAsync(new HelperRequest(HelperCommand.InstallMonado, Version: "x"))).Ok);
        Assert.False((await Handler(null).HandleAsync(new HelperRequest(HelperCommand.RollBackMonado))).Ok);
    }

    [Fact]
    public async Task Roll_back_goes_to_the_installer()
    {
        Assert.True((await Handler(monado).HandleAsync(new HelperRequest(HelperCommand.RollBackMonado))).Ok);
        Assert.Equal(1, monado.RollBacks);
    }

    [Fact]
    public async Task Set_runtime_sets_miradors_monado_when_installed_and_records_the_old_one_once()
    {
        var current = temp.File("monado", "monado-2026.11.10-1a2b3c4d5e", "openxr_monado.json");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "{}");
        monado.Current = current;
        registry.Active = @"C:\Pimax\openxr.json";

        var reply = await Handler(monado).HandleAsync(new HelperRequest(HelperCommand.SetRuntimeBundled));

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(current, registry.Active);
        Assert.Equal(@"C:\Pimax\openxr.json", registry.Recorded);

        // Again after an update: the record stays the runtime Mirador replaced, not Mirador's own older version.
        monado.Current = temp.File("monado", "monado-2026.11.11-2b3c4d5e6f", "openxr_monado.json");
        Directory.CreateDirectory(Path.GetDirectoryName(monado.Current)!);
        File.WriteAllText(monado.Current, "{}");
        await Handler(monado).HandleAsync(new HelperRequest(HelperCommand.SetRuntimeBundled));
        Assert.Equal(@"C:\Pimax\openxr.json", registry.Recorded);
    }

    [Fact]
    public async Task Set_runtime_with_nothing_installed_says_where_to_install()
    {
        var reply = await Handler(monado).HandleAsync(new HelperRequest(HelperCommand.SetRuntimeBundled));

        Assert.False(reply.Ok);
        Assert.Contains("isn't installed yet", reply.Error);
    }
}
