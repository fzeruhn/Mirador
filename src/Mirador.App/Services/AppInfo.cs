using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mirador.Core.Profiles;

namespace Mirador.App.Services;

/// <summary>Versions, for Diagnostics, Settings' About and the update check.</summary>
public static class AppInfo
{
    /// <summary>Mirador's version, such as <c>0.1.0</c>.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Windows' name, version and build, such as <c>Windows 11 Pro 25H2 (build 26200.7171)</c>.</summary>
    public static string Windows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var build = key?.GetValue("CurrentBuild") as string ?? "";

            // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
            if (int.TryParse(build, out var number) && number >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            }

            var display = key?.GetValue("DisplayVersion") as string;
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            return $"{product}{(display is null ? "" : " " + display)} (build {build}{ubr})";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    /// <summary>Which Monado the profile runs: the bundled one's <c>VERSION.txt</c>, or the build folder.</summary>
    public static string Monado(Profile profile, string appDir)
    {
        if (profile.Monado.Kind == SourceKind.Folder)
        {
            return $"build folder {profile.Monado.Folder}";
        }

        try
        {
            return "bundled " + File.ReadAllText(Path.Combine(appDir, "monado", "VERSION.txt")).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "bundled (no VERSION.txt: a development build without a bundled Monado)";
        }
    }

    /// <summary>The lines of versions.txt and the Diagnostics page.</summary>
    public static string Versions(Profile profile, string appDir) =>
        $"Mirador {Version}\n{Windows()}\nMonado: {Monado(profile, appDir)}\nProfile: {profile.Name}\n";
}
