using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AutumnOS.NativeHost.Windows;

public enum NativeProcessOwnerState { NotStarted, Starting, Authenticated, Unknown, Closing, Unsupported, Retired }

public sealed record NativeProcessOwnerSnapshot(NativeProcessOwnerState State, uint OriginalProcessId,
    ulong OriginalCreationFileTime, bool CreationAttempted, bool OriginalProcessExited,
    bool OriginalThreadExited, bool OriginalQueryJobBound, bool OriginalJobEmpty,
    bool AllObservedOriginalProcessHandlesSignaled, bool ResourcesRetired, string? FailureCode,
    string BrowserRoleReadiness, bool OwnedJobRecoveryRequested = false, bool OriginalStationRecoveryRequested = false);

/// <summary>
/// Trusted host composition only. Publish this owner before StartAsync. Startup
/// failure does not discard a created original process or release its lifetime.
/// No child/wire report can complete OriginalGroupRetired.
/// </summary>
public sealed class NativeProcessOwner
{
    private readonly object sync = new();
    private readonly NativeHostLaunchManifest manifest;
    private readonly NetOnlyLaunch launch = new();
    private readonly NativeCreationOwnership creation = new();
    private readonly TaskCompletionSource retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NamedPipeServerStream? pipe;
    private SessionJobWitness? witness;
    private Task? startup, observation;
    private NativeProcessOwnerState state;
    private string? failureCode;
    private bool closeRequested, resourcesRetired, pinsReleased;
    private bool queryJobBound, originalJobEmpty, allObservedSignaled;
    private readonly object peerSync = new();
    private readonly SemaphoreSlim exchange = new(1, 1);
    private int queuedExchangeCount;
    private long nextSequence;
    private bool ownedContextStartPermitIssued, retainingBootstrapForFacts;
    private NativeHostContextFacts? ownedContext;
    private HeldProcess? restrictedRoot, boundBrowser;
    private bool transportPoisoned;
    private readonly OwnedShutdownBudget shutdownBudget = new();
    private readonly System.Diagnostics.Stopwatch lifetimeClock = System.Diagnostics.Stopwatch.StartNew();
    private const string RoleReadiness = "restricted_controller_requires_original_peer_and_document_admission";

    private NativeProcessOwner(NativeHostLaunchManifest manifest) => this.manifest = manifest;

    public static NativeProcessOwner Create(string manifestPath, NativeHostBinding expectedBinding)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("NATIVE_HOST_WINDOWS_X64_REQUIRED");
        return new(NativeHostLaunchManifest.LoadPinned(manifestPath, expectedBinding));
    }

    public NativeHostBinding Binding => manifest.Binding;
    public string Nonce => manifest.Nonce;
    public Task OriginalGroupRetired => retired.Task;
    public Task? OwnedStartupTask { get { lock (sync) return startup; } }
    public Task? OwnedRetirementObservationTask { get { lock (sync) return observation; } }
    public bool ResourcesRetired { get { lock (sync) return resourcesRetired; } }

    public NativeProcessOwnerSnapshot Snapshot
    {
        get
        {
            lock (sync) return new(state, launch.original_pid, launch.original_creation_filetime,
                launch.create_process_with_logon_calls != 0, launch.original_process_signaled,
                launch.original_thread_signaled, queryJobBound,
                originalJobEmpty, allObservedSignaled, resourcesRetired,
                failureCode, RoleReadiness, witness?.termination_requested == true, launch.termination_requested);
        }
    }

    public Task StartAsync(CancellationToken beforeLaunchCancellation = default)
    {
        lock (sync)
        {
            if (startup is not null) return startup;
            if (closeRequested || resourcesRetired) throw new InvalidOperationException("NATIVE_HOST_OWNER_ALREADY_CLOSED");
            beforeLaunchCancellation.ThrowIfCancellationRequested();
            state = NativeProcessOwnerState.Starting;
            startup = Task.Run(() => StartCoreAsync(beforeLaunchCancellation));
            return startup;
        }
    }

    // This means an EOF/close request has been installed, never retirement.
    public Task RequestCloseAsync()
    {
        NamedPipeServerStream? transport;
        lock (sync)
        {
            closeRequested = true;
            shutdownBudget.ObserveShutdown(lifetimeClock.Elapsed);
            creation.RequestClose();
            if (!resourcesRetired && state != NativeProcessOwnerState.Unsupported) state = NativeProcessOwnerState.Closing;
            transport = retainingBootstrapForFacts ? null : pipe;
            if (startup is null && creation.CanRetireNeverCreated) RetireNeverCreatedLocked();
        }
        transport?.Dispose();
        return Task.CompletedTask;
    }

    // Cancellation affects this caller's wait only. No kill/detach/fake ACK.
    public Task WaitForRetirementAsync(CancellationToken waitCancellation = default)
        => retired.Task.WaitAsync(waitCancellation);

    private async Task StartCoreAsync(CancellationToken beforeLaunchCancellation)
    {
        try
        {
            beforeLaunchCancellation.ThrowIfCancellationRequested();
            ValidateCurrentParent();
            RequireSupportedDesktop();
            RuntimeHostStaging.RequireSealed(manifest);
            bool creationClaimed = false;
            try
            {
                lock (sync)
                {
                    // This is the launch-intent linearization point against
                    // RequestClose. A prior close wins; a later close is visible
                    // but cannot claim zero resources while the factory owns
                    // creation. No native process call or await holds sync.
                    if (!creation.TryClaimCreation()) throw new OperationCanceledException("NATIVE_HOST_CLOSED_BEFORE_LAUNCH");
                    creationClaimed = true;
                }
                var createdPipe = new NamedPipeServerStream(NativeHostBootstrapProtocol.PipeName(manifest.Nonce), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                lock (sync)
                {
                    pipe = createdPipe;
                    if (closeRequested) throw new OperationCanceledException("NATIVE_HOST_CLOSED_DURING_CREATION_INTENT");
                }
                // All paths are pinned and all arguments are fixed identities.
                // Close may arrive here: the owned factory must still settle,
                // and its actual original handles remain with this owner.
                launch.Start(Path.Combine(manifest.HostDirectory, manifest.HostExecutable),
                    "--station-host " + NetOnlyLaunch.Quote(manifest.ManifestPath) + " " + manifest.ManifestSha256,
                    manifest.HostDirectory);
            }
            finally
            {
                if (creationClaimed) lock (sync) creation.CompleteCreation(
                    nativeCallAttempted: launch.create_process_with_logon_calls != 0,
                    actualNoChildCreatedProven: launch.no_child_created_proven);
            }
            using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                Task connection = pipe.WaitForConnectionAsync(connect.Token);
                while (!connection.IsCompleted)
                {
                    if (launch.Exited)
                    {
                        connect.Cancel();
                        try { await connection.ConfigureAwait(false); } catch (OperationCanceledException) { }
                        throw new EndOfStreamException("ORIGINAL_NATIVE_HOST_EXITED_BEFORE_CONNECT");
                    }
                    await Task.WhenAny(connection, Task.Delay(50)).ConfigureAwait(false);
                }
                await connection.ConfigureAwait(false);
            }
            RequireOriginalStation();
            NativeBootstrapFact job = await NativeHostBootstrapProtocol.ReadAsync(pipe, manifest,
                launch.original_pid, launch.original_creation_filetime).ConfigureAwait(false);
            if (job.Kind != NativeBootstrapKind.JobReady || launch.Process is null)
                throw new InvalidDataException("NATIVE_ORIGINAL_JOB_BOOTSTRAP_REQUIRED");
            var auditRoots = manifest.ProcessAuditRoots;
            witness = new SessionJobWitness(launch.Process, job.OriginalJobHandle,
                auditRoots.Host, auditRoots.Runtime, DerivedPackageSid(manifest.Nonce));
            if (!witness.fresh_admission_passed) throw new InvalidDataException("NATIVE_FRESH_QUERY_JOB_ADMISSION_FAILED");
            lock (sync) { queryJobBound = true; originalJobEmpty = witness.empty; allObservedSignaled = witness.audit.AllSignaled; }
            RequireOriginalStation();
            async Task<NativeBootstrapFact> Expect(NativeBootstrapKind kind)
            {
                NativeBootstrapFact fact = await NativeHostBootstrapProtocol.ReadAsync(pipe, manifest,
                    launch.original_pid, launch.original_creation_filetime).ConfigureAwait(false);
                RequireOriginalStation();
                if (fact.Kind != kind) throw new InvalidDataException("NATIVE_WRONG_BOOTSTRAP_PHASE");
                return fact;
            }
            Task Send(NativeBootstrapKind kind) => NativeHostBootstrapProtocol.WriteAsync(pipe, manifest,
                new(kind, launch.original_pid, launch.original_creation_filetime, 0));
            // A partially written grant is ambiguous. From this point cleanup
            // requires observed absence of the exact private objects as well.
            lock (sync)
            {
                if (closeRequested) throw new OperationCanceledException("NATIVE_CONTEXT_GRANT_CANCELLED");
                retainingBootstrapForFacts = true; ownedContextStartPermitIssued = true;
            }
            await Send(NativeBootstrapKind.QueryJobBound).ConfigureAwait(false);
            await Expect(NativeBootstrapKind.ContextCreated).ConfigureAwait(false);
            ownedContext = await NativeHostContextFacts.CaptureAsync(pipe, launch.parent_identity!.UserSid,
                DerivedPackageSid(manifest.Nonce)).ConfigureAwait(false);
            lock (sync)
            {
                if (closeRequested) throw new OperationCanceledException("NATIVE_CLOSED_BEFORE_PROFILE_GRANT");
                ownedContext.ProfileCreationPermitted = true;
            }
            await Send(NativeBootstrapKind.ContextBound).ConfigureAwait(false);
            await Expect(NativeBootstrapKind.ProfileCreated).ConfigureAwait(false);
            ownedContext.CaptureOriginalProfile();
            lock (sync)
            {
                retainingBootstrapForFacts = false;
                if (closeRequested) throw new OperationCanceledException("NATIVE_CLOSED_BEFORE_RESTRICTED_ROOT_GRANT");
            }
            await Send(NativeBootstrapKind.ProfileBound).ConfigureAwait(false);
            NativeBootstrapFact host = await Expect(NativeBootstrapKind.RestrictedRootCreated).ConfigureAwait(false);
            lock (peerSync) restrictedRoot = CaptureLiveMember(checked((uint)host.OriginalJobHandle), Path.Combine(manifest.HostDirectory, manifest.HostExecutable));
            await Send(NativeBootstrapKind.RestrictedRootBound).ConfigureAwait(false);
            NativeBootstrapFact controller = await Expect(NativeBootstrapKind.BrowserControllerReady).ConfigureAwait(false);
            lock (peerSync) boundBrowser = CaptureLiveMember(checked((uint)controller.OriginalJobHandle), Path.Combine(manifest.RuntimeDirectory, "msedgewebview2.exe"));
            RequireOriginalPeer();
        }
        catch (Exception error)
        {
            lock (sync)
            {
                failureCode ??= error is PlatformNotSupportedException ? "NATIVE_WINDOWS_CLIENT_DESKTOP_REQUIRED" : NativeFailure.Describe(error);
                if (state != NativeProcessOwnerState.Unsupported) state = NativeProcessOwnerState.Unknown;
            }
            try { pipe?.Dispose(); } catch (Exception disposeError) { Remember(disposeError); }
            throw;
        }
        finally
        {
            lock (sync)
            {
                retainingBootstrapForFacts = false;
                if (creation.CanRetireNeverCreated && (launch.create_process_with_logon_calls == 0 || launch.no_child_created_proven)) RetireNeverCreatedLocked();
                else observation ??= Task.Run(ObserveOriginalLifetimeAsync);
            }
        }
    }

    public static void RequireSupportedDesktop()
    {
        var os = new WindowsVersion { Size = Marshal.SizeOf<WindowsVersion>(), ServicePack = "" };
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !Environment.UserInteractive ||
            RtlGetVersion(ref os) != 0 || !NativePlatformPolicy.IsVersionAllowed(os.ProductType, os.Build))
            throw new PlatformNotSupportedException("NATIVE_WINDOWS_CLIENT_DESKTOP_REQUIRED");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsVersion
    {
        internal int Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        internal ushort ServicePackMajor, ServicePackMinor, SuiteMask;
        internal byte ProductType, Reserved;
    }
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlGetVersion(ref WindowsVersion version);

    private void ValidateCurrentParent()
    {
        using var current = SessionNative.Current();
        SessionIdentity identity = SessionNative.Identity(current);
        if (manifest.ParentProcessId != (uint)Environment.ProcessId || identity.elevated || identity.appcontainer || identity.restricted || !identity.owner_is_user || identity.integrity_rid != 8192 ||
            !Native.GetProcessTimes(current.DangerousGetHandle(), out var creation, out _, out _, out _) || creation.Value != manifest.ParentCreationFileTime ||
            !Path.GetFullPath(SessionNative.Image(current)).Equals(manifest.ParentExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NATIVE_ACTUAL_ORDINARY_PARENT_BINDING_FAILED");
        using var image = new FileStream(manifest.ParentExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(SHA256.HashData(image)) != manifest.ParentExecutableSha256)
            throw new InvalidDataException("NATIVE_PARENT_IMAGE_BYTES_CHANGED");
    }

    public void RequireOriginalPeer()
    {
        lock (peerSync)
        {
            RequireOriginalStation();
            SessionJobWitness group = witness ?? throw new InvalidOperationException("NATIVE_ORIGINAL_QUERY_JOB_REQUIRED");
            if (transportPoisoned || restrictedRoot is null || boundBrowser is null)
                throw new IOException("NATIVE_VERIFIED_BROWSER_PEER_UNAVAILABLE");
            group.Tick();
            // Snapshot reflects this original kernel observation, not the
            // empty Job observed before the restricted children were created.
            lock (sync)
            {
                originalJobEmpty = group.last_query_succeeded && group.empty;
                allObservedSignaled = group.last_query_succeeded && group.audit.AllSignaled;
            }
            if (!group.last_query_succeeded || group.historical_query_failure || group.audit.ObservationGap ||
                group.audit.Held.Any(p => !p.image_in_new_copied_trees || !group.audit.BoundRestricted(p)) ||
                restrictedRoot.original_handle_signaled || boundBrowser.original_handle_signaled ||
                !group.process_ids.Contains(restrictedRoot.pid) || !group.process_ids.Contains(boundBrowser.pid))
                throw new InvalidDataException("NATIVE_ORIGINAL_RESTRICTED_BROWSER_GROUP_REQUIRED");
        }
    }

    private HeldProcess CaptureLiveMember(uint pid, string expectedImage)
    {
        SessionJobWitness group = witness ?? throw new InvalidOperationException("NATIVE_ORIGINAL_QUERY_JOB_REQUIRED");
        group.Tick();
        if (!group.last_query_succeeded || group.historical_query_failure || group.audit.ObservationGap ||
            !group.process_ids.Contains(pid) || group.audit.Held.Any(p => !p.image_in_new_copied_trees || !group.audit.BoundRestricted(p)))
            throw new InvalidDataException("NATIVE_KERNEL_MEMBER_BINDING_FAILED");
        HeldProcess? process = group.audit.Held.SingleOrDefault(p => p.pid == pid && !p.original_handle_signaled);
        if (process is null || process.image_path_binding_sha256 != "sha256:" + OwnedFiles.HashText(Path.GetFullPath(expectedImage).ToUpperInvariant()))
            throw new InvalidDataException("NATIVE_ORIGINAL_MEMBER_IMAGE_REQUIRED");
        return process;
    }

    public async Task<NativeViewPacket> ExchangeAsync(NativeViewKind kind, byte[] payload, CancellationToken beforeWriteCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!NativeHostContextFacts.IsRequest(kind)) throw new ArgumentException("NATIVE_VIEW_REQUEST_REQUIRED", nameof(kind));
        NativeViewProtocol.Validate(1, kind, payload.Length);
        if (Interlocked.Increment(ref queuedExchangeCount) > 32)
        {
            Interlocked.Decrement(ref queuedExchangeCount);
            throw new InvalidOperationException("NATIVE_EXCHANGE_CAPACITY");
        }
        try
        {
            byte[] ownedPayload = payload.ToArray();
            await exchange.WaitAsync(beforeWriteCancellation).ConfigureAwait(false);
            bool writeAttempted = false, closeAttempted = false;
            try
            {
                beforeWriteCancellation.ThrowIfCancellationRequested();
                // Once a write starts, the original exchange owns its response.
                // Caller cancellation cannot surrender the wire to a later request.
                RequireOriginalPeer();
                // Kernel peer verification can take time. Recheck the original
                // input generation before consuming a sequence or starting I/O.
                beforeWriteCancellation.ThrowIfCancellationRequested();
                long sequence = checked(++nextSequence);
                NamedPipeServerStream stream = pipe ?? throw new IOException("NATIVE_VIEW_PIPE_REQUIRED");
                if (kind == NativeViewKind.Close)
                    lock (sync)
                    {
                        shutdownBudget.BeginCloseExchange(lifetimeClock.Elapsed);
                        closeAttempted = true;
                        state = NativeProcessOwnerState.Closing;
                    }
                writeAttempted = true;
                // This owned deadline cancels the actual I/O, then poisons the
                // direction and requests original-group close. It is never an
                // ACK or evidence that a partially written delivery was absent.
                using var deadline = new CancellationTokenSource(OwnedJobPolicy.ExchangeDeadline);
                await NativeViewProtocol.WriteAsync(stream, new(sequence, kind, ownedPayload), deadline.Token).ConfigureAwait(false);
                NativeViewPacket reply = await NativeViewProtocol.ReadAsync(stream, deadline.Token).ConfigureAwait(false);
                NativeHostContextFacts.RequireReply(kind, reply, sequence);
                if (reply.Kind == NativeViewKind.Failed)
                    lock (sync) failureCode ??= NativeBrowserFailure.Sanitize(reply.Payload);
                if (kind != NativeViewKind.Close) RequireOriginalPeer();
                if (reply.Kind == NativeViewKind.Failed)
                {
                    lock (peerSync) transportPoisoned = true;
                }
                return reply;
            }
            catch (OperationCanceledException) when (!writeAttempted && beforeWriteCancellation.IsCancellationRequested) { throw; }
            catch
            {
                lock (peerSync) transportPoisoned = true;
                await RequestCloseAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (closeAttempted)
                    lock (sync) shutdownBudget.CompleteCloseExchange(lifetimeClock.Elapsed);
                exchange.Release();
            }
        }
        finally { Interlocked.Decrement(ref queuedExchangeCount); }
    }

    private void RequireOriginalStation()
    {
        if (pipe is null || launch.Process is null || launch.Exited ||
            !SessionNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != launch.original_pid ||
            SessionNative.GetProcessId(launch.Process) != launch.original_pid ||
            !Native.GetProcessTimes(launch.Process.DangerousGetHandle(), out var creation, out _, out _, out _) || creation.Value != launch.original_creation_filetime ||
            !Path.GetFullPath(SessionNative.Image(launch.Process)).Equals(Path.Combine(manifest.HostDirectory, manifest.HostExecutable), StringComparison.OrdinalIgnoreCase) ||
            launch.parent_identity is null || !SessionNative.TokenAdmission(launch.parent_identity, SessionNative.Identity(launch.Process)))
            throw new InvalidDataException("NATIVE_ORIGINAL_KERNEL_PIPE_PEER_REQUIRED");
        lock (sync) state = closeRequested ? NativeProcessOwnerState.Closing : NativeProcessOwnerState.Authenticated;
    }

    private async Task ObserveOriginalLifetimeAsync()
    {
        // The original handles stay owned even on a broken pipe, failed startup,
        // rejected token, or suspended thread. A recovery deadline may request
        // termination of only the original Job; it never proves retirement.
        try
        {
            while (true)
            {
                launch.ObserveExitState();
                lock (peerSync) witness?.Tick();
                lock (sync) { originalJobEmpty = witness?.empty == true; allObservedSignaled = witness?.audit.AllSignaled == true; }
                bool originalExited = launch.original_process_signaled && launch.original_thread_signaled;
                bool exactGroupEmpty = witness is { groups_retired: true };
                bool noGroupLaunched = !ownedContextStartPermitIssued && witness is null;
                bool contextRetired = !ownedContextStartPermitIssued || originalExited && exactGroupEmpty && ownedContext?.VerifyAbsent() == true;
                if (originalExited && (exactGroupEmpty || noGroupLaunched) && !creation.CreationOwned && contextRetired) break;
                bool shutdownRequired;
                TimeSpan recoveryElapsed;
                bool unexpectedRestrictedExit;
                lock (peerSync) unexpectedRestrictedExit = restrictedRoot?.original_handle_signaled == true || boundBrowser?.original_handle_signaled == true;
                bool interruptTransport;
                lock (sync)
                {
                    interruptTransport = unexpectedRestrictedExit && !shutdownBudget.CloseExchangeStarted && !closeRequested;
                    if (interruptTransport) { state = NativeProcessOwnerState.Unknown; failureCode ??= "NATIVE_ORIGINAL_BROWSER_EXITED"; }
                    if (closeRequested || state == NativeProcessOwnerState.Unknown || launch.original_process_signaled)
                        shutdownBudget.ObserveShutdown(lifetimeClock.Elapsed);
                    shutdownRequired = shutdownBudget.TryGetRecoveryElapsed(lifetimeClock.Elapsed, out recoveryElapsed);
                }
                if (interruptTransport) await RequestCloseAsync().ConfigureAwait(false);
                if (shutdownRequired)
                {
                    lock (peerSync)
                        if (witness?.RequestRecovery(recoveryElapsed, true) == true)
                            lock (sync) failureCode ??= "NATIVE_OWNED_JOB_RECOVERY_REQUESTED";
                    if (launch.RequestRecoveryBeforeContextGrant(recoveryElapsed, !ownedContextStartPermitIssued))
                        lock (sync) failureCode ??= "NATIVE_ORIGINAL_STATION_RECOVERY_REQUESTED";
                    // A dead broker may have left its captured profile behind.
                    // Cleanup may run only after original process/thread + Job0.
                    if (originalExited && exactGroupEmpty && ownedContext is not null)
                        ownedContext.TryCleanupCapturedProfileAfterGroupExit("AutumnOS.Paimon.NativeHost." + manifest.Nonce[..32]);
                }
                await Task.Delay(100).ConfigureAwait(false);
            }
            lock (peerSync)
            {
                transportPoisoned = true; witness?.CloseAfterEmpty(); launch.CloseOriginalAfterExit();
            }
            pipe?.Dispose();
            lock (sync)
            {
                ReleasePinsLocked();
                resourcesRetired = true;
                state = NativeProcessOwnerState.Retired;
                retired.TrySetResult();
            }
        }
        catch (Exception error)
        {
            Remember(error);
            // A failed observation never completes the successful-retirement
            // task. This owned task remains inspectable; resources stay pending.
            throw;
        }
    }

    private void RetireNeverCreatedLocked()
    {
        if (!creation.CanRetireNeverCreated || creation.CreationOwned || launch.Process is not null || launch.create_process_with_logon_calls != 0 && !launch.no_child_created_proven)
            throw new InvalidOperationException("NATIVE_NEVER_CREATED_PROOF_REQUIRED");
        ReleasePinsLocked();
        resourcesRetired = true;
        if (state != NativeProcessOwnerState.Unsupported) state = NativeProcessOwnerState.Retired;
        retired.TrySetResult();
    }
    private void ReleasePinsLocked() { if (!pinsReleased) { manifest.Dispose(); pinsReleased = true; } }
    private void Remember(Exception error) { lock (sync) { failureCode ??= NativeFailure.Describe(error); if (!resourcesRetired) state = NativeProcessOwnerState.Unknown; } }

    private static string DerivedPackageSid(string nonce)
    {
        int hr = DeriveAppContainerSidFromAppContainerName("AutumnOS.Paimon.NativeHost." + nonce[..32], out IntPtr sid);
        try { if (hr != 0 || sid == IntPtr.Zero) throw new COMException("NATIVE_DERIVE_PACKAGE_SID", hr); return Native.SidString(sid); }
        finally { if (sid != IntPtr.Zero) Native.FreeSid(sid); }
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
}
