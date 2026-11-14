using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

internal static class NativeBrowserCloseSequenceTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.close.revoke_failure_still_completes_gate_and_independent_cleanup", RevokeFailure);
        yield return ("native.close.gate_failure_does_not_skip_drain_or_other_cleanup", GateFailure);
        yield return ("native.close.delivery_drain_failure_still_closes_controller_and_releases_original", DrainFailure);
        yield return ("native.close.controller_failure_preserves_first_failure_after_later_cleanup", ControllerFailure);
        yield return ("native.close.one_stream_failure_retains_it_and_does_not_skip_other_streams", StreamFailure);
        yield return ("native.close.sdk_observation_failure_still_releases_original_but_never_returns_clean", ObservationFailure);
        yield return ("native.close.release_failure_cannot_return_clean", ReleaseFailure);
        yield return ("native.close.normal_order_and_unclean_observation_are_preserved", NormalOrder);
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("NATIVE_CLOSE_SEQUENCE_ASSERTION"); }
    private static Exception Capture(Task<bool> task)
    { try { _ = task.GetAwaiter().GetResult(); } catch (Exception error) { return error; } throw new InvalidOperationException("FAILED_CLOSE_RETURNED_SUCCESS"); }
    private static Task<bool> Run(NativeBrowserCloseObservation facts, List<string> trace, List<Stream>? streams = null,
        Action? revoke = null, Action? gate = null, Func<Task>? drain = null, Action? controller = null, Func<Task<bool>>? observe = null, Action? release = null)
        => new BrowserCloseSequence(facts).RunAsync(
            () => { trace.Add("revoke"); revoke?.Invoke(); }, () => { trace.Add("gate"); gate?.Invoke(); },
            () => { trace.Add("drain"); return drain?.Invoke() ?? Task.CompletedTask; },
            () => { trace.Add("controller"); controller?.Invoke(); }, streams ?? [],
            () => { trace.Add("observe"); return observe?.Invoke() ?? Task.FromResult(true); },
            () => { trace.Add("release"); release?.Invoke(); });
    private static void CompleteTrace(List<string> trace) => Check(trace.SequenceEqual(["revoke", "gate", "drain", "controller", "observe", "release"]));
    private static void RevokeFailure()
    {
        var facts = new NativeBrowserCloseObservation(); var trace = new List<string>(); var first = new IOException("owned fake revoke failure");
        Check(ReferenceEquals(Capture(Run(facts, trace, revoke: () => throw first)), first)); CompleteTrace(trace);
        Check(facts.Step == NativeBrowserCloseStep.Revoke);
    }
    private static void DrainFailure()
    {
        var facts = new NativeBrowserCloseObservation(); var trace = new List<string>(); var first = new IOException("owned fake drain failure");
        Check(ReferenceEquals(Capture(Run(facts, trace, drain: () => Task.FromException(first))), first)); CompleteTrace(trace);
        Check(facts.Step == NativeBrowserCloseStep.DeliveryDrain);
    }
    private static void GateFailure()
    {
        var facts = new NativeBrowserCloseObservation(); var trace = new List<string>(); var first = new IOException("owned fake gate failure");
        Check(ReferenceEquals(Capture(Run(facts, trace, gate: () => throw first)), first)); CompleteTrace(trace);
        Check(facts.Step == NativeBrowserCloseStep.Revoke);
    }
    private static void ControllerFailure()
    {
        var facts = new NativeBrowserCloseObservation(); var trace = new List<string>(); var first = new IOException("owned fake controller failure");
        var stream = new TestStream(true); var streams = new List<Stream> { stream };
        Check(ReferenceEquals(Capture(Run(facts, trace, streams, controller: () => throw first,
            observe: () => Task.FromException<bool>(new InvalidOperationException("later query")), release: () => throw new InvalidOperationException("later release"))), first));
        CompleteTrace(trace); Check(stream.Calls == 1 && streams.Contains(stream) && facts.Step == NativeBrowserCloseStep.Controller);
        Check(NativeBrowserFailure.Sanitize(facts.Failure(first)).StartsWith("NBC1|controller|error|", StringComparison.Ordinal));
    }
    private static void StreamFailure()
    {
        var facts = new NativeBrowserCloseObservation(); var trace = new List<string>();
        var first = new TestStream(false); var failed = new TestStream(true); var last = new TestStream(false);
        var streams = new List<Stream> { first, failed, last };
        Check(Capture(Run(facts, trace, streams)) is IOException); CompleteTrace(trace);
        Check(first.Calls == 1 && failed.Calls == 1 && last.Calls == 1 && streams.Count == 1 && ReferenceEquals(streams[0], failed));
        Check(facts.Step == NativeBrowserCloseStep.Streams);
    }
    private static void ObservationFailure()
    {
        var facts = new NativeBrowserCloseObservation { OriginalBrowserSignaled = true, SdkQueryHresult = unchecked((int)0x80004005) };
        var trace = new List<string>(); var first = new System.Runtime.InteropServices.COMException("owned fake query failure", unchecked((int)0x80004005));
        Check(ReferenceEquals(Capture(Run(facts, trace, observe: () => Task.FromException<bool>(first))), first)); CompleteTrace(trace);
        Check(!facts.Clean && facts.Step == NativeBrowserCloseStep.Observe && NativeBrowserFailure.Sanitize(facts.Failure(first)).Contains("|exited|-|80004005|", StringComparison.Ordinal));
    }
    private static void ReleaseFailure()
    {
        var facts = new NativeBrowserCloseObservation(); var trace = new List<string>(); var first = new IOException("owned fake release failure");
        Check(ReferenceEquals(Capture(Run(facts, trace, release: () => throw first)), first)); CompleteTrace(trace);
        Check(facts.Step == NativeBrowserCloseStep.Release);
    }
    private static void NormalOrder()
    {
        foreach (bool clean in new[] { false, true })
        {
            var facts = new NativeBrowserCloseObservation(); var trace = new List<string>(); var stream = new TestStream(false); var streams = new List<Stream> { stream };
            Check(Run(facts, trace, streams, observe: () => Task.FromResult(clean)).GetAwaiter().GetResult() == clean);
            CompleteTrace(trace); Check(stream.Calls == 1 && streams.Count == 0 && facts.Step == NativeBrowserCloseStep.Observe);
        }
    }
    private sealed class TestStream(bool fail) : MemoryStream
    {
        internal int Calls { get; private set; }
        protected override void Dispose(bool disposing)
        { Calls++; if (fail) throw new IOException("owned fake stream failure"); base.Dispose(disposing); }
    }
}
