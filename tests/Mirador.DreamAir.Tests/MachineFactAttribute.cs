namespace Mirador.DreamAir.Tests;

/// <summary>
/// A test that needs this machine set up (here: Pimax Play installed, the headset on, Pimax's software stopped). It
/// is skipped, not failed, unless the environment variable <c>MIRADOR_MACHINE_TESTS</c> is <c>1</c>.
/// </summary>
public sealed class MachineFactAttribute : FactAttribute
{
    public const string Variable = "MIRADOR_MACHINE_TESTS";

    public MachineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Machine test: set {Variable}=1 on a PC with Pimax Play and the Dream Air on, Pimax's software stopped.";
        }
    }
}
