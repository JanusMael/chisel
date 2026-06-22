namespace Bennewitz.Ninja.Chisel;

public enum ProgressKind
{
    /// <summary>A coarse phase boundary (loading, analyzing, walking, …). Meant to be shown once.</summary>
    Phase,

    /// <summary>
    /// Fine-grained "what am I doing right now" (e.g. the type currently being walked). Emitted
    /// frequently; consumers should treat it as a status to surface on a timer (a heartbeat), not
    /// print every one.
    /// </summary>
    Activity,
}

/// <summary>A progress update from <see cref="SliceRunner"/>. See <see cref="ProgressKind"/>.</summary>
public sealed record SliceProgress(ProgressKind Kind, string Message);
