namespace Mirador.Helper.Tests;

/// <summary>
/// A test that needs this machine set up (here: the helper service installed and running). It is skipped, not failed,
/// unless the environment variable <c>MIRADOR_MACHINE_TESTS</c> is <c>1</c>.
/// </summary>
public sealed class MachineFactAttribute : FactAttribute
{
    public const string Variable = "MIRADOR_MACHINE_TESTS";

    public MachineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Machine test: set {Variable}=1 on a machine where the Mirador helper service is installed.";
        }
    }
}
