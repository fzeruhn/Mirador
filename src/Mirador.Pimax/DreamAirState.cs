using System.Text.Json.Serialization;
using Mirador.Core;

namespace Mirador.Pimax;

/// <summary>What Mirador remembers about the Dream Air between runs, in <c>dreamair.json</c>.</summary>
/// <param name="LastGoodDriver">The NVIDIA driver (such as <c>621.01</c>) direct mode last worked with; a different one is checked first.</param>
/// <param name="BlobVariant">The eye geometry variant this headset needs, once known (Task 21).</param>
/// <param name="BlobSource">Where <paramref name="BlobVariant"/> came from: <c>pimax</c> or <c>owner</c>.</param>
/// <param name="BlobTrackerSerial">The eye tracker the variant belongs to.</param>
/// <param name="LastCheck">The last gaze check's result.</param>
public sealed record DreamAirState(
    string? LastGoodDriver = null,
    string? BlobVariant = null,
    string? BlobSource = null,
    string? BlobTrackerSerial = null,
    LastCheck? LastCheck = null);

/// <summary>A gaze check's result, kept to show on the Eye tracking page.</summary>
public sealed record LastCheck(DateTimeOffset When, double MedianDeg, double WorstDotDeg, int Dots);

/// <summary>Keeps the <see cref="DreamAirState"/> in one JSON file. Safe to use from several threads.</summary>
public sealed class DreamAirStateStore(string path)
{
    private readonly object gate = new();

    /// <summary>
    /// The saved state. A missing file gives an empty state; a file that can't be read is kept as <c>.bad</c> next to
    /// it and an empty state is returned. A variant Mirador doesn't know is dropped, so it never reaches a path.
    /// </summary>
    public DreamAirState Load()
    {
        lock (gate)
        {
            var state = JsonFile.Load(path, DreamAirJsonContext.Default.DreamAirState) ?? new DreamAirState();
            return state.BlobVariant is { } variant && !EyeBlobVariants.All.Contains(variant)
                ? state with { BlobVariant = null, BlobSource = null }
                : state;
        }
    }

    public void Save(DreamAirState state)
    {
        lock (gate)
        {
            JsonFile.Save(path, state, DreamAirJsonContext.Default.DreamAirState);
        }
    }

    /// <summary>Loads, changes and saves the state in one step, so that two changes at once don't undo each other.</summary>
    public DreamAirState Update(Func<DreamAirState, DreamAirState> change)
    {
        lock (gate)
        {
            var next = change(Load());
            Save(next);
            return next;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DreamAirState))]
internal sealed partial class DreamAirJsonContext : JsonSerializerContext;
