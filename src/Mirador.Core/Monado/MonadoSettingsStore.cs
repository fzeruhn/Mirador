using System.Text.Json.Serialization;

namespace Mirador.Core.Monado;

/// <summary>Keeps the <see cref="MonadoSettings"/> in one JSON file.</summary>
public sealed class MonadoSettingsStore(string path)
{
    /// <summary>
    /// The saved settings, clamped into range. A missing file gives the defaults; a file that cannot be read is kept
    /// as <c>.bad</c> next to it and the defaults are returned.
    /// </summary>
    public MonadoSettings Load() =>
        JsonFile.Load(path, MonadoSettingsJsonContext.Default.MonadoSettings)?.Clamped() ?? new MonadoSettings();

    public void Save(MonadoSettings settings) =>
        JsonFile.Save(path, settings, MonadoSettingsJsonContext.Default.MonadoSettings);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(MonadoSettings))]
internal sealed partial class MonadoSettingsJsonContext : JsonSerializerContext;
