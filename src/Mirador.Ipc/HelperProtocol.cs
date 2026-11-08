using System.Text.Json.Serialization;

namespace Mirador.Ipc;

/// <summary>What the helper service can be asked to do. The list is the whole interface: a request carries one of these
/// and, for <see cref="HelperCommand.StartMonadoElevated"/> only, the typed, range-checked values of
/// <see cref="ElevatedStart"/> — nothing else. The helper never acts on a path, a command line or any other free data a
/// caller supplies (Mirador.md section 4.10 item 46).</summary>
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

    /// <summary>
    /// Takes back the consent to run Monado as administrator of the user who sent this request (Mirador.md section 4.10
    /// item 44). The user is the pipe's client, as Windows reports it: the request names no one, and there is no way to
    /// revoke another user's consent. Giving consent is not a command: only an administrator can, through UAC.
    /// </summary>
    RevokeElevatedMonado,

    /// <summary>
    /// Starts the bundled <c>monado-service.exe</c> as administrator in the caller's session, with the caller's linked
    /// (elevated) token, once the checks of <see cref="ElevatedConsent"/>, the caller's image, SteamVR's driver and the
    /// rest pass (Mirador.md section 4.10 item 46). The request carries an <see cref="ElevatedStart"/> and nothing more.
    /// The reply's <see cref="HelperReply.Value"/> is the service's process id.
    /// </summary>
    StartMonadoElevated,

    /// <summary>Ends the elevated Monado this caller started (its job object), or nothing when it isn't running.</summary>
    StopMonadoElevated,

    /// <summary>
    /// Asks whether the elevated Monado this caller started has ended. It answers at once (Mirador polls it, as the plan
    /// chose over a call that blocks for the whole session): <see cref="HelperReply.Value"/> is the exit code once it has
    /// ended, and null while it still runs.
    /// </summary>
    WaitMonadoElevated,
}

/// <summary>
/// The typed values of a <see cref="HelperCommand.StartMonadoElevated"/>: Mirador's shared Monado settings (with the
/// ranges of <see cref="MonadoSettingsEnvironment"/>) and the headset module's name. The helper range-checks the
/// settings and adds the module's own fixed variables from its table; the request carries no path and no free
/// environment (Mirador.md section 4.10 item 46).
/// </summary>
/// <param name="LighthouseWaitMs">Lighthouse discovery wait; <see cref="MonadoSettingsEnvironment.LighthouseWaitRange"/>.</param>
/// <param name="RenderScalePercent">Compositor render scale; <see cref="MonadoSettingsEnvironment.RenderScaleRange"/>.</param>
/// <param name="CompositorMinTimeMs">Compositor minimum frame time; <see cref="MonadoSettingsEnvironment.CompositorMinTimeRange"/>.</param>
/// <param name="ExitOnDisconnect">Whether Monado exits when the last app disconnects.</param>
/// <param name="Module">The headset module's name: <c>""</c> for none, or <c>"dreamair"</c>.</param>
public sealed record ElevatedStart(
    [property: JsonRequired] int LighthouseWaitMs,
    [property: JsonRequired] int RenderScalePercent,
    [property: JsonRequired] int CompositorMinTimeMs,
    [property: JsonRequired] bool ExitOnDisconnect,
    [property: JsonRequired] string Module);

/// <summary>A request: a command, and the typed <see cref="Start"/> only with <see cref="HelperCommand.StartMonadoElevated"/>.</summary>
public sealed record HelperRequest([property: JsonRequired] HelperCommand Command, ElevatedStart? Start = null);

/// <summary>The helper's answer. <see cref="Error"/> is a message meant for the user when <see cref="Ok"/> is false.</summary>
/// <param name="Ok">Whether the command was carried out.</param>
/// <param name="Error">Why it was not, in plain words.</param>
/// <param name="WasRunning">For <see cref="HelperCommand.StopPimax"/>: whether Pimax's service was running.</param>
/// <param name="Value">For <see cref="HelperCommand.Ping"/> the version, for <see cref="HelperCommand.GetPimaxEyeBlob"/> the
/// variant (or null), for <see cref="HelperCommand.StartMonadoElevated"/> the service's process id, for
/// <see cref="HelperCommand.WaitMonadoElevated"/> the exit code once Monado has ended (null while it runs).</param>
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
