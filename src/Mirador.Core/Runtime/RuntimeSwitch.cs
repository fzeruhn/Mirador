using Mirador.Core.Monado;
using Mirador.Ipc;

namespace Mirador.Core.Runtime;

/// <summary>Makes a Monado install the active OpenXR runtime, through the helper service.</summary>
public static class RuntimeSwitch
{
    /// <summary>
    /// Sets <paramref name="install"/> as the active runtime, always through the helper service: Mirador's Monado (or an
    /// older install's bundled one), or the approved copy of a branch or build folder (Mirador.md §4.11 item 51). A build
    /// in a folder the user can write is never made the runtime itself: every OpenXR program, elevated ones and other
    /// users' included, loads it. Afterwards <paramref name="runtime"/> is read for Mirador's Monado: success means its
    /// manifest is active now.
    /// </summary>
    /// <param name="approvedProfileId">
    /// For a branch or build folder: the profile whose approved copy becomes the runtime (Mirador.md §4.11 item 51), or
    /// null when it has none. Without one, a build in a folder the user can write is never made the runtime.
    /// </param>
    /// <param name="hadApproval">Whether the profile has an approval of an earlier build: then this build changed since.</param>
    /// <returns>Null when the runtime was set, else what went wrong in plain words.</returns>
    public static async Task<string?> SetAsync(
        MonadoInstall install, IHelperClient helper, OpenXrRuntime runtime, string? approvedProfileId = null, bool hadApproval = false)
    {
        if (!install.IsBundled)
        {
            if (approvedProfileId is null)
            {
                return hadApproval
                    ? "This build changed since it was approved: approve it again on the Profiles page, then set it as the runtime."
                    : "Approve this build on the Profiles page first: every OpenXR program loads the runtime, so it can't be in a folder you can change.";
            }

            var approved = await helper.SendAsync(new HelperRequest(HelperCommand.SetRuntimeApproved, Build: approvedProfileId), CancellationToken.None).ConfigureAwait(false);
            return approved.Ok ? null : approved.Error ?? "Mirador's helper service couldn't set the OpenXR runtime, so the runtime is unchanged.";
        }

        var reply = await helper.SendAsync(HelperCommand.SetRuntimeBundled, CancellationToken.None).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return reply.Error ?? "Mirador's helper service couldn't set the OpenXR runtime, so the runtime is unchanged.";
        }

        return runtime.IsActive(install.Manifest)
            ? null
            : $"Mirador's helper service made another Monado the OpenXR runtime ({Describe(runtime.Active())}), not this Mirador's ({install.Manifest}): the helper belongs to another Mirador install. Reinstall Mirador, or pick a Monado build folder in the profile.";
    }

    private static string Describe(string? active) => active ?? "none";
}
