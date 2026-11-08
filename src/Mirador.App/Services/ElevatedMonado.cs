using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Mirador.Core.Elevation;
using Mirador.Ipc;

namespace Mirador.App.Services;

/// <summary>
/// The user's consent to run Monado as administrator (Mirador.md section 4.10 item 44): giving it takes one UAC prompt,
/// taking it back goes through the helper without one.
/// </summary>
public static class ElevatedMonado
{
    /// <summary>Whether the current user has consented (read from the registry; any user can read it).</summary>
    public static bool IsAllowed() =>
        AllowElevatedCommand.CurrentUser() is { } user && new ElevatedConsent(new RegistryConsentValues()).IsAllowed(user);

    /// <summary>
    /// Runs <c>Mirador.exe --allow-elevated-monado</c> through the UAC prompt, like <see cref="ElevatedRuntime.RunAsync"/>.
    /// The step records the consent and, with it, SteamVR's Lighthouse DLLs that Valve didn't sign as they are now
    /// (Settings' switch, and Home's "Allow again" after a SteamVR update). Returns the elevated step's exit code (0 when
    /// consent is recorded), <see cref="ElevatedRuntime.PromptCancelled"/> when the user said no to the prompt, or -1
    /// when it couldn't be started.
    /// </summary>
    public static async Task<int> AllowAsync()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Mirador.exe"))
        {
            Arguments = AllowElevatedCommand.Flag,
            Verb = "runas",
            UseShellExecute = true,
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return -1;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ElevatedRuntime.PromptCancelled)
        {
            return ElevatedRuntime.PromptCancelled;
        }
        catch (Win32Exception e)
        {
            AppLog.Write("The elevated consent step couldn't be started", e);
            return -1;
        }
    }

    /// <summary>
    /// Whether this is an account with no administrator token at all: not an administrator, and Windows has no
    /// elevated token to offer it. An administrator in Admin Approval Mode has a linked (limited) token and is not one.
    /// </summary>
    public static bool IsStandardAccount()
    {
        const int TokenElevationType = 18;
        const int Default = 1;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                return false;
            }

            return GetTokenInformation(identity.Token, TokenElevationType, out var type, sizeof(int), out _) && type == Default;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
}
