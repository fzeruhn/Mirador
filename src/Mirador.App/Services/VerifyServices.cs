using System.Globalization;
using Mirador.Core;
using Mirador.Core.Build;
using Mirador.Core.Monado;
using Mirador.Core.Profiles;
using Mirador.Ipc;

namespace Mirador.App.Services;

/// <summary>
/// Monado started headless for "Verify build for Windows" (Mirador.md §4.11 item 49), for <paramref name="profile"/>:
/// its build's own service as the user, the same when a stand-in asks for it, and as administrator through the helper
/// (Mirador's Monado with the consent, or an approved build). Headless means Monado's null compositor and its simulated
/// headset: no headset, display or headset module is touched.
/// </summary>
public sealed class VerifyServices(AppHost host, Profile profile, Action<string> onLine) : IVerifyServices
{
    /// <summary>The variables that make Monado headless.</summary>
    public static readonly IReadOnlyDictionary<string, string> Headless = new Dictionary<string, string>
    {
        ["XRT_COMPOSITOR_NULL"] = "1",
        ["SIMULATED_ENABLE"] = "1",
    };

    private static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(45);

    public async Task<IAsyncDisposable> StartHeadlessAsync(MonadoInstall install, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(host.InheritedEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Headless)
        {
            env[name] = value;
        }

        var pipeName = host.Pipe.NameFor(env);
        if (host.Pipe.Exists(pipeName))
        {
            throw new MiradorException("Another Monado is running (its pipe is open): close it, then verify again.");
        }

        var logPath = NewLog("verify-monado");
        var log = new StreamWriter(logPath) { AutoFlush = true };
        IRunningProcess process;
        try
        {
            process = host.Launcher.Start(new ProcessSpec(install.ServiceExe, "", Path.GetDirectoryName(install.ServiceExe)!, env));
        }
        catch
        {
            await log.DisposeAsync();
            throw;
        }

        var tail = new Queue<string>();
        process.Line += line =>
        {
            lock (tail)
            {
                try
                {
                    log.WriteLine(line);
                }
                catch (ObjectDisposedException)
                {
                    // Monado's last lines, after Verify stopped it.
                }

                tail.Enqueue(line);
                if (tail.Count > 5)
                {
                    tail.Dequeue();
                }
            }
        };
        var running = new Running(process, log);
        try
        {
            await WaitForPipeAsync(process, pipeName, ct);
            onLine($"Monado is up (its log: {logPath}).");
            return running;
        }
        catch (MiradorException e)
        {
            await running.DisposeAsync();
            string last;
            lock (tail)
            {
                last = string.Join(" | ", tail);
            }

            throw new MiradorException(last.Length == 0 ? e.Message : $"{e.Message} Last lines: {last}");
        }
        catch
        {
            await running.DisposeAsync();
            throw;
        }
    }

    public StandInWait ExpectStandIn(MonadoInstall install)
    {
        var wait = new StandInWait(install, ct => StartHeadlessAsync(install, ct));
        host.ExpectStandIn(wait);
        return wait;
    }

    /// <summary>
    /// A new log for a verify run, <c>logs\&lt;prefix&gt;-yyyyMMdd-HHmmss.log</c>: not named <c>monado-*</c>, so the
    /// session's own last 10 logs stay; the last 10 of these are kept too.
    /// </summary>
    private string NewLog(string prefix)
    {
        Directory.CreateDirectory(host.Paths.LogsDir);
        foreach (var old in Directory.GetFiles(host.Paths.LogsDir, prefix + "-*.log").OrderDescending(StringComparer.Ordinal).Skip(9))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return Path.Combine(host.Paths.LogsDir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public async Task<ElevatedVerify> StartElevatedHeadlessAsync(MonadoInstall install, CancellationToken ct)
    {
        if (!host.IsInstalledCopy(askHelper: true))
        {
            return new ElevatedVerify(null, "Only an installed Mirador starts Monado as administrator, so this was skipped.");
        }

        string build;
        if (profile.Monado.Kind is SourceKind.Branch or SourceKind.Folder)
        {
            if (!profile.RunAsAdministrator || !host.ApprovedBuildCurrent(profile))
            {
                return new ElevatedVerify(null, "Turn on Run as administrator and approve this build to check it as administrator.");
            }

            build = profile.Id;
        }
        else
        {
            if (!host.ElevatedMonadoAllowed())
            {
                return new ElevatedVerify(null, "Allow Monado as administrator in App settings to check it as administrator.");
            }

            build = "";
        }

        var settings = host.MonadoSettings.Load();
        var start = new ElevatedStart(
            settings.LighthouseWaitMs, settings.RenderScalePercent, settings.CompositorMinTimeMs,
            ExitOnDisconnect: false, Module: "", Build: build, Headless: true);
        var env = new Dictionary<string, string>(host.InheritedEnvironment, StringComparer.OrdinalIgnoreCase);
        var pipeName = host.Pipe.NameFor(env);
        if (host.Pipe.Exists(pipeName))
        {
            return new ElevatedVerify(null, "Another Monado is running (its pipe is open), so this was skipped.");
        }

        var logPath = NewLog("verify-monado-elevated");
        IRunningProcess process;
        try
        {
            process = await Task.Run(() => new HelperProcessLauncher(host.Helper, start, logPath, host.Launcher)
                .Start(new ProcessSpec(install.ServiceExe, "", Path.GetDirectoryName(install.ServiceExe)!, env)), ct);
        }
        catch (MiradorException e)
        {
            return new ElevatedVerify(null, "Mirador's helper didn't start Monado as administrator: " + e.Message);
        }

        var running = new Running(process, null);
        try
        {
            await WaitForPipeAsync(process, pipeName, ct);
            onLine("Monado is up as administrator.");
            return new ElevatedVerify(running, null);
        }
        catch (MiradorException e)
        {
            await running.DisposeAsync();
            return new ElevatedVerify(null, "Monado as administrator didn't come up: " + e.Message);
        }
        catch
        {
            await running.DisposeAsync();
            throw;
        }
    }

    private async Task WaitForPipeAsync(IRunningProcess process, string pipeName, CancellationToken ct)
    {
        var until = DateTime.UtcNow + StartLimit;
        while (!host.Pipe.Exists(pipeName))
        {
            if (process.Exited.IsCompleted)
            {
                throw new MiradorException($"Monado stopped while starting (exit code {(await process.Exited).ToString(CultureInfo.InvariantCulture)}).");
            }

            if (DateTime.UtcNow > until)
            {
                throw new MiradorException($"Monado didn't come up within {(int)StartLimit.TotalSeconds} s.");
            }

            await Task.WhenAny(process.Exited, Task.Delay(200, ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class Running(IRunningProcess process, StreamWriter? log) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            process.Kill();
            await Task.WhenAny(process.Exited, Task.Delay(TimeSpan.FromSeconds(5)));
            await process.DisposeAsync();
            if (log is not null)
            {
                await log.DisposeAsync();
            }
        }
    }
}
