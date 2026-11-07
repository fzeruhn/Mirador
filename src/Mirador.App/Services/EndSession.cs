using System.Runtime.InteropServices;
using Mirador.Core.Session;

namespace Mirador.App.Services;

/// <summary>
/// Makes Windows wait, when it shuts down, restarts or signs out, while Mirador stops Monado and switches the base
/// stations off. Windows asks every top-level window first (<c>WM_QUERYENDSESSION</c>), one hidden in the tray too: when Monado runs
/// or the base stations' off is still to come, Mirador gives Windows its reason (shown on Windows' "these apps are
/// preventing" screen), starts that work and agrees. When Windows then ends the session (<c>WM_ENDSESSION</c>), the
/// window waits for the work at most <see cref="Limit"/>; the work itself runs off the window's thread.
/// </summary>
internal sealed class EndSession
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const string Reason = "Switching off the base stations";

    /// <summary>The longest Windows' ending waits for Monado and the base stations.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly AppHost host;

    // Windows calls this for as long as the window lives: the field keeps the delegate from being collected.
    private readonly SubclassProc proc;

    // On the window's thread.
    private Task? work;
    private bool blocking;

    /// <param name="window">The main window's handle.</param>
    /// <param name="host">Mirador's parts: its session and base stations.</param>
    public EndSession(IntPtr window, AppHost host)
    {
        this.host = host;
        proc = OnMessage;

        // Should it fail, Windows ends Mirador without the wait: not worth stopping Mirador for.
        SetWindowSubclass(window, proc, 3, 0);
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WmQueryEndSession && (work is { IsCompleted: false } || MustFinish()))
        {
            if (!blocking)
            {
                blocking = ShutdownBlockReasonCreate(window, Reason);
            }

            if (work is not { IsCompleted: false })
            {
                AppLog.Write("Windows is ending the session: Mirador stops Monado and switches off the base stations");
                work = Task.Run(FinishAsync);
            }

            return 1;
        }

        if (message == WmEndSession)
        {
            // TRUE: the session ends now. FALSE: something else cancelled it; the work goes on anyway.
            if (wParam != 0 && work is { } finishing)
            {
                Wait(finishing);
            }

            if (blocking)
            {
                ShutdownBlockReasonDestroy(window);
                blocking = false;
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    /// <summary>Monado runs (or is starting or stopping), or the base stations' off is still to come.</summary>
    private bool MustFinish() => host.Session.State != SessionState.Stopped || host.Stations.OffPending;

    /// <summary>As quitting does: the session's end schedules the off, and the flush sends it at once.</summary>
    private async Task FinishAsync()
    {
        try
        {
            await host.Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Write("Stopping Monado as Windows ends the session failed", e);
        }

        await host.Stations.FlushAsync(Limit).ConfigureAwait(false);
    }

    private static void Wait(Task finishing)
    {
        try
        {
            if (!finishing.Wait(Limit))
            {
                AppLog.Write($"Windows is ending the session: Mirador stopped waiting after {Limit.TotalSeconds:0} s");
            }
        }
        catch (AggregateException e)
        {
            AppLog.Write("Windows is ending the session: finishing failed", e);
        }
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
