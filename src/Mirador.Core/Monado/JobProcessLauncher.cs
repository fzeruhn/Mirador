using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mirador.Core.Native;

namespace Mirador.Core.Monado;

/// <summary>
/// Starts processes inside one Windows job object that ends all of them when it closes: when the launcher is
/// disposed, and also when Mirador itself is killed or crashes, so Monado never outlives Mirador.
/// </summary>
public sealed class JobProcessLauncher : IProcessLauncher, IDisposable
{
    private readonly object gate = new();
    private readonly SafeFileHandle job;
    private bool disposed;

    /// <exception cref="MiradorException">Windows would not create the job.</exception>
    public JobProcessLauncher()
    {
        try
        {
            job = Win32.CreateKillOnCloseJob();
        }
        catch (Win32Exception e)
        {
            throw new MiradorException(
                $"Couldn't set up the group that stops Monado when Mirador closes: {Reason(e)}. Restart Mirador; if this keeps happening, restart Windows.");
        }
    }

    /// <summary>
    /// Starts the program with the environment, working directory and command line of <paramref name="spec"/>, no
    /// window, and its output on <see cref="IRunningProcess.Line"/>.
    /// </summary>
    /// <exception cref="MiradorException">The program could not be started.</exception>
    /// <exception cref="ObjectDisposedException">The launcher has been disposed.</exception>
    public IRunningProcess Start(ProcessSpec spec)
    {
        // One lock for starting and disposing: the job handle must not close between Start and the assignment.
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var process = StartProcess(spec);
            try
            {
                var error = Win32.AssignToJob(job, process.SafeHandle);
                if (error != 0 && !process.HasExited) // a process that already ended needs no group
                {
                    throw new MiradorException(
                        $"Couldn't start {spec.FileName}: Windows wouldn't put it in the group that stops it when Mirador closes ({Reason(new Win32Exception(error))}).");
                }

                return new RunningProcess(process);
            }
            catch
            {
                Discard(process);
                throw;
            }
        }
    }

    /// <summary>Closes the job, which ends every process started through this launcher that is still running.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            job.Dispose();
        }
    }

    private static Process StartProcess(ProcessSpec spec)
    {
        if (!Directory.Exists(spec.WorkingDirectory))
        {
            throw new MiradorException($"Couldn't start {spec.FileName}: the folder {spec.WorkingDirectory} it should run in doesn't exist.");
        }

        var info = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
        {
            info.Environment[name] = value;
        }

        try
        {
            return Process.Start(info)
                ?? throw new MiradorException($"Couldn't start {spec.FileName}: Windows didn't start it.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            throw new MiradorException($"Couldn't start {spec.FileName}: {Reason(e, spec.FileName)}.");
        }
    }

    private static void Discard(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }

        process.Dispose();
    }

    /// <summary>
    /// The system's own wording of why, without the full stop the caller adds. For a failed start that is Windows' text
    /// for the error code, because .NET's message repeats the file and folder the user's text already names; a few of
    /// Windows' texts have a <c>%1</c> where the file name goes.
    /// </summary>
    private static string Reason(Exception e, string fileName = "") =>
        (e is Win32Exception win ? new Win32Exception(win.NativeErrorCode).Message : e.Message)
            .Replace("%1", fileName).TrimEnd().TrimEnd('.');
}
