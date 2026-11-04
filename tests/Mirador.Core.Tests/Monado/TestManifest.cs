using System.Text.Json;

namespace Mirador.Core.Tests.Monado;

/// <summary>The text of a Monado runtime manifest, the way Monado's build writes it.</summary>
internal static class TestManifest
{
    public static string Json(string libraryPath) =>
        """{"file_format_version": "1.0.0", "runtime": {"name": "Monado", "library_path": """
        + JsonSerializer.Serialize(libraryPath) + "}}";
}
