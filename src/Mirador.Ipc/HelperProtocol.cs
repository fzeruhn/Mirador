using System.Text.Json.Serialization;

namespace Mirador.Ipc;

/// <summary>
/// What the helper service can be asked to do. The list is the whole interface: a request carries nothing but
/// one of these, so the helper never acts on a path, a command line or any other data a caller supplies.
/// </summary>
[JsonConverter(typeof(HelperCommandConverter))]
public enum HelperCommand
{
    /// <summary>Answers with the helper's version.</summary>
    Ping,

    /// <summary>
    /// Makes the Monado that came with Mirador the active OpenXR runtime, remembering the one it replaces. Putting that
    /// one back is not a command: only the uninstaller does it (<c>Mirador.Helper.exe --restore-runtime</c>), because
    /// the remembered runtime may be one in a folder any user can change.
    /// </summary>
    SetRuntimeBundled,

    /// <summary>
    /// Stops Pimax's service and ends its processes (also when the service would not stop; the reply then says why).
    /// </summary>
    StopPimax,

    /// <summary>Starts Pimax's service again unless it runs; one that is still stopping is given 20 s to stop first.</summary>
    StartPimax,

    /// <summary>Reads which eye geometry file Pimax's eye tracking runtime was started with.</summary>
    GetPimaxEyeBlob,
}

/// <summary>A request: one command and nothing else.</summary>
public sealed record HelperRequest([property: JsonRequired] HelperCommand Command);

/// <summary>The helper's answer. <see cref="Error"/> is a message meant for the user when <see cref="Ok"/> is false.</summary>
/// <param name="Ok">Whether the command was carried out.</param>
/// <param name="Error">Why it was not, in plain words.</param>
/// <param name="WasRunning">For <see cref="HelperCommand.StopPimax"/>: whether Pimax's service was running.</param>
/// <param name="Value">For <see cref="HelperCommand.Ping"/> the version, for <see cref="HelperCommand.GetPimaxEyeBlob"/> the variant (or null).</param>
public sealed record HelperReply(
    [property: JsonRequired] bool Ok,
    string? Error = null,
    bool? WasRunning = null,
    string? Value = null)
{
    /// <summary>
    /// Set by the client, never sent: the request never reached the helper (it isn't running, or the pipe couldn't be
    /// opened), so nothing was done. A reply that is merely missing (the helper took too long, or answered nonsense)
    /// leaves this false: the command may still have been carried out.
    /// </summary>
    [JsonIgnore]
    public bool NotDelivered { get; init; }
}

/// <summary>The command as its name, and nothing else: a number in a request is refused, not mapped to a command.</summary>
internal sealed class HelperCommandConverter : JsonStringEnumConverter<HelperCommand>
{
    public HelperCommandConverter() : base(namingPolicy: null, allowIntegerValues: false)
    {
    }
}

/// <summary>The wire format: camelCase names, empty fields left out.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperReply))]
internal sealed partial class HelperJsonContext : JsonSerializerContext
{
}
