namespace Mirador.DreamAir;

/// <summary>Which eye geometry to upload, and the state that remembers it.</summary>
/// <param name="Variant">The variant to use.</param>
/// <param name="NeedsOwner">Mirador doesn't know the headset's variant: it uses the tested one, and the owner should pick theirs.</param>
/// <param name="State">The state to keep (it remembers a variant read from Pimax).</param>
public sealed record BlobDecision(string Variant, bool NeedsOwner, DreamAirState State);

/// <summary>
/// Which eye geometry variant the headset needs (Mirador.md §4.5 item 14): the one Pimax's software was started with,
/// read while it runs, wins and is remembered; else the one remembered (read before, or chosen by the owner); else the
/// tested one, and the owner is asked.
/// </summary>
public static class BlobChoice
{
    public const string FromPimax = "pimax";
    public const string FromOwner = "owner";

    /// <param name="s">What Mirador remembers.</param>
    /// <param name="detected">The variant Pimax's eye runtime runs with now; null when it doesn't run or didn't say.</param>
    public static BlobDecision Resolve(DreamAirState s, string? detected)
    {
        if (detected is not null && EyeBlobVariants.All.Contains(detected))
        {
            return new BlobDecision(detected, false, s with { BlobVariant = detected, BlobSource = FromPimax });
        }

        if (s.BlobVariant is { } remembered && EyeBlobVariants.All.Contains(remembered))
        {
            return new BlobDecision(remembered, false, s);
        }

        return new BlobDecision(EyeBlobVariants.Tested, true, s);
    }

    /// <summary>
    /// After an upload that named the tracker: the first serial is stored; a different one means a different headset,
    /// whose variant may differ, so a remembered variant is cleared.
    /// </summary>
    public static DreamAirState AfterUpload(DreamAirState s, string? trackerSerial)
    {
        if (string.IsNullOrEmpty(trackerSerial) || s.BlobTrackerSerial == trackerSerial)
        {
            return s;
        }

        return s.BlobTrackerSerial is null
            ? s with { BlobTrackerSerial = trackerSerial }
            : s with { BlobTrackerSerial = trackerSerial, BlobVariant = null, BlobSource = null };
    }

    /// <summary>Whether <see cref="AfterUpload"/> cleared a remembered variant.</summary>
    public static bool Cleared(DreamAirState before, DreamAirState after) => before.BlobVariant is not null && after.BlobVariant is null;
}
