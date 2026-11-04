using Mirador.Core.Monado;

namespace Mirador.Core.Profiles;

/// <summary>What is wrong with a profile before it is saved, in words for the editor.</summary>
public static class ProfileValidation
{
    /// <summary>
    /// The profile's problems, empty when it can be saved: a name, a Monado build that can be found (a folder build is
    /// checked through <paramref name="locator"/>), extra variables with names that are set, have no <c>=</c> and appear
    /// once (ignoring case, as Windows does); then whatever <paramref name="moduleProblem"/> finds (the Dream Air's
    /// layer folder).
    /// </summary>
    public static IReadOnlyList<string> Problems(Profile p, MonadoLocator locator, Func<Profile, string?> moduleProblem)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Name))
        {
            problems.Add("A profile needs a name.");
        }

        if (p.Monado.Kind == SourceKind.Folder && p.Monado.Folder is { } folder && !string.IsNullOrWhiteSpace(folder)
            && !Path.IsPathFullyQualified(folder.Trim()))
        {
            problems.Add($"The Monado build folder \"{folder.Trim()}\" isn't a full path. Pick the folder again in the profile.");
        }
        else if (p.Monado.Kind == SourceKind.Folder)
        {
            try
            {
                locator.Locate(p.Monado);
            }
            catch (MiradorException e)
            {
                problems.Add(e.Message);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in p.ExtraEnvironment)
        {
            var name = variable.Name.Trim();
            if (name.Length == 0)
            {
                problems.Add("Every extra variable needs a name.");
            }
            else if (name.Contains('='))
            {
                problems.Add($"A variable's name can't contain \"=\": {name}.");
            }
            else if (!seen.Add(name))
            {
                problems.Add($"{name} is set twice. Keep one.");
            }
        }

        if (moduleProblem(p) is { } problem)
        {
            problems.Add(problem);
        }

        return problems.Distinct().ToList();
    }
}
