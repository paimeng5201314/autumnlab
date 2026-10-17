// Standalone inbox Framework tests; this fixture identity is never used by packaging.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace AutumnOS.SingleFile
{
    internal static class BundleIdentity
    {
        internal const string PayloadSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal const string BuildId = "preparation-test-only";
    }
}

internal static class PreparationTests
{
    private static readonly Type ProgramType = typeof(AutumnOS.SingleFile.Program);
    private static int passed;
    private static object Call(string name, params object[] args)
    {
        try { return ProgramType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        passed++;
        Console.WriteLine("PASS " + name);
    }
    private static void Rejected(Action action, string code)
    {
        try { action(); }
        catch (IOException error) { Check(error.Message == code, code); return; }
        throw new InvalidOperationException("Expected rejection: " + code);
    }
    private static Control Find(Form window, string name) { return window.Controls.Find(name, true)[0]; }
    private static void RunPreparation(Form window, Action work)
    {
        try { window.GetType().GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { work }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static void ReportProgress(Form window, int stage, long current, long maximum)
    {
        try { window.GetType().GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { stage, current, maximum }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static void CheckCancellation(Form window)
    {
        try { window.GetType().GetMethod("Pump", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static void Capture(Form window, string directory, string name)
    {
        using (var bitmap = new Bitmap(window.Width, window.Height))
        {
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            bitmap.Save(Path.Combine(directory, name), ImageFormat.Png);
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string report = Path.GetFullPath(args[0]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Control.CheckForIllegalCrossThreadCalls = true;
            var bytes = new byte[262195];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
            long observed = 0; int callbacks = 0;
            Action<long> progress = delegate(long completed) { if (completed <= observed) throw new InvalidOperationException("Nonmonotonic measured progress"); observed = completed; callbacks++; };
            string actual;
            using (var input = new MemoryStream(bytes)) actual = (string)Call("HashStream", input, progress);
            using (var sha = SHA256.Create()) Check(actual == BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(), "chunked_hash_matches_framework_sha256");
            Check(observed == bytes.Length && callbacks == 3, "progress_reports_real_bytes_monotonically");
            using (var input = new MemoryStream()) Check((string)Call("HashStream", input, null) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "empty_file_hash_is_preserved");

            Type windowType = ProgramType.GetNestedType("PreparationWindow", BindingFlags.NonPublic);
            using (var window = (Form)Activator.CreateInstance(windowType, true))
            {
                window.Show();
                var showProgress = windowType.GetMethod("Report", BindingFlags.Instance | BindingFlags.NonPublic);
                showProgress.Invoke(window, new object[] { 0, 123L * 1048576, 246L * 1048576 });
                Check(window.ClientSize.Width >= 600 && window.ClientSize.Height >= 480, "native_preparation_uses_application_sized_window");
                Check(Find(window, "PreparationProgress").AccessibilityObject.Value == "50%", "accessible_percentage_is_actual_stage_ratio");
                Check(Find(window, "PreparationStep1").Text.Contains("进行中") && !Find(window, "PreparationStep2").Text.Contains("完成"), "future_steps_are_not_claimed_complete");
                Capture(window, report, "01-native-preparation-fixture.png");
                showProgress.Invoke(window, new object[] { 1, 8L, 10L });
                Check(Find(window, "PreparationStep1").Text.Contains("完成") && Find(window, "PreparationDetail").Text.Contains("8 / 10"), "file_count_and_completed_stage_are_reported");
                window.ClientSize = new Size(620, 480);
                Application.DoEvents();
                Check(Find(window, "PreparationDetail").Bottom < Find(window, "PreparationDataNotice").Top, "short_window_progress_does_not_overlap_footer");
                Check(Find(window, "PreparationProduct").Bottom <= Find(window, "PreparationStep1").Top, "short_window_hero_does_not_overlap_steps");
                Capture(window, report, "02-compact-native-preparation-fixture.png");
                using (var input = new MemoryStream(bytes))
                using (var output = new MemoryStream())
                {
                    Call("CopyResource", input, output, window);
                    Check(Convert.ToBase64String(output.ToArray()) == Convert.ToBase64String(bytes), "responsive_copy_preserves_exact_resource_bytes");
                }
                window.Close();
                bool cancelled = false;
                try { Call("HashStream", new MemoryStream(bytes), (Action<long>)delegate(long count) { windowType.GetMethod("Pump", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null).Invoke(window, null); }); }
                catch (TargetInvocationException error) { cancelled = error.InnerException is OperationCanceledException; }
                Check(cancelled, "closing_preparation_cancels_chunked_work");
            }

            // Block the real UI message loop temporarily, as a synchronous native
            // accessibility/capture call can do. Hashing/reporting must still finish.
            using (var window = (Form)Activator.CreateInstance(windowType, true))
            using (var workerReady = new ManualResetEvent(false))
            using (var uiWaiting = new ManualResetEvent(false))
            using (var workDone = new ManualResetEvent(false))
            using (var timer = new System.Windows.Forms.Timer())
            {
                int uiThread = Thread.CurrentThread.ManagedThreadId, workThread = 0;
                bool completedWithoutUi = false;
                string workerHash = null;
                timer.Interval = 15;
                timer.Tick += delegate
                {
                    if (!workerReady.WaitOne(0)) return;
                    timer.Stop();
                    uiWaiting.Set();
                    completedWithoutUi = workDone.WaitOne(2000);
                };
                window.Shown += delegate { timer.Start(); };
                RunPreparation(window, delegate
                {
                    workThread = Thread.CurrentThread.ManagedThreadId;
                    workerReady.Set();
                    if (!uiWaiting.WaitOne(3000)) throw new InvalidOperationException("Test UI timer never started");
                    using (var input = new MemoryStream(bytes)) workerHash = (string)Call("HashStream", input,
                        (Action<long>)delegate(long count) { ReportProgress(window, 0, count, bytes.Length); });
                    workDone.Set();
                });
                Check(workThread != uiThread, "preparation_runs_off_the_window_thread");
                Check(completedWithoutUi && workerHash == actual, "hash_and_measured_reporting_finish_while_ui_is_blocked");
                Check(window.IsDisposed, "normal_message_loop_closes_after_worker_completion");
            }

            using (var window = (Form)Activator.CreateInstance(windowType, true))
            using (var ready = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var mutex = new Mutex(true))
            using (var timer = new System.Windows.Forms.Timer())
            {
                bool closeDeferred = false, workerStopped = false, mainStillOwnsMutex = false, cancelled = false;
                string partial = Path.Combine(report, "cancelled-worker-retained.bin");
                timer.Interval = 15;
                timer.Tick += delegate
                {
                    if (!ready.WaitOne(0)) return;
                    timer.Stop();
                    window.Close();
                    closeDeferred = !window.IsDisposed && window.Visible;
                    release.Set();
                };
                window.Shown += delegate { timer.Start(); };
                try
                {
                    RunPreparation(window, delegate
                    {
                        bool acquired = mutex.WaitOne(0);
                        mainStillOwnsMutex = !acquired;
                        if (acquired) mutex.ReleaseMutex();
                        try
                        {
                            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                file.Write(bytes, 0, 32);
                                ready.Set();
                                if (!release.WaitOne(3000)) throw new InvalidOperationException("Test cancellation never arrived");
                                CheckCancellation(window);
                                file.Write(bytes, 32, 32);
                            }
                        }
                        finally { workerStopped = true; }
                    });
                }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && closeDeferred, "close_requests_cancel_and_waits_for_active_worker");
                Check(workerStopped && new FileInfo(partial).Length == 32, "cancel_returns_only_after_worker_stops_and_preserves_partial_file");
                using (var file = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(file.Length == 32, "cancelled_worker_releases_its_file_handle");
                mutex.ReleaseMutex();
                Check(mainStillOwnsMutex, "main_retains_extraction_mutex_until_worker_has_joined");
            }

            using (var window = (Form)Activator.CreateInstance(windowType, true))
            {
                var expected = new IOException("PORTABLE_TEST_WORKER_FAILURE");
                bool preserved = false;
                try { RunPreparation(window, delegate { throw expected; }); }
                catch (IOException error) { preserved = object.ReferenceEquals(error, expected); }
                Check(preserved && window.IsDisposed, "worker_failure_propagates_original_exception_after_ui_cleanup");
            }

            string system = Path.Combine(report, "binding-fixture");
            Directory.CreateDirectory(system);
            Check(Call("ReadEntryBinding", system) == null, "missing_entry_binding_is_initial_seed");
            string bindingPath = Path.Combine(system, "Entry-aaaaaaaaaaaaaaaaaaaa.json");
            string binding = "{\"schemaVersion\":1,\"seedSha256\":\"" + AutumnOS.SingleFile.BundleIdentity.PayloadSha256 + "\",\"productDirectory\":\"Product-aaaaaaaaaaaaaaaaaaaa\",\"buildId\":\"updated-build\"}";
            File.WriteAllText(bindingPath, binding);
            object entry = Call("ReadEntryBinding", system);
            Check(entry != null, "valid_portable_reentry_binding_is_read");
            File.WriteAllText(Path.Combine(system, "autumn.install.json"), "{\"schemaVersion\":1,\"buildId\":\"old-build\"}");
            Rejected(delegate { Call("ValidateEntryBinding", system, entry); }, "PORTABLE_ENTRY_BINDING_INVALID");
            File.WriteAllText(bindingPath, "null");
            Rejected(delegate { Call("ReadEntryBinding", system); }, "PORTABLE_ENTRY_BINDING_INVALID");
            File.WriteAllText(bindingPath, binding.Replace("Product-aaaaaaaaaaaaaaaaaaaa", "Product-bbbbbbbbbbbbbbbbbbbb"));
            Rejected(delegate { Call("ReadEntryBinding", system); }, "PORTABLE_ENTRY_BINDING_INVALID");
            Console.WriteLine("TOTAL " + passed);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
