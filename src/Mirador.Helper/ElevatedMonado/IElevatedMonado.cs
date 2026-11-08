using Mirador.Ipc;

namespace Mirador.Helper.ElevatedMonado;

/// <summary>
/// Starts, stops and reports on the elevated <c>monado-service</c> for a caller (Mirador.md section 4.10 item 46). The
/// seam the <see cref="HelperHandler"/> talks to, so the handler stays free of Win32; the real one is
/// <c>ElevatedMonadoRunner</c>, checked on the PC in Task 51.
/// </summary>
public interface IElevatedMonado
{
    /// <summary>Starts Monado for <paramref name="caller"/> with <paramref name="start"/>; the reply's value is the pid, or it says why not.</summary>
    HelperReply Start(ElevatedStart start, CallerInfo caller);

    /// <summary>Ends the elevated Monado this caller started, or does nothing when it isn't running.</summary>
    HelperReply Stop(CallerInfo caller);

    /// <summary>Whether this caller's Monado has ended: the reply's value is the exit code once it has, null while it runs.</summary>
    HelperReply Wait(CallerInfo caller);
}
