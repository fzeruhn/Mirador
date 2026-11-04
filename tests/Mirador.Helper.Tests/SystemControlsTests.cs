using System.Diagnostics;
using Microsoft.Win32;

namespace Mirador.Helper.Tests;

/// <summary>
/// The real controls, as far as an unelevated test can reach them: reading services, ending and listing a process
/// of the test's own, and the registry code against a scratch key under <c>HKEY_CURRENT_USER</c>.
/// </summary>
public sealed class SystemControlsTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly List<Process> started = [];
    private readonly string scratch = @"Software\MiradorHelperTests-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        foreach (var process in started)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            process.Dispose();
        }

        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        hkcu.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
        temp.Dispose();
    }

    /// <summary>Starts a copy of <c>ping.exe</c> under a name no other program has, so nothing else is touched.</summary>
    private (string Name, Process Process) StartSleeper()
    {
        var name = "mirador-test-" + Guid.NewGuid().ToString("N")[..12];
        var exe = temp.File(name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        var process = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        started.Add(process);
        return (name, process);
    }

    // Services

    [Fact]
    public void A_service_that_exists_and_runs_is_found()
    {
        var services = new SystemServiceControl();

        Assert.True(services.Exists("EventLog"));
        Assert.True(services.IsRunning("EventLog"));
    }

    [Fact]
    public void A_service_that_is_not_installed_does_not_exist_and_is_not_running()
    {
        var services = new SystemServiceControl();
        var name = "MiradorNoSuchService" + Guid.NewGuid().ToString("N");

        Assert.False(services.Exists(name));
        Assert.False(services.IsRunning(name));
    }

    [Fact]
    public void Service_names_match_ignoring_case()
    {
        Assert.True(new SystemServiceControl().Exists("eventlog"));
    }

    // Processes

    [Fact]
    public void KillAll_ends_the_named_processes_and_only_those()
    {
        var (name, target) = StartSleeper();
        var (otherName, other) = StartSleeper();

        new SystemProcessControl().KillAll([name]);

        Assert.True(target.HasExited);
        Assert.False(other.HasExited);
        Assert.NotEqual(name, otherName);
    }

    [Fact]
    public void KillAll_of_a_process_that_is_not_running_is_fine()
    {
        new SystemProcessControl().KillAll(["mirador-test-not-running-" + Guid.NewGuid().ToString("N")]);
    }

    [Fact]
    public void CommandLines_lists_the_command_line_of_each_running_process_of_that_name()
    {
        var (name, _) = StartSleeper();
        var (_, _) = StartSleeper();

        var lines = new SystemProcessControl().CommandLines(name);

        var line = Assert.Single(lines);
        Assert.Contains(name + ".exe", line);
        Assert.EndsWith("-n 60 127.0.0.1", line);
    }

    [Fact]
    public void CommandLines_of_a_process_that_is_not_running_is_empty()
    {
        Assert.Empty(new SystemProcessControl().CommandLines("mirador-test-not-running-" + Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData("x' OR Name LIKE '%")]
    [InlineData("a b")]
    [InlineData("a\"b")]
    [InlineData("")]
    public void CommandLines_refuses_a_name_that_is_not_a_plain_process_name(string name)
    {
        Assert.Throws<ArgumentException>(() => new SystemProcessControl().CommandLines(name));
    }

    // The registry

    private SystemRuntimeRegistry Registry() =>
        new(RegistryHive.CurrentUser, scratch + @"\OpenXR\1", scratch + @"\Mirador");

    private string? Read(string subKey, string value)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.OpenSubKey(scratch + @"\" + subKey);
        return key?.GetValue(value) as string;
    }

    [Fact]
    public void With_nothing_set_both_values_are_null()
    {
        var registry = Registry();

        Assert.Null(registry.GetActive());
        Assert.Null(registry.GetRecorded());
    }

    [Fact]
    public void The_active_runtime_is_written_as_a_string_under_the_openxr_key()
    {
        var registry = Registry();

        registry.SetActive(@"C:\Program Files\Mirador\monado\runtime\openxr_monado.json");

        Assert.Equal(@"C:\Program Files\Mirador\monado\runtime\openxr_monado.json", registry.GetActive());
        Assert.Equal(@"C:\Program Files\Mirador\monado\runtime\openxr_monado.json", Read(@"OpenXR\1", "ActiveRuntime"));
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.OpenSubKey(scratch + @"\OpenXR\1")!;
        Assert.Equal(RegistryValueKind.String, key.GetValueKind("ActiveRuntime"));
    }

    [Fact]
    public void The_recorded_runtime_is_written_under_the_mirador_key_and_null_forgets_it()
    {
        var registry = Registry();

        registry.SetRecorded(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json");
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", registry.GetRecorded());
        Assert.Equal(@"C:\Program Files\Pimax\Runtime\PiOpenXR_64.json", Read("Mirador", "PreviousActiveRuntime"));

        registry.SetRecorded(null);
        Assert.Null(registry.GetRecorded());
        Assert.Null(Read("Mirador", "PreviousActiveRuntime"));
    }

    [Fact]
    public void Forgetting_a_record_that_is_not_there_is_fine()
    {
        var registry = Registry();

        registry.SetRecorded(null);
        registry.SetRecorded("x");
        registry.SetRecorded(null);
        registry.SetRecorded(null);

        Assert.Null(registry.GetRecorded());
    }

    [Fact]
    public void Writing_one_value_leaves_the_other_alone()
    {
        var registry = Registry();
        registry.SetRecorded("recorded");

        registry.SetActive("active");

        Assert.Equal("recorded", registry.GetRecorded());
        Assert.Equal("active", registry.GetActive());
    }
}
