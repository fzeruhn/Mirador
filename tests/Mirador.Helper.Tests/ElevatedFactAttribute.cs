namespace Mirador.Helper.Tests;

/// <summary>
/// A test of the helper's real pipe. Its permissions let only SYSTEM and Administrators open more instances of it (the
/// helper runs as LocalSystem), so an unelevated test process can't serve a second connection: skipped unless elevated.
/// </summary>
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        Skip = Elevated.SkipReason;
    }
}

/// <inheritdoc cref="ElevatedFactAttribute"/>
public sealed class ElevatedTheoryAttribute : TheoryAttribute
{
    public ElevatedTheoryAttribute()
    {
        Skip = Elevated.SkipReason;
    }
}

internal static class Elevated
{
    public static string? SkipReason => Environment.IsPrivilegedProcess
        ? null
        : "The helper's pipe lets only SYSTEM and Administrators open more instances: run this test elevated.";
}
