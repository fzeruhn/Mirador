using System.Security.Principal;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

/// <summary>The helper's side of approved builds: as the runtime, and taken back (Mirador.md §4.11 item 51).</summary>
public sealed class HelperHandlerApprovalTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private static readonly SecurityIdentifier Finn = new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier Other = new("S-1-5-21-1-2-3-1002");

    private readonly TempDir temp = new();
    private readonly FakeRuntimeRegistry registry = new();
    private readonly FakeApprovedBuildStore store = new();
    private readonly FakeConsentValues consent = new();
    private readonly FakeMonadoInstaller monado = new();

    public void Dispose() => temp.Dispose();

    private string App => temp.File("app");

    private CallerInfo Caller(SecurityIdentifier sid) => new(sid, 1, 4242, Path.Combine(App, "Mirador.exe"));

    private HelperHandler Handler() =>
        new(new FakeServiceControl(), new FakeProcessControl(), registry, "1.0",
            consent: new ElevatedConsent(consent), installDir: App, monado: monado, approved: store);

    private ApprovedBuild Approve(SecurityIdentifier owner, string profileId)
    {
        var folder = temp.File("user", owner.Value, profileId);
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "openxr_monado.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "bin", "openxr_monado.dll"), "dll");
        File.WriteAllText(Path.Combine(folder, "bin", "monado-service.exe"), "service");
        return ApprovedBuilds.Approve(owner.Value, profileId, Commit, folder, App, store);
    }

    [Fact]
    public async Task The_callers_approvals_are_listed_and_one_is_taken_back_never_another_users()
    {
        var gone = Approve(Finn, "p1");
        Approve(Finn, "p2");
        Approve(Other, "p1");

        var listed = await Handler().HandleAsync(new HelperRequest(HelperCommand.ListApprovedBuilds), Caller(Finn));
        Assert.True(listed.Ok, listed.Error);
        Assert.Equal(["p1", "p2"], listed.Value!.Split(',').Order());

        var reply = await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn));

        Assert.True(reply.Ok, reply.Error);
        Assert.Null(store.Get(Finn.Value, "p1"));
        Assert.False(Directory.Exists(gone.Folder));
        Assert.NotNull(store.Get(Finn.Value, "p2"));
        Assert.NotNull(store.Get(Other.Value, "p1"));
        Assert.Equal("p2", (await Handler().HandleAsync(new HelperRequest(HelperCommand.ListApprovedBuilds), Caller(Finn))).Value);
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_hands_it_to_miradors_monado()
    {
        var build = Approve(Finn, "p1");
        registry.Active = build.Manifest;
        var miradors = temp.File("app", "monado", "monado-2026.11.10-1a2b3c4d5e", "openxr_monado.json");
        Directory.CreateDirectory(Path.GetDirectoryName(miradors)!);
        File.WriteAllText(miradors, "{}");
        monado.Current = miradors;

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Equal(miradors, registry.Active);
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_without_the_bundled_monado_gives_back_the_runtime_it_replaced()
    {
        consent.Set(Finn.Value);
        var steamVr = temp.File("SteamVR", "steamxr_win64.json");
        Directory.CreateDirectory(Path.GetDirectoryName(steamVr)!);
        File.WriteAllText(steamVr, "{}");
        registry.Active = steamVr;
        var build = Approve(Finn, "p1");
        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn))).Ok);
        Assert.Equal(build.Manifest, registry.Active);

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Equal(steamVr, registry.Active);
        Assert.Null(registry.Recorded);
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_doesnt_give_back_a_runtime_that_is_gone()
    {
        consent.Set(Finn.Value);
        registry.Active = temp.File("uninstalled", "steamxr_win64.json");
        var build = Approve(Finn, "p1");
        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn))).Ok);

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Null(registry.Active);
        Assert.Null(registry.Recorded);
        Assert.False(Directory.Exists(build.Folder));
    }

    [Fact]
    public async Task A_forgotten_copy_that_is_the_runtime_with_nothing_to_go_back_to_leaves_no_runtime()
    {
        var build = Approve(Finn, "p1");
        registry.Active = build.Manifest;

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.ForgetApprovedBuild, Build: "p1"), Caller(Finn))).Ok);

        Assert.Null(registry.Active);
    }

    [Fact]
    public async Task Taking_the_consent_back_keeps_the_approvals()
    {
        consent.Set(Finn.Value);
        Approve(Finn, "p1");
        Approve(Finn, "p2");

        Assert.True((await Handler().HandleAsync(new HelperRequest(HelperCommand.RevokeElevatedMonado), Caller(Finn))).Ok);

        Assert.Equal(["p1", "p2"], store.ProfilesOf(Finn.Value).Order());
    }

    [Fact]
    public async Task An_approved_build_becomes_the_runtime_without_the_consent_for_monado_as_administrator()
    {
        var build = Approve(Finn, "p1");
        registry.Active = @"C:\SteamVR\steamxr.json";

        var reply = await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn));

        Assert.True(reply.Ok, reply.Error);
        Assert.Equal(build.Manifest, registry.Active);
        Assert.Equal(@"C:\SteamVR\steamxr.json", registry.Recorded);
    }

    [Fact]
    public async Task Another_users_approval_and_a_changed_copy_are_refused_leaving_the_runtime()
    {
        var build = Approve(Finn, "p1");
        consent.Set(Other.Value);
        consent.Set(Finn.Value);
        registry.Active = @"C:\SteamVR\steamxr.json";

        Assert.False((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Other))).Ok);

        File.WriteAllText(Path.Combine(build.ServerDir, "monado-service.exe"), "changed after the approval");
        Assert.False((await Handler().HandleAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: "p1"), Caller(Finn))).Ok);
        Assert.Equal(@"C:\SteamVR\steamxr.json", registry.Active);
    }
}
