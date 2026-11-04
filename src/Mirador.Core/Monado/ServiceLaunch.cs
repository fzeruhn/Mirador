using System.Text.RegularExpressions;
using Mirador.Core.Modules;

namespace Mirador.Core.Monado;

/// <summary>
/// Whether a Monado build starts its service when a game finds none running, which is what lets a game start Monado
/// through Mirador's stand-in. Monado's runtime DLL does it only when it was built with the CMake cache entry
/// <c>XRT_SERVICE_EXECUTABLE=monado-service</c>, which nothing in Monado sets by itself (checked 2026-11-04).
/// </summary>
public static partial class ServiceLaunch
{
    /// <summary>What the build must be configured with.</summary>
    public const string ConfigureOption = "-DXRT_SERVICE_EXECUTABLE=monado-service";

    /// <summary>
    /// For the build in <paramref name="buildFolder"/>: null when its runtime DLL starts the service, or when that can't
    /// be told (no readable <c>CMakeCache.txt</c>); else the "Games" status line that says games can't start it.
    /// </summary>
    public static StatusLine? Check(string buildFolder)
    {
        string[] lines;
        try
        {
            var cache = Path.Combine(buildFolder, "CMakeCache.txt");
            if (!File.Exists(cache))
            {
                return null;
            }

            lines = File.ReadAllLines(cache);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var value = lines.Select(line => CacheEntry().Match(line)).FirstOrDefault(m => m.Success)?.Groups["value"].Value.Trim();
        return string.Equals(value, "monado-service", StringComparison.OrdinalIgnoreCase)
            ? null
            : new StatusLine(
                "standin",
                "Games",
                StatusLevel.Warning,
                $"Games can't start Monado from this build: it was configured without {ConfigureOption}. Configure and build it again with it, or press Start before starting a game.");
    }

    [GeneratedRegex("^XRT_SERVICE_EXECUTABLE:[A-Z]+=(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheEntry();
}
