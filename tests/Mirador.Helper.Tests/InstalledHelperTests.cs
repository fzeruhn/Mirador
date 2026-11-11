using Mirador.Ipc;

namespace Mirador.Helper.Tests;

[Trait("Category", "Hardware")]
public class InstalledHelperTests
{
    /// <summary>
    /// Talks to the real helper service on this machine (installed with <c>tools\dev-install-helper.ps1</c>). Set
    /// <c>MIRADOR_MACHINE_TESTS=1</c> to run it: <c>dotnet test tests\Mirador.Helper.Tests --filter Category=Machine</c>.
    /// </summary>
    [Trait("Category", "Machine")]
    [MachineFact]
    public async Task The_installed_helper_answers_ping()
    {
        var reply = await new HelperClient().SendAsync(HelperCommand.Ping, CancellationToken.None);

        Assert.True(reply.Ok, reply.Error);
        Assert.False(string.IsNullOrWhiteSpace(reply.Value));
    }
}
