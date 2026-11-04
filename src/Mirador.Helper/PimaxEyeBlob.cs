using System.Text.RegularExpressions;

namespace Mirador.Helper;

/// <summary>
/// Reads which eye geometry file Pimax's eye tracking runtime was started with. Only the four known variants are
/// ever returned, so nothing from a command line (which any program can fake) travels any further.
/// </summary>
public static partial class PimaxEyeBlob
{
    private static readonly string[] Known = ["K10_RAWH", "K10_RAYH", "K11_RAWH", "K11_RAYH"];

    /// <summary>
    /// The variant in the first <c>config_XR5PIMAXDA_&lt;variant&gt;.bin</c> of <paramref name="commandLine"/> that is a
    /// known one, or null.
    /// </summary>
    public static string? Parse(string commandLine)
    {
        foreach (Match match in ConfigFile().Matches(commandLine))
        {
            var variant = match.Groups["variant"].Value;
            if (Array.IndexOf(Known, variant) >= 0)
            {
                return variant;
            }
        }

        return null;
    }

    [GeneratedRegex(@"config_XR5PIMAXDA_(?<variant>[A-Za-z0-9_]+)\.bin", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigFile();
}
