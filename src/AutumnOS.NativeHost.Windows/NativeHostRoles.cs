using System.IO.Pipes;

namespace AutumnOS.NativeHost.Windows;

internal static class NativeHostRoles
{
    internal static async Task<int> RunStationHostAsync(string manifestPath, string manifestSha256)
    {
        using var manifest = NativeHostLaunchManifest.LoadPinnedForRole(manifestPath, manifestSha256);
        // Until an actual STA role exists there is no reason to create an LSA
        // context, USER object, profile or browser, even when invoked directly.
        if (!NativeBrowserRole.IsInstalled) return NativeBrowserRole.UnsupportedExitCode;
        string self = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidDataException("NATIVE_APPHOST_REQUIRED"));
        if (!self.Equals(Path.Combine(manifest.HostDirectory, manifest.HostExecutable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NATIVE_RUNNING_FIXED_HOST_IMAGE_REQUIRED");
        using var parent = SessionNative.OpenProcess(0x101000, false, manifest.ParentProcessId);
        using var current = SessionNative.Current();
        if (parent.IsInvalid || SessionNative.Exited(parent) || SessionNative.GetProcessId(parent) != manifest.ParentProcessId)
            throw new InvalidDataException("NATIVE_ROLE_ORIGINAL_PARENT_HANDLE_REQUIRED");
        SessionIdentity parentIdentity = SessionNative.Identity(parent), currentIdentity = SessionNative.Identity(current);
        if (!Native.GetProcessTimes(parent.DangerousGetHandle(), out var parentCreation, out _, out _, out _) || parentCreation.Value != manifest.ParentCreationFileTime ||
            !Path.GetFullPath(SessionNative.Image(parent)).Equals(manifest.ParentExecutable, StringComparison.OrdinalIgnoreCase) ||
            parentIdentity.elevated || parentIdentity.appcontainer || parentIdentity.restricted || !parentIdentity.owner_is_user || parentIdentity.integrity_rid != 8192 ||
            !SessionNative.TokenAdmission(parentIdentity, currentIdentity))
            throw new InvalidDataException("NATIVE_ROLE_ORIGINAL_PARENT_REQUIRED");
        using var pipe = new NamedPipeClientStream(".", NativeHostBootstrapProtocol.PipeName(manifest.Nonce), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connection = new CancellationTokenSource(TimeSpan.FromSeconds(25))) await pipe.ConnectAsync(connection.Token).ConfigureAwait(false);
        if (SessionNative.Exited(parent) || !SessionNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer != manifest.ParentProcessId)
            throw new InvalidDataException("NATIVE_ROLE_ORIGINAL_KERNEL_SERVER_REQUIRED");
        var job = new JobEvidence();
        try
        {
            job.Create();
            var auditRoots = manifest.ProcessAuditRoots;
            job.Observe(new ProcessAudit(auditRoots.Host, auditRoots.Runtime, null));
            if (!job.Empty || job.limit_flags != OwnedJobPolicy.LimitFlags) throw new InvalidDataException("NATIVE_FRESH_RESOURCE_LIMITED_JOB_REQUIRED");
            Native.Check(Native.GetProcessTimes(current.DangerousGetHandle(), out var creation, out _, out _, out _), "native_role_original_creation");
            await NativeHostBootstrapProtocol.WriteAsync(pipe, manifest, new(NativeBootstrapKind.JobReady,
                (uint)Environment.ProcessId, creation.Value, unchecked((ulong)job.Handle.ToInt64()))).ConfigureAwait(false);
            // Keep the original Job and process alive until the authenticated
            // parent confirms it duplicated and queried this exact Job. Sending
            // a handle value and exiting first would lose the original object.
            NativeBootstrapFact bound = await NativeHostBootstrapProtocol.ReadAsync(pipe, manifest,
                (uint)Environment.ProcessId, creation.Value).ConfigureAwait(false);
            if (bound.Kind != NativeBootstrapKind.QueryJobBound || SessionNative.Exited(parent))
                throw new InvalidDataException("NATIVE_PARENT_QUERY_JOB_ACK_REQUIRED");
            await RunRestrictedGroupAsync(pipe, manifest, parent, creation.Value, job).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            // The original job remains owned through restricted child retirement.
            if (job.created) job.CloseAfterEmpty();
        }
    }
    private static async Task RunRestrictedGroupAsync(NamedPipeClientStream pipe, NativeHostLaunchManifest manifest,
        Microsoft.Win32.SafeHandles.SafeProcessHandle parent, ulong creationTime, JobEvidence job)
    {
        var station = new OwnedStation(manifest.Nonce);
        RestrictedBrowserChild? child = null;
        using var stop = new CancellationTokenSource();
        uint stationPid = (uint)Environment.ProcessId;
        Task Write(NativeBootstrapKind kind, ulong operand = 0) => NativeHostBootstrapProtocol.WriteAsync(pipe, manifest,
            new(kind, stationPid, creationTime, operand));
        async Task Expect(NativeBootstrapKind kind)
        {
            NativeBootstrapFact fact = await NativeHostBootstrapProtocol.ReadAsync(pipe, manifest, stationPid, creationTime).ConfigureAwait(false);
            if (fact.Kind != kind || SessionNative.Exited(parent)) throw new InvalidDataException("NATIVE_EXPECTED_PARENT_HANDSHAKE");
        }
        async Task WatchOwnerAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(200, stop.Token).ConfigureAwait(false);
                    if (SessionNative.Exited(parent) || !PeekNamedPipe(pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                        break;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception) { /* Fail closed and signal the original child. */ }
            if (stop.IsCancellationRequested) return;
            stop.Cancel();
            pipe.Dispose();
            Volatile.Read(ref child)?.SignalInputEof();
        }
        Task watch = WatchOwnerAsync();
        try
        {
            RuntimeHostStaging.RequireSealed(manifest);
            stop.Token.ThrowIfCancellationRequested();
            station.CreateStation();
            await Write(NativeBootstrapKind.ContextCreated).ConfigureAwait(false);
            await NativeHostContextFacts.WriteNameAsync(pipe, station.station_name).ConfigureAwait(false);
            await Expect(NativeBootstrapKind.ContextBound).ConfigureAwait(false);
            station.CreateDesktopAndProfile();
            await Write(NativeBootstrapKind.ProfileCreated).ConfigureAwait(false);
            await Expect(NativeBootstrapKind.ProfileBound).ConfigureAwait(false);
            string udf = Path.Combine(station.ProfileFolder, "session-udf-" + manifest.Nonce);
            string temp = Path.Combine(station.ProfileFolder, "session-temp-" + manifest.Nonce);
            OwnedFiles.CreatePrivateDirectory(udf, station.package_sid, true);
            OwnedFiles.CreatePrivateDirectory(temp, station.package_sid, true);
            child = new(manifest, station, job, udf, temp);
            stop.Token.ThrowIfCancellationRequested();
            child.Start();
            child.RequireLive();
            await Write(NativeBootstrapKind.RestrictedRootCreated, child.RootPid).ConfigureAwait(false);
            await Expect(NativeBootstrapKind.RestrictedRootBound).ConfigureAwait(false);
            // The restricted reader cannot open parent paths or substitute a
            // descriptor. Only this inherited pipe receives pinned launch input.
            await NativeBrowserStartup.WriteAsync(child.Input, manifest).ConfigureAwait(false);
            NativeBootstrapFact ready = await NativeHostBootstrapProtocol.ReadAsync(child.Output, manifest, stationPid, creationTime).ConfigureAwait(false);
            if (ready.Kind != NativeBootstrapKind.BrowserControllerReady)
                throw new InvalidDataException("NATIVE_ACTUAL_BROWSER_CONTROLLER_REQUIRED");
            uint browserPid = checked((uint)ready.OriginalJobHandle);
            child.RequireLive(browserPid);
            await Write(ready.Kind, browserPid).ConfigureAwait(false);

            long sequence = 0;
            while (true)
            {
                NativeViewPacket request = await NativeViewProtocol.ReadAsync(pipe, stop.Token).ConfigureAwait(false);
                if (request.Sequence != checked(++sequence) || !NativeHostContextFacts.IsRequest(request.Kind))
                    throw new InvalidDataException("NATIVE_STATION_REQUEST_SEQUENCE_OR_KIND");
                station.VerifyRuntime(); child.RequireLive(browserPid);
                await NativeViewProtocol.WriteAsync(child.Input, request, stop.Token).ConfigureAwait(false);
                NativeViewPacket reply = await NativeViewProtocol.ReadAsync(child.Output, stop.Token).ConfigureAwait(false);
                NativeHostContextFacts.RequireReply(request.Kind, reply, sequence);
                if (request.Kind != NativeViewKind.Close) child.RequireLive(browserPid);
                await NativeViewProtocol.WriteAsync(pipe, reply, stop.Token).ConfigureAwait(false);
                if (request.Kind == NativeViewKind.Close || reply.Kind == NativeViewKind.Failed) break;
            }
        }
        finally
        {
            stop.Cancel();
            await watch.ConfigureAwait(false);
            if (child is not null) await child.DrainAsync().ConfigureAwait(false);
            // The original thread and job must already be empty before deleting
            // the exact new profile or releasing the owned USER objects.
            do
            {
                station.CleanupAfterGroupEmpty(job.Empty);
                if (!station.cleanup_proven) await Task.Delay(150).ConfigureAwait(false);
            }
            while (!station.cleanup_proven);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, IntPtr buffer, uint size, IntPtr read, IntPtr available, IntPtr left);
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // This return precedes platform checks, manifest IO, PInvoke, LSA,
        // profile creation, USER initialization or browser setup.
        if (args.Length == 0) return 0;
        try
        {
            if (args.Length == 3 && args[0] == "--station-host" && Path.IsPathFullyQualified(args[1]) && Hash(args[2]))
                return NativeHostRoles.RunStationHostAsync(args[1], args[2]).GetAwaiter().GetResult();
            // No generic executable, script, URL or arbitrary role arguments.
            if (args.Length == 9 && args[0] == "--browser-host") return NativeBrowserRole.Run(args);
            if (args.Length == 1 && args[0] == "--browser-host") return NativeBrowserRole.UnsupportedExitCode;
            return 64;
        }
        catch { return 1; }
    }
    private static bool Hash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
