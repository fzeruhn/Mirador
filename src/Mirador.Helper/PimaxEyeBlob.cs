using System.Text.RegularExpressions;
using Mirador.Ipc;

namespace Mirador.Helper;

/// <summary>
/// Reads which eye geometry file Pimax's eye tracking runtime was started with. Only the four known variants are
/// ever returned, so nothing from a command line (which any program can fake) travels any further.
/// </summary>
public static partial class PimaxEyeBlob
{
    /// <summary>
    /// The variant in the first <c>config_XR5PIMAXDA_&lt;variant&gt;.bin</c> of <paramref name="commandLine"/> that is a
    /// known one, or null.
    /// </summary>
    public static string? Parse(string commandLine)
    {
        foreach (Match match in ConfigFile().Matches(commandLine))
        {
            var variant = match.Groups["variant"].Value;
            if (PimaxNames.EyeBlobVariants.Contains(variant))
            {
                return variant;
            }
        }

        return null;
    }

    [GeneratedRegex(@"config_XR5PIMAXDA_(?<variant>[A-Za-z0-9_]+)\.bin", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigFile();
}
