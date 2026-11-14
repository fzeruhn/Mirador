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
    /// Makes Mirador's Monado (its current version; on an older install, the Monado that came with Mirador) the active
    /// OpenXR runtime, remembering the one it replaces. Putting that
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
    /// rest pass (Mirador.md section 4.10 item 46). The request carries an <see cref="ElevatedStart"/> and nothing more:
    /// the SteamVR files the caller allowed are read from the caller's own consent, never from a request. The reply's
    /// <see cref="HelperReply.Value"/> is the service's process id; a refusal because those files changed carries
    /// <see cref="HelperRefusal.SteamVrChanged"/>.
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

    /// <summary>
    /// Answers with the folder the helper starts Monado as administrator for: its own install folder, where the
    /// installed <c>Mirador.exe</c> is beside it (<see cref="HelperReply.Value"/>). Mirador compares its own
    /// path with it through <see cref="InstalledMirador.IsInstalledCopy"/>, the test the helper applies to the caller,
    /// so a copy the helper would refuse runs Monado as the user instead (Mirador.md section 4.10 item 46).
    /// </summary>
    GetInstallFolder,

    /// <summary>
    /// Installs the version of Mirador's Monado named by <see cref="HelperRequest.Version"/> and makes it current
    /// (Mirador.md §4.11 item 55). The helper fetches the list of Mirador's releases itself and takes the download and
    /// its SHA-256 only from there: the request carries the version's name and nothing else. The reply's
    /// <see cref="HelperReply.Value"/> is the version now current.
    /// </summary>
    InstallMonado,

    /// <summary>Makes the previous version of Mirador's Monado current again ("Roll back"); the reply's value is it.</summary>
    RollBackMonado,

    /// <summary>
    /// Makes the approved copy of the build of the profile named by <see cref="HelperRequest.Build"/> the active OpenXR
    /// runtime (Mirador.md §4.11 item 51), once every file is checked against the hashes recorded at the approval.
    /// </summary>
    SetRuntimeApproved,

    /// <summary>
    /// Answers with the profiles the caller has an approved build for (Mirador.md §4.11 item 51), as
    /// <see cref="HelperReply.Value"/>: their ids separated by commas (an id has none), empty for none. With
    /// <see cref="ForgetApprovedBuild"/>, Mirador takes back what no profile asks for any more: a request never carries
    /// a list, a reply may.
    /// </summary>
    ListApprovedBuilds,

    /// <summary>
    /// Takes back the caller's own approval of the build of the profile <see cref="HelperRequest.Build"/> names: its
    /// record, then its copy. Mirador sends it once a profile no longer runs that build as administrator (its switch
    /// off, its Monado changed, the profile deleted), so an approval never outlives the choice that made it. A copy that
    /// is the active OpenXR runtime hands that over to Mirador's Monado first. Taking a right away needs no prompt.
    /// </summary>
    ForgetApprovedBuild,
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
/// <param name="Module">The headset module's table: <c>""</c> for none, or <c>"pimax"</c> (a Dream Air plugged in).</param>
/// <param name="Build">
/// <c>""</c> for Mirador's Monado; else the id of the profile whose approved build runs (Mirador.md §4.11 item 51). The
/// helper finds that build in its own record and checks every file's hash; the request names no path.
/// </param>
/// <param name="Headless">
/// For "Verify build for Windows" (Mirador.md §4.11 item 49): Monado's null compositor and its simulated headset
/// (<c>XRT_COMPOSITOR_NULL=1</c>, <c>SIMULATED_ENABLE=1</c>), so it opens sessions without touching a headset or a
/// display. Only with no module.
/// </param>
/// <param name="ElevationFix">
/// Whether the build has Monado's elevation fix, as Verify found (an app running as the user connected to it running as
/// administrator): it then admits its own user's apps by itself, and the helper leaves out
/// <c>IPC_RELAXED_CONNECTION_SECURITY</c>, which lets any signed-in user's apps connect, unless Windows' Administrator
/// protection is on (elevation then runs as another account, which the fix can't tell from the user).
/// </param>
public sealed record ElevatedStart(
    [property: JsonRequired] int LighthouseWaitMs,
    [property: JsonRequired] int RenderScalePercent,
    [property: JsonRequired] int CompositorMinTimeMs,
    [property: JsonRequired] bool ExitOnDisconnect,
    [property: JsonRequired] string Module,
    string Build = "",
    bool Headless = false,
    bool ElevationFix = false);

/// <summary>
/// A request: a command, the typed <see cref="Start"/> only with <see cref="HelperCommand.StartMonadoElevated"/>, and
/// <see cref="Version"/> only with <see cref="HelperCommand.InstallMonado"/> (a weekly build's name, checked by the helper), and
/// <see cref="Build"/> only with <see cref="HelperCommand.SetRuntimeApproved"/> (a profile's id, looked up by the helper), and
/// <see cref="Clean"/> only with <see cref="HelperCommand.StopMonadoElevated"/> (Monado is asked to end by itself first).
/// </summary>
public sealed record HelperRequest(
    [property: JsonRequired] HelperCommand Command, ElevatedStart? Start = null, string? Version = null, string? Build = null, bool? Clean = null);

/// <summary>
/// Why the helper refused, for the refusals Mirador answers in its own way rather than only showing
/// <see cref="HelperReply.Error"/>. Sent as its name; a number is refused.
/// </summary>
[JsonConverter(typeof(HelperRefusalConverter))]
public enum HelperRefusal
{
    /// <summary>
    /// <see cref="HelperCommand.StartMonadoElevated"/>: a DLL in SteamVR's Lighthouse driver folder is neither signed by
    /// Valve nor the copy this user allowed (its SHA-256 isn't the one recorded at the consent), as after a SteamVR
    /// update. Mirador then runs the same Start as the user and offers to allow it again (Mirador.md section 4.10 item
    /// 46). <see cref="HelperReply.Error"/> names the files.
    /// </summary>
    SteamVrChanged,
}

/// <summary>The helper's answer. <see cref="Error"/> is a message meant for the user when <see cref="Ok"/> is false.</summary>
/// <param name="Ok">Whether the command was carried out.</param>
/// <param name="Error">Why it was not, in plain words.</param>
/// <param name="WasRunning">For <see cref="HelperCommand.StopPimax"/>: whether Pimax's service was running.</param>
/// <param name="Value">For <see cref="HelperCommand.Ping"/> the version, for <see cref="HelperCommand.GetPimaxEyeBlob"/> the
/// variant (or null), for <see cref="HelperCommand.StartMonadoElevated"/> the service's process id, for
/// <see cref="HelperCommand.WaitMonadoElevated"/> the exit code once Monado has ended (null while it runs), for
/// <see cref="HelperCommand.GetInstallFolder"/> the helper's install folder.</param>
/// <param name="Refusal">When <see cref="Ok"/> is false: the typed reason, for a refusal Mirador answers in its own way;
/// null for every other failure.</param>
public sealed record HelperReply(
    [property: JsonRequired] bool Ok,
    string? Error = null,
    bool? WasRunning = null,
    string? Value = null,
    HelperRefusal? Refusal = null)
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

/// <summary>The refusal as its name, and nothing else.</summary>
internal sealed class HelperRefusalConverter : JsonStringEnumConverter<HelperRefusal>
{
    public HelperRefusalConverter() : base(namingPolicy: null, allowIntegerValues: false)
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
