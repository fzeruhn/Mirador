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

    public void Start(string name)
    {
        using var service = new ServiceController(name);
        try
        {
            service.Start();
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

    public IReadOnlyList<string> CommandLines(string processName)
    {
        // The name goes into a WMI query; only plain process names are let through.
        if (!PlainName().IsMatch(processName))
        {
            throw new ArgumentException("Not a plain process name.", nameof(processName));
        }

        var lines = new List<string>();
        using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE Name = '{processName}.exe'");
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                if (item["CommandLine"] is string { Length: > 0 } line)
                {
                    lines.Add(line);
                }
            }
        }

        return lines;
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
