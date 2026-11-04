using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mirador.Core;
using Mirador.Core.Runtime;
using Mirador.Ipc;

namespace Mirador.App;

/// <summary>
/// Mirador's start: the elevated runtime switch first (it never opens a window), then one Mirador per user and
/// session: a second hands its request to the first over the app pipe and ends.
/// </summary>
public static partial class Program
{
    /// <summary>How long a second Mirador waits for the first to show its window, or to start Monado (a Start takes up to 45 s).</summary>
    private static readonly TimeSpan ShowWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(90);

    [STAThread]
    private static int Main(string[] args)
    {
        var parsed = AppArgs.Parse(args);
        if (parsed.SetRuntime is { } manifest)
        {
            return SetRuntimeCommand.Run(manifest, ActiveRuntimeWriter.Write);
        }

        SingleInstance? instance;
        try
        {
            instance = SingleInstance.TryAcquire();
        }
        catch (MiradorException e)
        {
            MessageBox(e.Message);
            return 1;
        }

        if (instance is null)
        {
            return AskTheFirst(parsed);
        }

        using (instance)
        {
            XamlCheckProcessRequirements();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callback =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App(parsed);
            });
        }

        return 0;
    }

    /// <summary>Hands a second start to the Mirador already running: start Monado, or show the window.</summary>
    private static int AskTheFirst(AppArgs parsed)
    {
        var request = parsed.Start ? AppPipe.Start : AppPipe.Show;
        var reply = AppPipeClient.SendAsync(AppPipe.CurrentName(), request, parsed.Start ? StartWait : ShowWait, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (reply is null)
        {
            if (!parsed.Background)
            {
                MessageBox("Mirador is already running but didn't answer. Close it from the tray (or in Task Manager), then start it again.");
            }

            return 1;
        }

        return reply == AppPipe.Ok ? 0 : 1;
    }

    private static void MessageBox(string text)
    {
        const uint IconError = 0x10;
        _ = MessageBoxW(IntPtr.Zero, text, "Mirador", IconError);
    }

    /// <summary>What WinUI's own Main calls first: it ends the process with a message when Windows is too old.</summary>
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
