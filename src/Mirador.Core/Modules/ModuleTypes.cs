using Mirador.Ipc;

namespace Mirador.Core.Modules;

/// <summary>How a status line or a check result looks: fine, for information, worth a look, or broken.</summary>
public enum StatusLevel
{
    Ok,
    Info,
    Warning,
    Error,
}

/// <summary>One line of the status on Home, such as "Tracking: HMD LHR-2A2E5F48, 1 more device".</summary>
/// <param name="Key">Names the line, so that it can be found and replaced; never shown.</param>
/// <param name="Label">What the line is about, in a word or two.</param>
/// <param name="Level">How it looks.</param>
/// <param name="Text">What it says, in plain words.</param>
public sealed record StatusLine(string Key, string Label, StatusLevel Level, string Text);

/// <summary>The result of one check a headset module runs before a Start, with what to do about a problem.</summary>
/// <param name="Label">What was checked.</param>
/// <param name="Level">How it went.</param>
/// <param name="Text">What was found, in plain words.</param>
/// <param name="Fix">What the user can do about it; null when there is nothing to do.</param>
public sealed record CheckResult(string Label, StatusLevel Level, string Text, string? Fix = null);

/// <summary>Something the user should know while Monado runs, such as a program that started and gets in the way.</summary>
/// <param name="Key">Names the kind of warning, so that a repeat replaces the one shown; never shown.</param>
/// <param name="Text">What happened and what to do, in plain words.</param>
public sealed record SessionWarning(string Key, string Text);

/// <summary>Collects the files and texts that go into the diagnostics zip.</summary>
public interface IDiagnosticsSink
{
    /// <summary>Adds the file at <paramref name="path"/> under <paramref name="entryName"/>; a file that isn't there is noted, not an error.</summary>
    void AddFile(string entryName, string path);

    /// <summary>Adds <paramref name="text"/> as the entry <paramref name="entryName"/>.</summary>
    void AddText(string entryName, string text);
}

/// <summary>What a headset module may use: Mirador's files, the helper service, the running programs, the USB devices and the clock.</summary>
/// <param name="Paths">Where Mirador keeps its data.</param>
/// <param name="Helper">The helper service, for the steps that need an administrator.</param>
/// <param name="Processes">The programs that are running.</param>
/// <param name="Usb">The USB devices that are plugged in.</param>
/// <param name="Time">The clock to use for every wait, so that tests can move it.</param>
public sealed record ModuleContext(AppPaths Paths, IHelperClient Helper, IProcessList Processes, IUsbDevices Usb, TimeProvider Time);
