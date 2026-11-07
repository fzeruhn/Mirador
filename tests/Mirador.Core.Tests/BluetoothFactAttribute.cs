namespace Mirador.Core.Tests;

/// <summary>
/// A test that talks to the real base stations over Bluetooth, switching them. It is skipped, not failed, unless the
/// environment variable <c>MIRADOR_BLUETOOTH_TESTS</c> is <c>1</c>. The stations' Bluetooth names come from
/// <c>MIRADOR_BLUETOOTH_STATIONS</c>, comma-separated.
/// </summary>
public sealed class BluetoothFactAttribute : FactAttribute
{
    /// <summary>The environment variable that switches these tests on (set it to 1).</summary>
    public const string Variable = "MIRADOR_BLUETOOTH_TESTS";
    /// <summary>The environment variable naming the stations to test, comma-separated.</summary>
    public const string StationsVariable = "MIRADOR_BLUETOOTH_STATIONS";

    /// <summary>Skips the test unless <see cref="Variable"/> is 1.</summary>
    public BluetoothFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Bluetooth test: set {Variable}=1 and {StationsVariable}=<station names, comma-separated> on a PC near the base stations. It switches them.";
        }
    }
}
