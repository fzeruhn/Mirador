using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Mirador.App.Services;
using Mirador.Core;
using Mirador.Core.Elevation;
using Mirador.Core.OpenVr;
using Mirador.Core.Runtime;
using Mirador.Core.SignIn;
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

        // The elevated step of "Run Monado as administrator": records this user's consent, and SteamVR's Lighthouse DLLs
        // that Valve didn't sign as they are now. No window, no app.
        if (parsed.AllowElevatedMonado)
        {
            AppLog.Open(AppPaths.Default);
            return AllowElevatedCommand.Run(
                new ElevatedConsent(new RegistryConsentValues()),
                AllowElevatedCommand.CurrentUser(),
                AllowElevatedCommand.UnsignedLighthouseFiles,
                AppLog.Write);
        }

        // The elevated step of "Run as administrator" for a branch or build folder (Mirador.md §4.11 item 51): copies the
        // build into the install folder, which only administrators can change, and records each file's hash. No window.
        if (parsed.ApproveBuild is { } approve)
        {
            AppLog.Open(AppPaths.Default);
            try
            {
                Mirador.Ipc.ApprovedBuilds.Approve(approve.ProfileId, approve.Commit, approve.Source, AppContext.BaseDirectory, new Mirador.Ipc.RegistryApprovedBuildStore());
                return 0;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                AppLog.Write("Approving the build failed", e);
                return 1;
            }
        }

        // The uninstaller's step, run as the user who uninstalls (elevated, like the uninstaller): no window, no app.
        if (parsed.RestoreOpenVr)
        {
            try
            {
                var copy = AppPaths.Default.OpenCompositeDir;
                return RestoreOpenVrCommand.Run(new OpenVrPaths(OpenVrPaths.DefaultFile, copy), copy);
            }
            catch (Exception)
            {
                return 1; // the copy stays, so games still find their DLL; the uninstaller logs the exit code
            }
        }

        // The uninstaller's other step: Mirador no longer starts at sign-in. HKCU only, so no elevation needed.
        if (parsed.RemoveSignIn)
        {
            try
            {
                new SignInStartup(new RegistryRunKeys(), Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe")).TurnOff();
                return 0;
            }
            catch (Exception)
            {
                return 1; // the Run value stays, and Windows would start a Mirador that is gone; the uninstaller logs the exit code
            }
        }

        if (IsElevated())
        {
            MessageBox("Mirador doesn't run as administrator: Monado would then turn away every game that isn't elevated too. Start Mirador normally; it asks for administrator rights itself when it needs them.");
            return 1;
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
        if ((parsed.Background || parsed.SignIn) && !parsed.Start)
        {
            return 0; // a stand-in or the sign-in start raced another Mirador: it runs, and nothing should pop up over the game
        }

        // The first Mirador may then bring its window to the front (Windows lets only the foreground program do that).
        const uint AnyProcess = unchecked((uint)-1);
        AllowSetForegroundWindow(AnyProcess);
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

    /// <summary>Whether Mirador runs elevated (as administrator).</summary>
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>A plain message box, for what must be said before (or without) a window.</summary>
    internal static void MessageBox(string text)
    {
        const uint IconError = 0x10;
        _ = MessageBoxW(IntPtr.Zero, text, "Mirador", IconError);
    }

    /// <summary>What WinUI's own Main calls first: it ends the process with a message when Windows is too old.</summary>
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
