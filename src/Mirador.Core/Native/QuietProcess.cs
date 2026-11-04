using System.Diagnostics;

namespace Mirador.Core.Native;

/// <summary>Starts programs without Windows' error message boxes.</summary>
public static class QuietProcess
{
    /// <summary>
    /// <see cref="Process.Start(ProcessStartInfo)"/>, but a file that isn't a program fails at once with its error
    /// (a <see cref="System.ComponentModel.Win32Exception"/>) instead of showing "Unsupported 16-Bit Application" and
    /// waiting until someone clicks OK.
    /// </summary>
    public static Process? Start(ProcessStartInfo info) => Win32.WithoutErrorBoxes(() => Process.Start(info));
}
