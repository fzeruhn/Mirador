using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Mirador.Helper;

/// <summary>The real <see cref="IServiceControl"/>, through the Windows service manager.</summary>
public sealed class SystemServiceControl : IServiceControl
{
    /// <summary><c>ERROR_SERVICE_NOT_ACTIVE</c>: the service stopped by itself between looking and asking it to.</summary>
    private const int ErrorServiceNotActive = 1062;

    /// <summary><c>ERROR_SERVICE_ALREADY_RUNNING</c>: the service started between looking and asking it to.</summary>
    private const int ErrorServiceAlreadyRunning = 1056;

    public (int Id, DateTime Started)? Process(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2",
                $"SELECT ProcessId FROM Win32_Service WHERE Name = '{name}'",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true });
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item["ProcessId"] is uint id && id != 0)
                    {
                        using var process = System.Diagnostics.Process.GetProcessById((int)id);
                        return ((int)id, process.StartTime);
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Not running, gone meanwhile, or Windows won't say: no service process to trust.
        }

        return null;
    }

    public string? ImagePath(string name)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("ImagePath") as string;
    }

    public bool Exists(string name)
    {
        var services = ServiceController.GetServices();
        try
        {
            return services.Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var service in services)
            {
                service.Dispose();
            }
        }
    }

    public bool IsRunning(string name)
    {
        if (!Exists(name))
        {
            return false;
        }

        using var service = new ServiceController(name);
        return service.Status != ServiceControllerStatus.Stopped;
    }

    public void Stop(string name, TimeSpan timeout)
    {
        using var service = new ServiceController(name);
        try
        {
            if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                service.Stop();
            }
        }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: ErrorServiceNotActive })
        {
            return;
        }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception inner)
        {
            throw new InvalidOperationException($"Windows wouldn't stop the service {name}: {inner.Message}", e);
        }

        try
        {
            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
        }
        catch (System.ServiceProcess.TimeoutException e)
        {
            throw new InvalidOperationException(
                $"The service {name} didn't stop within {timeout.TotalSeconds:0} seconds. Close the program that runs it, then try again.", e);
        }
    }

    public void Start(string name, TimeSpan timeout)
    {
        using var service = new ServiceController(name);
        try
        {
            if (service.Status == ServiceControllerStatus.StopPending)
            {
                // A stop that timed out may still finish; starting now would fail, and leaving it would leave it stopped.
                service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            }

            if (service.Status == ServiceControllerStatus.Stopped)
            {
                service.Start();
            }
        }
        catch (System.ServiceProcess.TimeoutException e)
        {
            throw new InvalidOperationException(
                $"The service {name} was still stopping after {timeout.TotalSeconds:0} seconds, so it wasn't started again. Start it in Services, or restart Windows.", e);
        }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception { NativeErrorCode: ErrorServiceAlreadyRunning })
        {
            // It started between looking and asking (Windows, or Pimax itself): that is what was wanted.
        }
        catch (InvalidOperationException e) when (e.InnerException is Win32Exception inner)
        {
            throw new InvalidOperationException($"Windows wouldn't start the service {name}: {inner.Message}", e);
        }
    }
}

/// <summary>The real <see cref="IProcessControl"/>.</summary>
public sealed partial class SystemProcessControl : IProcessControl
{
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long WMI may take to answer. Its default is to wait for ever, and the helper handles one request at a time,
    /// so a WMI service that hangs would otherwise hang the helper.
    /// </summary>
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, IEnumerable<(string? Path, string? CommandLine, int? ParentId, DateTime? Started)>> queryCommandLines;

    public SystemProcessControl() : this(QueryWmi)
    {
    }

    /// <summary>For tests: answers the command-line query without WMI.</summary>
    internal SystemProcessControl(Func<string, IEnumerable<(string? Path, string? CommandLine, int? ParentId, DateTime? Started)>> queryCommandLines) =>
        this.queryCommandLines = queryCommandLines;

    public void KillAll(IEnumerable<string> names)
    {
        string? failure = null;
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(ExitWait);
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone, which is what was asked for.
                    }
                    catch (Win32Exception e)
                    {
                        failure ??= $"Windows wouldn't end {name}: {e.Message}";
                    }
                }
            }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }
    }

    public IReadOnlyList<ProcessCommandLine> CommandLines(string processName)
    {
        // The name goes into a WMI query; only plain process names are let through.
        if (!PlainName().IsMatch(processName))
        {
            throw new ArgumentException("Not a plain process name.", nameof(processName));
        }

        try
        {
            return [.. queryCommandLines(processName)
                .Where(p => !string.IsNullOrEmpty(p.CommandLine))
                .Select(p => new ProcessCommandLine(p.Path, p.CommandLine!, p.ParentId, p.Started))];
        }
        catch (ManagementException e)
        {
            throw new InvalidOperationException(
                e.ErrorCode == ManagementStatus.Timedout
                    ? $"Windows couldn't list the running programs within {WmiTimeout.TotalSeconds:0} seconds. Try again."
                    : $"Windows couldn't list the running programs: {e.Message}",
                e);
        }
    }

    /// <summary>The WMI query for the command lines of the running programs called <paramref name="processName"/>.</summary>
    internal static ManagementObjectSearcher CreateSearcher(string processName) =>
        new(
            @"root\cimv2",
            $"SELECT CommandLine, ExecutablePath, ParentProcessId, CreationDate FROM Win32_Process WHERE Name = '{processName}.exe'",
            new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true });

    private static IEnumerable<(string? Path, string? CommandLine, int? ParentId, DateTime? Started)> QueryWmi(string processName)
    {
        using var searcher = CreateSearcher(processName);
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                var parent = item["ParentProcessId"] is uint p ? (int?)p : null;
                DateTime? started = null;
                if (item["CreationDate"] is string created)
                {
                    try
                    {
                        started = ManagementDateTimeConverter.ToDateTime(created);
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                yield return (item["ExecutablePath"] as string, item["CommandLine"] as string, parent, started);
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}

/// <summary>
/// The real <see cref="IRuntimeRegistry"/>: <c>ActiveRuntime</c> under <c>SOFTWARE\Khronos\OpenXR\1</c> and
/// <c>PreviousActiveRuntime</c> under <c>SOFTWARE\Mirador</c>, in the 64-bit view of <c>HKEY_LOCAL_MACHINE</c>.
/// </summary>
public sealed class SystemRuntimeRegistry : IRuntimeRegistry
{
    private const string ActiveKey = @"SOFTWARE\Khronos\OpenXR\1";
    private const string ActiveValue = "ActiveRuntime";
    private const string RecordedKey = @"SOFTWARE\Mirador";
    private const string RecordedValue = "PreviousActiveRuntime";

    private readonly RegistryHive hive;
    private readonly string activeKey;
    private readonly string recordedKey;

    public SystemRuntimeRegistry() : this(RegistryHive.LocalMachine, ActiveKey, RecordedKey)
    {
    }

    /// <summary>For tests, which point the same code at a scratch key instead of the machine's.</summary>
    internal SystemRuntimeRegistry(RegistryHive hive, string activeKey, string recordedKey)
    {
        this.hive = hive;
        this.activeKey = activeKey;
        this.recordedKey = recordedKey;
    }

    public string? GetActive() => Read(activeKey, ActiveValue);

    public void SetActive(string path) => Write(activeKey, ActiveValue, path);

    public string? GetRecorded() => Read(recordedKey, RecordedValue);

    public void SetRecorded(string? path)
    {
        if (path is not null)
        {
            Write(recordedKey, RecordedValue, path);
            return;
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(recordedKey, writable: true);
        key?.DeleteValue(RecordedValue, throwOnMissingValue: false);
    }

    public void SetAvailable(string manifest, bool available)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        if (available)
        {
            using var key = root.CreateSubKey(activeKey + @"\AvailableRuntimes", writable: true);
            key.SetValue(manifest, 0, RegistryValueKind.DWord);
        }
        else
        {
            using var key = root.OpenSubKey(activeKey + @"\AvailableRuntimes", writable: true);
            key?.DeleteValue(manifest, throwOnMissingValue: false);
        }
    }

    private string? Read(string subKey, string value)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(subKey);
        return key?.GetValue(value) as string;
    }

    private void Write(string subKey, string value, string data)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(subKey, writable: true);
        key.SetValue(value, data, RegistryValueKind.String);
    }
}
