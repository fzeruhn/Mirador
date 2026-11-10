namespace Mirador.Pimax.Tests;

/// <summary>Reads this PC's real registry: values every Windows has.</summary>
public class RegistryReaderTests
{
    private readonly WindowsRegistryReader reg = new();

    [Fact]
    public void Reads_a_string_value_from_the_64_bit_view()
    {
        Assert.False(string.IsNullOrWhiteSpace(reg.GetString(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")));
    }

    [Fact]
    public void Expands_variables_in_expandable_values()
    {
        // ProgramFilesPath is stored as %ProgramFiles%.
        var path = reg.GetString(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion", "ProgramFilesPath");

        Assert.NotNull(path);
        Assert.DoesNotContain('%', path);
    }

    [Fact]
    public void Lists_subkeys()
    {
        Assert.Contains("CurrentVersion", reg.SubKeyNames(@"HKLM\SOFTWARE\Microsoft\Windows"), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_the_current_users_hive()
    {
        Assert.NotEmpty(reg.SubKeyNames(@"HKCU\Software"));
    }

    [Fact]
    public void A_missing_key_or_value_reads_as_missing()
    {
        Assert.Null(reg.GetString(@"HKLM\SOFTWARE\Mirador-tests-no-such-key", "x"));
        Assert.Null(reg.GetString(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "Mirador-tests-no-such-value"));
        Assert.Empty(reg.SubKeyNames(@"HKLM\SOFTWARE\Mirador-tests-no-such-key"));
    }

    [Fact]
    public void A_value_that_isnt_a_string_reads_as_missing()
    {
        // CurrentMajorVersionNumber is a DWORD.
        Assert.Null(reg.GetString(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentMajorVersionNumber"));
    }

    [Fact]
    public void An_unknown_hive_is_a_mistake_in_mirador()
    {
        Assert.Throws<ArgumentException>(() => reg.GetString(@"HKCR\.txt", ""));
    }
}
