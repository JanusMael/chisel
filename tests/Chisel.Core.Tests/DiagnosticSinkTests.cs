using FluentAssertions;
using Bennewitz.Ninja.Chisel.Diagnostics;

namespace Bennewitz.Ninja.Chisel.Tests;

public sealed class DiagnosticSinkTests
{
    [Fact]
    public void Guard_OnSuccess_ReturnsTrue_RecordsNothing()
    {
        var sink = new DiagnosticSink();
        var ran = false;

        var ok = sink.Guard("Stage", "item", () => ran = true);

        ok.Should().BeTrue();
        ran.Should().BeTrue();
        sink.Items.Should().BeEmpty();
        sink.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Guard_OnException_ReturnsFalse_RecordsError_DoesNotThrow()
    {
        var sink = new DiagnosticSink();

        var ok = sink.Guard("Copy", "file.cs", () => throw new InvalidOperationException("boom"));

        ok.Should().BeFalse();
        sink.HasErrors.Should().BeTrue();
        var d = sink.Items.Should().ContainSingle().Subject;
        d.Severity.Should().Be(DiagnosticSeverity.Error);
        d.Stage.Should().Be("Copy");
        d.Item.Should().Be("file.cs");
        d.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task GuardAsync_PropagatesCancellation_WithoutRecording()
    {
        var sink = new DiagnosticSink();

        var act = () => sink.GuardAsync("Walk", "T", () => throw new OperationCanceledException());

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Items.Should().BeEmpty("cancellation is control flow, not a per-item failure");
    }

    [Fact]
    public async Task GuardAsync_PropagatesCancellation_WhenWrappedInAggregateException()
    {
        var sink = new DiagnosticSink();

        // Task.WhenAll surfaces faults as an AggregateException; if it wraps only cancellations,
        // that's cancellation and must propagate, not be swallowed as a per-item error.
        var act = () => sink.GuardAsync("Walk", "T", () =>
            throw new AggregateException(new OperationCanceledException(), new OperationCanceledException()));

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Items.Should().BeEmpty();
    }

    [Fact]
    public void Guard_RecordsError_WhenAggregateException_ContainsNonCancellation()
    {
        var sink = new DiagnosticSink();

        // A mixed AggregateException (cancellation + a real fault) is a genuine failure — record it.
        var ok = sink.Guard("Walk", "T", () =>
            throw new AggregateException(new OperationCanceledException(), new InvalidOperationException("boom")));

        ok.Should().BeFalse();
        sink.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Report_DedupsExactDuplicates()
    {
        var sink = new DiagnosticSink();

        sink.Warn("Stage", "same message", "same item");
        sink.Warn("Stage", "same message", "same item");
        sink.Warn("Stage", "different", "same item");

        sink.Items.Should().HaveCount(2);
    }

    [Fact]
    public void LiveCallback_IsInvoked_PerUniqueDiagnostic()
    {
        var streamed = new List<SliceDiagnostic>();
        var sink = new DiagnosticSink(streamed.Add);

        sink.Warn("S", "m1");
        sink.Error("S", "m2");
        sink.Warn("S", "m1"); // duplicate — should not re-stream

        streamed.Should().HaveCount(2);
        streamed.Select(d => d.Severity).Should().Equal(DiagnosticSeverity.Warning, DiagnosticSeverity.Error);
    }

    [Fact]
    public void MisbehavingCallback_DoesNotBreakReporting()
    {
        var sink = new DiagnosticSink(_ => throw new Exception("callback blew up"));

        var act = () => sink.Warn("S", "m");

        act.Should().NotThrow("a faulty reporter must never break the run");
        sink.Items.Should().ContainSingle();
    }
}
