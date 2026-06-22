namespace Bennewitz.Ninja.Chisel.Diagnostics;

public enum DiagnosticSeverity
{
    /// <summary>A non-ideal condition that did not prevent collecting the affected item.</summary>
    Warning,

    /// <summary>An item (file, symbol, reference) could not be processed, but the run continued.</summary>
    Error,
}

/// <summary>
/// A structured, non-fatal diagnostic emitted while building a slice. Errors here mean "this one
/// item failed but the slice was still produced" — they never abort the run. Truly fatal
/// conditions (missing solution, unresolvable seed, unloadable workspace) are thrown instead.
/// </summary>
/// <param name="Severity">Warning or Error.</param>
/// <param name="Stage">The pipeline stage that produced it (e.g. "Walk", "Copy", "References").</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Item">The file path / symbol / reference the diagnostic is about, if any.</param>
public sealed record SliceDiagnostic(
    DiagnosticSeverity Severity,
    string Stage,
    string Message,
    string? Item = null)
{
    public string Format() =>
        Item is null
            ? $"[{Severity}] {Stage}: {Message}"
            : $"[{Severity}] {Stage}: {Message} — {Item}";
}
