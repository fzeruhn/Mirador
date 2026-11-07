using System.Runtime.InteropServices;
using Mirador.Core.Session;

namespace Mirador.App.Services;

/// <summary>
/// Switches the base stations off when Windows shuts down, restarts or signs out while their off is still to come,
/// without waiting for Monado's session to end first (a headset's own end may take longer than Windows waits). Windows
/// asks every top-level window first (<c>WM_QUERYENDSESSION</c>), one hidden in the tray too: when an off will be needed,
/// Mirador gives Windows its reason (shown on Windows' "these apps are preventing" screen); it agrees either way and
/// starts nothing yet. When Windows then ends the session (<c>WM_ENDSESSION</c> TRUE), the session's off, or Mirador's
/// own as it quits, is queued at once and sent while Monado stops beside it (one off, not both); without an off, a
/// session that hasn't ended is stopped all the same, with no reason given. The window waits at most
/// <see cref="Limit"/>; the work runs off the window's thread. A shutdown something else cancelled
/// (<c>WM_ENDSESSION</c> FALSE) changes nothing.
/// </summary>
internal sealed class EndSession
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint WmEndSession = 0x0016;
    private const string Reason = "Switching off the base stations";

    /// <summary>The longest Windows' ending waits for the base stations and Monado.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly AppHost host;

    // Windows calls this for as long as the window lives: the field keeps the delegate from being collected.
    private readonly SubclassProc proc;

    // On the window's thread.
    private bool offNeeded;
    private bool blocking;

    /// <param name="window">The main window's handle.</param>
    /// <param name="host">Mirador's parts: its session and base stations.</param>
    public EndSession(IntPtr window, AppHost host)
    {
        this.host = host;
        proc = OnMessage;

        // Should it fail, Windows ends Mirador without the off: not worth stopping Mirador for.
        if (!SetWindowSubclass(window, proc, 3, 0))
        {
            AppLog.Write("Mirador can't tell when Windows shuts down or signs out, so it doesn't switch the base stations off then");
        }
    }

    private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WmQueryEndSession)
        {
            offNeeded = OffNeeded();
            if (offNeeded && !blocking)
            {
                blocking = ShutdownBlockReasonCreate(window, Reason);
                if (!blocking)
                {
                    AppLog.Write($"Windows is ending the session: Mirador couldn't give Windows its reason (error {Marshal.GetLastPInvokeError()})");
                }
            }

            return 1;
        }

        if (message == WmEndSession)
        {
            // TRUE: the session ends now. FALSE: something else cancelled it, and nothing was started.
            if (wParam != 0 && (offNeeded || OffNeeded()))
            {
                Finish();
            }
            else if (wParam != 0 && host.Session.State != SessionState.Stopped)
            {
                // No off to send, but Monado still runs: the headset's steps are undone now, not at the next launch.
                AppLog.Write("Windows is ending the session: Mirador stops Monado");
                Wait(Task.Run(StopAsync));
            }

            offNeeded = false;
            if (blocking)
            {
                ShutdownBlockReasonDestroy(window);
                blocking = false;
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    /// <summary>
    /// The base stations' off is still to come: the one after a session is waiting or being sent, a session that hasn't
    /// ended switches them off when it does (its profile has base stations, and "Power off when Monado stops"), or
    /// Mirador switches them off as it quits ("Power off when Mirador quits", with a station selected).
    /// </summary>
    private bool OffNeeded() =>
        host.Stations.OffPending
        || host.Stations.OffAtQuitWanted
        || (host.Session.CurrentProfile is { } running && host.Stations.OffFollows(running.Profile, running.Module));

    /// <summary>
    /// Queues the off at once, the session's (for a session that hasn't ended) or else Mirador's own as it quits, and
    /// waits for it to be sent, with Monado's stop beside it, at most <see cref="Limit"/> in all.
    /// </summary>
    private void Finish()
    {
        AppLog.Write("Windows is ending the session: Mirador switches off the base stations and stops Monado");
        try
        {
            // Before the stop: the session's end, which may come at once, then finds its off, or Mirador's own, on its way.
            if (host.Session.CurrentProfile is { } running)
            {
                host.Stations.EndingNow(running.Profile, running.Module);
            }

            host.Stations.QuittingNow();
            var off = Task.Run(() => host.Stations.FlushAsync(Limit));
            var stop = Task.Run(StopAsync);
            Wait(Task.WhenAll(off, stop));
        }
        catch (Exception e)
        {
            AppLog.Write("Windows is ending the session: finishing failed", e);
        }
    }

    /// <summary>Waits for the work at most <see cref="Limit"/>, on the window's thread; logs when it stops waiting or the work fails.</summary>
    private static void Wait(Task work)
    {
        try
        {
            if (!work.Wait(Limit))
            {
                AppLog.Write($"Windows is ending the session: Mirador stopped waiting after {Limit.TotalSeconds:0} s");
            }
        }
        catch (AggregateException e)
        {
            AppLog.Write("Windows is ending the session: finishing failed", e);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await host.Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.Write("Stopping Monado as Windows ends the session failed", e);
        }
    }

    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
