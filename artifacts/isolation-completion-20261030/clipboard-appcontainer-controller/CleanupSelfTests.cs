using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.OwnedBrowserParent;

// Real in-process OS pipe/managed IO regression, not a no-native protocol test.
// No process, Job, profile, station, clipboard, UI or UAC operation is invoked.
internal static class CleanupSelfTests
{
    internal static int Run(string reportPath)
    {
        var results = new List<object>(); int passed = 0, failed = 0;
        foreach (bool capturePrimary in new[] { false, true })
        {
            string name = capturePrimary ? "real_primary_pipe_failure_survives_secondary_dispose_failure" : "broken_reader_dispose_flush_does_not_skip_owned_resource_actions";
            var journal = new CleanupJournal(); var teardown = new CleanupJournal();
            AnonymousPipeServerStream? transport = null; AnonymousPipeClientStream? reader = null;
            StreamWriter? writer = null; SafePipeHandle? readHandle = null, writeHandle = null;
            IOException? primaryFailure = null; uint? primaryHresult = null;
            var subsequentActions = new List<string>();
            bool sharedReadHandle = false, connectedControlPassed = false, readerActuallyClosed = false;
            try
            {
                transport = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
                // Reuse the SAME SafePipeHandle object in this process. Do not
                // stringify/duplicate the native handle, which would leave a
                // second reader or risk two owning SafeHandles for one value.
                readHandle = transport.ClientSafePipeHandle;
                reader = new AnonymousPipeClientStream(PipeDirection.In, readHandle);
                sharedReadHandle = ReferenceEquals(reader.SafePipeHandle, readHandle) && ReferenceEquals(transport.ClientSafePipeHandle, readHandle);
                Require(sharedReadHandle, "ONE_SHARED_SAFE_READ_HANDLE_REQUIRED");
                writeHandle = transport.SafePipeHandle;
                Require(!ReferenceEquals(readHandle, writeHandle), "DISTINCT_READ_AND_WRITE_KERNEL_ENDPOINTS_REQUIRED");
                transport.WriteByte(0x5A); connectedControlPassed = reader.ReadByte() == 0x5A;
                Require(connectedControlPassed, "REAL_PIPE_CONNECTED_BYTE_POSITIVE_CONTROL_FAILED");
                reader.Dispose(); readerActuallyClosed = readHandle.IsClosed;
                Require(readerActuallyClosed && !writeHandle.IsClosed, "READER_MUST_BE_REALLY_CLOSED_WITH_WRITER_STILL_OPEN");

                if (capturePrimary)
                {
                    // The primary failure is also an actual broken-pipe IO
                    // result, not a synthesized exception or test double.
                    try { transport.WriteByte(0x6B); }
                    catch (IOException error) { primaryFailure = error; primaryHresult = unchecked((uint)error.HResult); }
                    Require(primaryFailure is not null, "REAL_PRIMARY_PIPE_IO_EXCEPTION_REQUIRED");
                }
                IOException? retainedPrimary = primaryFailure;
                writer = new StreamWriter(transport, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = false };
                // Fewer than the writer's buffer capacity; no explicit Flush
                // before the production runner attempts Dispose.
                writer.Write("owned-pending-data-for-real-dispose-flush");
                journal.DisposeWriterAndTransport("fixture_stdin", writer, transport);
                journal.Attempt("sentinel.empty_job_followup_no_native", () => subsequentActions.Add("job"));
                journal.Attempt("sentinel.profile_followup_no_native", () => subsequentActions.Add("profile"));
                journal.Attempt("sentinel.station_followup_no_native", () => subsequentActions.Add("station"));

                CleanupAttempt? flush = journal.attempts.SingleOrDefault(a => a.stage == "fixture_stdin.writer_dispose_flush");
                CleanupAttempt? close = journal.attempts.SingleOrDefault(a => a.stage == "fixture_stdin.transport_dispose");
                Require(flush is { status: "failed", exception_type: "IOException", critical: false, hresult: not null }, "DISPOSE_MUST_OBSERVE_REAL_IO_EXCEPTION");
                Require(close is { status: "passed", critical: true } && writeHandle.IsClosed, "TRANSPORT_DISPOSE_MUST_CONTINUE_AND_CLOSE_ORIGINAL_ENDPOINT");
                Require(subsequentActions.SequenceEqual(new[] { "job", "profile", "station" }), "ALL_LATER_SENTINEL_ACTIONS_MUST_RUN_IN_ORDER");
                Require(journal.attempts.Count == 5 && journal.attempts.Skip(2).All(a => a.status == "passed"), "LATER_ACTION_LEDGER_MUST_RETAIN_SUCCESS");
                Require(journal.any_failure && !journal.critical_failure, "DISPOSE_FAILURE_LEDGER_MUST_REMAIN_VISIBLE_SEPARATELY_FROM_CLOSED_TRANSPORT");
                Require(ReferenceEquals(primaryFailure, retainedPrimary) && (!capturePrimary || unchecked((uint)primaryFailure!.HResult) == primaryHresult),
                    "CALLER_PRIMARY_FAILURE_MUST_NOT_BE_REPLACED_BY_CLEANUP_LEDGER");
                passed++;
                results.Add(new { name, status = "passed", shared_original_safe_read_handle = sharedReadHandle,
                    connected_kernel_pipe_positive_control = connectedControlPassed, reader_endpoint_actually_closed = readerActuallyClosed,
                    write_endpoint_closed_after_flush_failure = writeHandle.IsClosed,
                    primary_failure_was_real_io_exception = primaryFailure is not null, primary_hresult = primaryHresult,
                    caller_primary_field_retained = ReferenceEquals(primaryFailure, retainedPrimary),
                    subsequent_sentinel_actions = subsequentActions, cleanup_attempts = journal.attempts,
                    sentinel_scope = "Managed followup callbacks only; no Job/profile/station operations performed." });
            }
            catch (Exception error)
            {
                failed++; results.Add(new { name, status = "failed", exception_type = error.GetType().Name,
                    hresult = unchecked((uint)error.HResult), shared_original_safe_read_handle = sharedReadHandle,
                    connected_kernel_pipe_positive_control = connectedControlPassed, reader_endpoint_actually_closed = readerActuallyClosed,
                    primary_hresult = primaryHresult, subsequent_sentinel_actions = subsequentActions, cleanup_attempts = journal.attempts });
            }
            finally
            {
                // The expected production call already attempted writer.Dispose.
                // A leaveOpen writer owns no native endpoint; ensure the actual
                // reader/write handles close independently on assertion failure.
                if (reader is not null && readHandle?.IsClosed != true) teardown.Attempt("fixture_reader_teardown", reader.Dispose);
                if (writeHandle?.IsClosed != true) teardown.DisposeWriterAndTransport("fixture_teardown", writer, transport);
                if (readHandle is not null && !readHandle.IsClosed) teardown.Attempt("fixture_original_read_handle_teardown", readHandle.Dispose);
                if (writeHandle is not null && !writeHandle.IsClosed) teardown.Attempt("fixture_original_write_handle_teardown", writeHandle.Dispose);
                if (readHandle?.IsClosed == false || writeHandle?.IsClosed == false)
                {
                    failed++; results.Add(new { name = name + ".fixture_endpoint_cleanup", status = "failed", cleanup_attempts = teardown.attempts });
                }
            }
        }
        var report = new { schema_version = 1, producer = "派蒙", kind = "inprocess_real_kernel_anonymous_pipe_cleanup_regression",
            status = failed == 0 ? "passed" : "failed", total = passed + failed, passed, failed,
            io_scope = "Real System.IO.Pipes handles and actual IOException on broken-reader flush/dispose; no synthetic throw substitutes for that failure.",
            synthetic_exceptions_for_pipe_failure = false, process_launches = 0, gui_operations = "not_run", uac_requests = 0,
            profile_operations = "not_run", job_operations = "not_run", station_operations = "not_run", clipboard_operations = "not_run",
            primary_preservation_scope = "Actual pipe exception retained in the caller while the same production CleanupJournal records the secondary dispose failure separately; Main native finally is not executed.",
            mechanism_acceptance = "not_claimed", results };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_NEW_REPORT_REQUIRED");
            using FileStream file = new(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] bytes = Encoding.UTF8.GetBytes(json); file.Write(bytes); file.Flush(true);
            Console.WriteLine(json); return failed == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed_report_output", exception_type = error.GetType().Name,
                hresult = unchecked((uint)error.HResult), process_launches = 0, uac_requests = 0, clipboard_operations = "not_run" }));
            return 1;
        }
    }
    private static void Require(bool condition, string fixedCode)
    { if (!condition) throw new InvalidDataException(fixedCode); }
}
