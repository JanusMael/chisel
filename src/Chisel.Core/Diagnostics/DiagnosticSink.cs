namespace Bennewitz.Ninja.Chisel.Diagnostics;

/// <summary>
/// Thread-safe collector for <see cref="SliceDiagnostic"/>s. Accumulates everything for an
/// end-of-run summary and optionally streams each diagnostic to a callback as it happens (so the
/// CLI can report problems live during execution). The <see cref="Guard"/> / <see cref="GuardAsync"/>
/// helpers run a per-item operation and turn any failure into a non-fatal error diagnostic —
/// except cancellation, which is always propagated.
/// </summary>
public sealed class DiagnosticSink
{
    private readonly object _gate = new();
    private readonly List<SliceDiagnostic> _items = new();
    private readonly HashSet<SliceDiagnostic> _seen = new();
    private readonly Action<SliceDiagnostic>? _onReport;

    public DiagnosticSink(Action<SliceDiagnostic>? onReport = null) => _onReport = onReport;

    public void Report(SliceDiagnostic diagnostic)
    {
        lock (_gate)
        {
            // Dedup exact duplicates (record value equality) — e.g. the same `dynamic` site or
            // the same per-type failure surfaced twice — so the summary stays readable.
            if (!_seen.Add(diagnostic))
            {
                return;
            }
            _items.Add(diagnostic);
        }

        // Invoke the live callback OUTSIDE the lock, and never let a misbehaving callback break
        // the run — reporting a problem must not itself become a problem.
        try
        {
            _onReport?.Invoke(diagnostic);
        }
        catch
        {
            // Intentionally swallowed.
        }
    }

    public void Warn(string stage, string message, string? item = null)
        => Report(new SliceDiagnostic(DiagnosticSeverity.Warning, stage, message, item));

    public void Error(string stage, string message, string? item = null)
        => Report(new SliceDiagnostic(DiagnosticSeverity.Error, stage, message, item));

    /// <summary>Runs <paramref name="body"/>; on failure records a non-fatal error and returns false.</summary>
    public bool Guard(string stage, string? item, Action body)
    {
        try
        {
            body();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsCancellationOnly(ex))
            {
                throw;
            }
            Error(stage, Describe(ex), item);
            return false;
        }
    }

    /// <summary>Async <see cref="Guard"/>: runs <paramref name="body"/>; on failure records a non-fatal error and returns false.</summary>
    public async Task<bool> GuardAsync(string stage, string? item, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsCancellationOnly(ex))
            {
                throw;
            }
            Error(stage, Describe(ex), item);
            return false;
        }
    }

    public IReadOnlyList<SliceDiagnostic> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }
    }

    public bool HasErrors
    {
        get
        {
            lock (_gate)
            {
                return _items.Any(i => i.Severity == DiagnosticSeverity.Error);
            }
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    // A Task that faults via Task.WhenAll / Parallel can surface an AggregateException; if it wraps
    // only cancellations, treat it as cancellation (propagate) rather than a per-item error.
    private static bool IsCancellationOnly(Exception ex)
        => ex is AggregateException ae
           && ae.Flatten().InnerExceptions.Count > 0
           && ae.Flatten().InnerExceptions.All(e => e is OperationCanceledException);
}
