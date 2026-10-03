using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using AutumnOS.Launcher;

namespace AutumnOS.Tests;

[SupportedOSPlatform("windows")]
internal static class LauncherTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("launcher.request_roundtrip_has_only_fixed_recall_message", RequestRoundtrip);
        yield return ("launcher.request_rejects_unknown_protocol_and_reserved_fields", InvalidRequestHeader);
        yield return ("launcher.request_rejects_empty_nonce_and_nonexact_lengths", InvalidRequestExtent);
        yield return ("launcher.response_roundtrips_all_defined_outcomes", ResponseRoundtrip);
        yield return ("launcher.response_rejects_wrong_nonce_pid_and_session", ResponseBinding);
        yield return ("launcher.response_rejects_malformed_header_outcome_and_extent", InvalidResponse);
        yield return ("launcher.scope_is_stable_user_session_bound_and_rejects_names", ScopeValidation);
        yield return ("launcher.forward_budget_must_be_positive_and_bounded", BudgetValidation);
        yield return ("launcher.secondary_recalls_ready_primary_without_owning_mutex", ReadyPrimary);
        yield return ("launcher.secondary_waits_for_primary_readiness", WaitForReadiness);
        yield return ("launcher.unready_primary_times_out_without_second_owner", UnreadyTimeout);
        yield return ("launcher.invalid_pipe_frames_cannot_invoke_recall", InvalidPipeFrames);
        yield return ("launcher.concurrent_secondary_burst_retains_single_owner", ConcurrentRecall);
        yield return ("launcher.released_owner_allows_clean_reentry", OwnerRelease);
        yield return ("launcher.unresponsive_handler_obeys_secondary_deadline", UnresponsiveHandler);
        yield return ("launcher.begin_stop_keeps_ownership_until_owner_dispose", BeginStopRetainsOwnership);
        yield return ("launcher.handler_fault_rejects_only_that_request_and_listener_recovers", HandlerFaultRecovery);
        yield return ("launcher.lost_ack_disconnects_recover_without_transport_spin", LostAcknowledgementRecovery);
        yield return ("launcher.buffered_ack_survives_delayed_client_read", DelayedAcknowledgementRead);
    }

    private static void RequestRoundtrip()
    {
        Guid nonce = Guid.NewGuid();
        byte[] request = LauncherProtocol.Request(nonce);
        Assert(request.Length == 24, "Requests must have one fixed extent with no arbitrary command payload.");
        Assert(LauncherProtocol.TryReadRequest(request, out var actual) && actual == nonce, "A valid recall request must retain its nonce.");
    }

    private static void InvalidRequestHeader()
    {
        foreach (var (offset, value) in new (int, byte)[] { (0, (byte)'X'), (4, 0), (4, 2), (5, 0), (5, 2), (6, 1), (7, 1) })
        {
            byte[] request = LauncherProtocol.Request(Guid.NewGuid()); request[offset] = value;
            Assert(!LauncherProtocol.TryReadRequest(request, out _), "Wrong magic/version/opcode/reserved fields must not authorize a recall.");
        }
    }

    private static void InvalidRequestExtent()
    {
        Assert(!LauncherProtocol.TryReadRequest(LauncherProtocol.Request(Guid.Empty), out _), "An empty nonce must be rejected.");
        byte[] valid = LauncherProtocol.Request(Guid.NewGuid());
        for (int length = 0; length < valid.Length; length++)
            Assert(!LauncherProtocol.TryReadRequest(valid.AsSpan(0, length), out _), "Every truncated request must be rejected safely.");
        Assert(!LauncherProtocol.TryReadRequest([.. valid, 0], out _), "One extra byte must be rejected.");
        Assert(!LauncherProtocol.TryReadRequest([.. valid, .. "launch C:/untrusted.exe"u8], out _), "A valid prefix cannot authorize an appended command or path.");
    }

    private static void ResponseRoundtrip()
    {
        Guid nonce = Guid.NewGuid();
        foreach (var outcome in Enum.GetValues<RecallOutcome>())
        {
            var expected = new RecallResult(outcome, 0x123456789);
            byte[] response = LauncherProtocol.Response(nonce, 123, 7, expected);
            Assert(response.Length == 40, "Replies must have a fixed extent.");
            Assert(LauncherProtocol.TryReadResponse(response, nonce, 123, 7, out var actual) && actual == expected,
                "A correlated reply must preserve the explicit outcome and window handle.");
        }
    }

    private static void ResponseBinding()
    {
        Guid nonce = Guid.NewGuid();
        byte[] response = LauncherProtocol.Response(nonce, 123, 7, new(RecallOutcome.Foreground, 99));
        Assert(!LauncherProtocol.TryReadResponse(response, Guid.NewGuid(), 123, 7, out _), "Another request's nonce must not settle this request.");
        Assert(!LauncherProtocol.TryReadResponse(response, nonce, 124, 7, out _), "Reply PID must match the actual pipe server.");
        Assert(!LauncherProtocol.TryReadResponse(response, nonce, 123, 8, out _), "Reply session must match the actual pipe server.");
        Assert(!LauncherProtocol.TryReadResponse(LauncherProtocol.Response(Guid.Empty, 123, 7, new(RecallOutcome.Foreground, 99)), Guid.Empty, 123, 7, out _),
            "An empty expected nonce cannot make an unbound response valid.");
    }

    private static void InvalidResponse()
    {
        Guid nonce = Guid.NewGuid();
        byte[] valid = LauncherProtocol.Response(nonce, 123, 7, new(RecallOutcome.Foreground, 99));
        foreach (var (offset, value) in new (int, byte)[] { (0, (byte)'X'), (4, 0), (4, 2), (5, 0), (5, 6), (5, 255), (6, 1), (7, 1) })
        {
            byte[] response = (byte[])valid.Clone(); response[offset] = value;
            Assert(!LauncherProtocol.TryReadResponse(response, nonce, 123, 7, out _), "Unknown or reserved reply fields must be rejected.");
        }
        for (int length = 0; length < valid.Length; length++)
            Assert(!LauncherProtocol.TryReadResponse(valid.AsSpan(0, length), nonce, 123, 7, out _), "Truncated replies must fail safely.");
        Assert(!LauncherProtocol.TryReadResponse([.. valid, 0], nonce, 123, 7, out _), "An overlong reply must not be accepted by prefix.");
    }

    private static void ScopeValidation()
    {
        string key = NewKey();
        string first = LauncherCoordinator.ScopeName(key);
        Assert(first == LauncherCoordinator.ScopeName(key), "The same user/session/key must have stable ownership scope.");
        Assert(first != LauncherCoordinator.ScopeName(NewKey()), "Unique test keys must remain isolated.");
        Assert(first.EndsWith(".s" + Process.GetCurrentProcess().SessionId, StringComparison.Ordinal), "Scope must include the current Windows session.");
        foreach (string invalid in new[] { "", new string('a', 101), "bad/key", "bad\\key", "bad key", "bad:key", "bad\nkey" })
            Throws<ArgumentException>(() => LauncherCoordinator.ScopeName(invalid));
    }

    private static void BudgetValidation()
    {
        foreach (TimeSpan invalid in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), LauncherCoordinator.ForwardBudget + TimeSpan.FromTicks(1) })
            Throws<ArgumentOutOfRangeException>(() => LauncherCoordinator.Enter(NewKey(), invalid));
    }

    private static void ReadyPrimary() => WithPrimary((key, primary) =>
    {
        int calls = 0, clientPid = 0;
        primary.SetReady((pid, _) => { Interlocked.Increment(ref calls); clientPid = pid; return Task.FromResult(new RecallResult(RecallOutcome.Foreground, 101)); });
        LauncherStart secondary = LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(3));
        Assert(secondary.Primary is null && secondary.ExitCode == 0 && secondary.Outcome == RecallOutcome.Foreground,
            "A secondary must acknowledge the existing owner without acquiring another primary.");
        Assert(calls == 1 && clientPid == Environment.ProcessId, "The actual pipe client PID must reach the bounded recall callback once.");
    });

    private static void WaitForReadiness() => WithPrimary((key, primary) =>
    {
        Task<LauncherStart> waiting = Task.Run(() => LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(3)));
        Thread.Sleep(120);
        Assert(!waiting.IsCompleted, "A primary that owns the mutex but has no handler cannot be treated as ready.");
        primary.SetReady((_, _) => Task.FromResult(new RecallResult(RecallOutcome.AttentionRequested, 102)));
        LauncherStart result = waiting.WaitAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();
        Assert(result.Primary is null && result.ExitCode == 0 && result.Outcome == RecallOutcome.AttentionRequested,
            "Readiness completion must recall the existing owner rather than create another window.");
    });

    private static void UnreadyTimeout() => WithPrimary((key, _) =>
    {
        var clock = Stopwatch.StartNew();
        LauncherStart result = LauncherCoordinator.Enter(key, TimeSpan.FromMilliseconds(300));
        Assert(result.Primary is null && result.ExitCode == 20 && result.Outcome == RecallOutcome.Timeout,
            "An unready owner must produce a finite timeout, never a second primary or false success.");
        Assert(clock.Elapsed < TimeSpan.FromSeconds(3), "A short caller budget must not wait for the server's larger deadline.");
    });

    private static void InvalidPipeFrames() => WithPrimary((key, primary) =>
    {
        int calls = 0;
        var observed = new ConcurrentQueue<string>();
        primary.Observed += item => observed.Enqueue(item.EventName);
        primary.SetReady((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new RecallResult(RecallOutcome.Foreground, 103)); });
        byte[] wrongOpcode = LauncherProtocol.Request(Guid.NewGuid()); wrongOpcode[5] = 2;
        byte[] wrongVersion = LauncherProtocol.Request(Guid.NewGuid()); wrongVersion[4] = 2;
        foreach (byte[] frame in new byte[][] { wrongOpcode, wrongVersion, LauncherProtocol.Request(Guid.Empty), [.. LauncherProtocol.Request(Guid.NewGuid()), .. "arbitrary-command"u8] })
            SendInvalidFrame(primary.PipeName, frame, expectDisconnect: true);
        // A truncated message followed by EOF must also release its listener, without a callback.
        for (int index = 0; index < 8; index++)
            SendInvalidFrame(primary.PipeName, [.. LauncherProtocol.Request(Guid.NewGuid()).AsSpan(0, 10)], expectDisconnect: false);
        Assert(SpinWait.SpinUntil(() => observed.Count(name => name == "request_rejected") >= 4, TimeSpan.FromSeconds(2)),
            "Malformed complete frames must be rejected by the real pipe receiver.");
        Assert(Volatile.Read(ref calls) == 0, "Malformed frames must not invoke application code.");
        LauncherStart legal = LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(3));
        Assert(legal.Primary is null && legal.ExitCode == 0 && Volatile.Read(ref calls) == 1,
            "Invalid clients must not poison later legitimate requests or exhaust the bounded listeners.");
    });

    private static void ConcurrentRecall() => WithPrimary((key, primary) =>
    {
        int calls = 0;
        primary.SetReady((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new RecallResult(RecallOutcome.AttentionRequested, 104)); });
        // Dedicated callers avoid testing thread-pool starvation from deliberately synchronous Enter.
        Task<LauncherStart>[] burst = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(
            () => LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(5)), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        LauncherStart[] results = Task.WhenAll(burst).WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        int dispatchedCalls = Volatile.Read(ref calls);
        string resultDetails = $"calls={dispatchedCalls}; " + string.Join("; ", results.Select((result, index) =>
            $"request[{index}]: primary={result.Primary is not null}, exit={result.ExitCode}, outcome={result.Outcome?.ToString() ?? "null"}"));
        Assert(results.All(result => result.Primary is null && result.ExitCode == 0 && result.Outcome == RecallOutcome.AttentionRequested),
            "A burst larger than the listener count must still recall one primary within each caller's budget. " + resultDetails);
        Assert(dispatchedCalls == 8, "Each valid burst request must dispatch once. " + resultDetails);
    });

    private static void OwnerRelease()
    {
        string? releasedKey = null;
        WithPrimary((key, _) => releasedKey = key);
        LauncherStart next = LauncherCoordinator.Enter(releasedKey!, TimeSpan.FromSeconds(3));
        Assert(next.Primary is not null && next.ExitCode == 0 && next.Outcome is null, "Owner disposal must release ownership for a fresh launch.");
        next.Primary!.Dispose(); // Enter and Dispose deliberately execute on this same test thread.
    }

    private static void UnresponsiveHandler() => WithPrimary((key, primary) =>
    {
        int calls = 0;
        primary.SetReady(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new RecallResult(RecallOutcome.Foreground, 105);
        });
        var clock = Stopwatch.StartNew();
        LauncherStart result = LauncherCoordinator.Enter(key, TimeSpan.FromMilliseconds(300));
        Assert(result.Primary is null && result.ExitCode == 20 && result.Outcome == RecallOutcome.Timeout && Volatile.Read(ref calls) == 1,
            "A connected but unresponsive handler must not report activation success or create a new primary.");
        Assert(clock.Elapsed < TimeSpan.FromSeconds(3), "Connected reads must obey the caller's total deadline.");
    });

    private static void BeginStopRetainsOwnership()
    {
        string? stoppedKey = null;
        WithPrimary((key, primary) =>
        {
            stoppedKey = key;
            int calls = 0, stoppingEvents = 0;
            primary.Observed += item => { if (item.EventName == "stopping") Interlocked.Increment(ref stoppingEvents); };
            primary.SetReady((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new RecallResult(RecallOutcome.Foreground, 106)); });
            primary.BeginStop();
            primary.BeginStop();
            Assert(primary.IsStopping && Volatile.Read(ref stoppingEvents) == 1, "Beginning shutdown must be idempotent.");
            var clock = Stopwatch.StartNew();
            LauncherStart blocked = LauncherCoordinator.Enter(key, TimeSpan.FromMilliseconds(300));
            using LauncherCoordinator? unexpectedPrimary = blocked.Primary;
            Assert(blocked.Primary is null && blocked.ExitCode == 20 && blocked.Outcome == RecallOutcome.Timeout,
                "Closing activation pipes must not release the owner mutex before window and application disposal.");
            Assert(Volatile.Read(ref calls) == 0 && clock.Elapsed < TimeSpan.FromSeconds(3),
                "A stopping primary cannot dispatch new recalls or block secondary launch indefinitely.");
        }); // WithPrimary releases/disposes on its dedicated mutex-owning thread before returning.
        LauncherStart next = LauncherCoordinator.Enter(stoppedKey!, TimeSpan.FromSeconds(3));
        using LauncherCoordinator? replacement = next.Primary;
        Assert(replacement is not null && next.ExitCode == 0 && next.Outcome is null,
            "Only owner-thread Dispose may permit a fresh primary after BeginStop.");
    }

    private static void HandlerFaultRecovery() => WithPrimary((key, primary) =>
    {
        int calls = 0;
        var events = new ConcurrentQueue<LauncherEvent>();
        primary.Observed += events.Enqueue;
        primary.SetReady((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1) throw new InvalidOperationException("Test-only synchronous callback failure.");
            if (call == 3) return Task.FromException<RecallResult>(new InvalidOperationException("Test-only asynchronous callback failure."));
            return Task.FromResult(new RecallResult(RecallOutcome.AttentionRequested, 107));
        });
        for (int index = 0; index < 8; index++)
        {
            LauncherStart reply = LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(3));
            using LauncherCoordinator? unexpectedPrimary = reply.Primary;
            bool failedHandler = index is 0 or 2;
            Assert(reply.Primary is null && reply.ExitCode == (failedHandler ? 21 : 0)
                && reply.Outcome == (failedHandler ? RecallOutcome.Rejected : RecallOutcome.AttentionRequested),
                "Each handler failure must produce a correlated rejection, and the following request must still reach the existing owner.");
        }
        Assert(Volatile.Read(ref calls) == 8, "Handler faults must not cause hidden retries or stop subsequent dispatch.");
        Assert(events.Count(item => item.EventName == "recall_handled" && item.Result.Outcome == RecallOutcome.Rejected) == 2
            && events.Count(item => item.EventName == "recall_handled" && item.Result.Outcome == RecallOutcome.AttentionRequested) == 6,
            "Only the two failing callbacks may be rejected; later legitimate callbacks must remain available.");
    });

    private static void LostAcknowledgementRecovery() => WithPrimary((key, primary) =>
    {
        int calls = 0, transportFailures = 0;
        var blockedReplies = new ConcurrentQueue<(TaskCompletionSource<bool> Started, TaskCompletionSource<bool> Release)>();
        primary.Observed += item =>
        {
            // Keep this diagnostic bounded even if the regression causes a hot error loop.
            if (item.EventName == "transport_unavailable") Interlocked.Increment(ref transportFailures);
        };
        primary.SetReady(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            if (blockedReplies.TryDequeue(out var reply))
            {
                reply.Started.TrySetResult(true);
                await reply.Release.Task.WaitAsync(token).ConfigureAwait(false);
            }
            return new RecallResult(RecallOutcome.AttentionRequested, 108);
        });

        // Eight broken response cycles exceed the four bounded listener instances.
        // Handshake barriers guarantee client closure occurs before response production.
        for (int index = 0; index < 8; index++)
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            blockedReplies.Enqueue((started, release));
            int failuresBefore = Volatile.Read(ref transportFailures);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            using (var client = new NamedPipeClientStream(".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
                client.ReadMode = PipeTransmissionMode.Message;
                client.WriteAsync(LauncherProtocol.Request(Guid.NewGuid()), timeout.Token).AsTask().GetAwaiter().GetResult();
                started.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            } // Close the real client handle while the server callback is still held.
            release.TrySetResult(true);
            Assert(SpinWait.SpinUntil(() => Volatile.Read(ref transportFailures) > failuresBefore, TimeSpan.FromSeconds(3)),
                "The server must observe a failed acknowledgement after the client closes before reply release.");

            LauncherStart next = LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(3));
            using LauncherCoordinator? unexpectedPrimary = next.Primary;
            Assert(next.Primary is null && next.ExitCode == 0 && next.Outcome == RecallOutcome.AttentionRequested,
                "After a lost acknowledgement the same owner must still handle the next legitimate recall.");
            Assert(Volatile.Read(ref transportFailures) <= (index + 1) * 2 + 4,
                "A broken pipe must not cause repeated WaitForConnection failures or an unbounded diagnostic loop.");
        }
        Assert(Volatile.Read(ref calls) == 16 && blockedReplies.IsEmpty,
            "Eight abandoned acknowledgements and eight following recalls must each dispatch exactly once.");
        Assert(Volatile.Read(ref transportFailures) is >= 8 and <= 20,
            "Transport failures must remain bounded by the deliberately disconnected requests.");
    });

    private static void DelayedAcknowledgementRead() => WithPrimary((key, primary) =>
    {
        int calls = 0;
        var handlerCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handledObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.Observed += item =>
        {
            if (item.EventName == "recall_handled" && item.Result.Outcome == RecallOutcome.AttentionRequested)
                handledObserved.TrySetResult(true);
        };
        primary.SetReady((_, _) =>
        {
            Interlocked.Increment(ref calls);
            handlerCalled.TrySetResult(true);
            return Task.FromResult(new RecallResult(RecallOutcome.AttentionRequested, 109));
        });
        Guid nonce = Guid.NewGuid();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        using (var client = new NamedPipeClientStream(".", primary.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            client.ReadMode = PipeTransmissionMode.Message;
            client.WriteAsync(LauncherProtocol.Request(nonce), timeout.Token).AsTask().GetAwaiter().GetResult();
            handlerCalled.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            handledObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            // Deliberately keep the real pipe connected without reading after dispatch.
            // Immediate server Disconnect would discard the unread buffered acknowledgement.
            Thread.Sleep(200);
            byte[] response = new byte[LauncherProtocol.ResponseSize];
            client.ReadExactlyAsync(response, timeout.Token).AsTask().GetAwaiter().GetResult();
            Assert(client.IsMessageComplete && LauncherProtocol.TryReadResponse(response, nonce, primary.ProcessId, primary.SessionId, out var result)
                && result == new RecallResult(RecallOutcome.AttentionRequested, 109),
                "A complete nonce/PID/session-bound acknowledgement must survive a delayed client read before client disposal.");
        }
        LauncherStart next = LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(3));
        using LauncherCoordinator? unexpectedPrimary = next.Primary;
        Assert(next.Primary is null && next.ExitCode == 0 && next.Outcome == RecallOutcome.AttentionRequested && Volatile.Read(ref calls) == 2,
            "After the client reads and closes, the same owner must handle the next recall without a duplicate dispatch.");
    });

    private static void SendInvalidFrame(string pipeName, byte[] frame, bool expectDisconnect)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
        pipe.ReadMode = PipeTransmissionMode.Message;
        pipe.WriteAsync(frame, timeout.Token).AsTask().GetAwaiter().GetResult();
        if (!expectDisconnect) return;
        try
        {
            byte[] response = new byte[1];
            Assert(pipe.ReadAsync(response, timeout.Token).AsTask().GetAwaiter().GetResult() == 0,
                "Malformed frames must not receive a successful acknowledgement.");
        }
        catch (IOException) { /* The server may disconnect a rejected message pipe immediately. */ }
    }

    private static void WithPrimary(Action<string, LauncherCoordinator> test)
    {
        string key = NewKey();
        using var release = new ManualResetEventSlim(false);
        var ready = new TaskCompletionSource<LauncherCoordinator>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? ownerError = null;
        var owner = new Thread(() =>
        {
            try
            {
                LauncherStart start = LauncherCoordinator.Enter(key, TimeSpan.FromSeconds(3));
                using LauncherCoordinator primary = start.Primary ?? throw new InvalidOperationException("The isolated test key must create a primary.");
                ready.TrySetResult(primary);
                release.Wait();
            }
            catch (Exception error) { ownerError = error; ready.TrySetException(error); }
        }) { IsBackground = true, Name = "AutumnOS isolated launcher test owner" };
        owner.Start();
        try { test(key, ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()); }
        finally
        {
            release.Set();
            Assert(owner.Join(TimeSpan.FromSeconds(5)), "The test owner must dispose on its acquiring thread and exit promptly.");
            if (ownerError is not null) ExceptionDispatchInfo.Capture(ownerError).Throw();
        }
    }

    private static string NewKey() => "AutumnOS.Launcher.Tests." + Guid.NewGuid().ToString("N");
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
