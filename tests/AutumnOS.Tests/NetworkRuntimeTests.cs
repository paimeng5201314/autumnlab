using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Synthetic authority, DNS, HTTP and SDK fixtures. These do not attest native
// browser isolation or public Internet reachability; the loopback transport is separate.
internal static class NetworkRuntimeTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("status_uses_link_policy_and_unknown_internet_without_permission", Status);
        yield return Case("request_requires_native_instance_proof_and_grant_before_dns", Admission);
        yield return Case("same_epoch_expired_identity_never_resolves_or_sends", InitiallyExpired);
        yield return Case("revoked_pending_dns_cannot_start_transport_or_revive_on_regrant", RevokeDns);
        yield return Case("same_epoch_expiry_cancels_pending_dns_before_transport", ExpireDns);
        yield return Case("revoke_regrant_after_first_response_cannot_start_redirect_hop", RevokeRedirect);
        yield return Case("same_epoch_expiry_after_first_response_cannot_start_redirect_hop", ExpireRedirect);
        yield return Case("preview_offline_cycle_cancels_pending_dns_same_epoch", OfflineDns);
        yield return Case("revocation_blocks_next_actual_stream_write_without_waiting_for_inflight_io", RevokeWrite);
        yield return Case("same_epoch_expiry_blocks_next_actual_stream_write", ExpireWrite);
        yield return Case("preview_offline_blocks_next_actual_stream_write", OfflineWrite);
        yield return Case("changed_domain_scope_blocks_next_actual_stream_write", ScopeWrite);
        yield return Case("close_cancels_pending_dns_and_prevents_send", CloseDns);
        yield return Case("suspend_resume_does_not_revive_pending_network_work", SuspendDns);
        yield return Case("prepared_network_response_cannot_post_after_revoke_regrant", ResponseRevoke);
        yield return Case("prepared_network_response_cannot_post_after_offline_cycle", ResponseOffline);
        yield return Case("prepared_network_response_cannot_post_after_same_epoch_expiry", ResponseExpired);
        yield return Case("policy_token_alone_cancels_dns_and_blocks_prepared_response_same_identity_epoch", PolicyOnly);
        yield return Case("preview_permission_domain_expansion_requires_new_prompt", PreviewScope);
        yield return Case("noncooperative_http_fake_still_has_bounded_deadline", BoundedTimeout);
        yield return Case("fixed_user_agent_and_explicit_app_bearer_have_no_ambient_cookie_or_credentials", FixedHeaders);
        yield return Case("raw_url_whitespace_and_noncanonical_scheme_never_start_dns", UrlWhitespace);
        yield return Case("tls_chain_cannot_download_aia_or_change_system_hostname_eku_validation", TlsPolicy);
        yield return Case("live_preview_policy_lease_serializes_actual_io_admission_with_offline_commit", LivePolicyIo);
        yield return Case("live_preview_policy_lease_serializes_local_native_post_with_offline_commit", LivePolicyPost);
    }
    private static (string, Action) Case(string name, Func<Task> run) =>
        ("network.runtime.synthetic_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());

    private static Task Status() => InFixture(async fixture =>
    {
        fixture.Grant(PermissionDecision.Revoked);
        Check(Ok((await fixture.Send("network.getStatus")).Response).GetProperty("available").GetBoolean());
        fixture.Link = false;
        JsonElement status = Ok((await fixture.Send("network.getStatus")).Response);
        Check(!status.GetProperty("available").GetBoolean() && status.GetProperty("internetAccess").GetString() == "unknown");
        Check(fixture.Resolutions == 0 && fixture.Sends == 0);
    });
    private static Task Admission() => InFixture(async f =>
    {
        f.Grant(PermissionDecision.Revoked);
        Error((await f.Send()).Response, "PERMISSION_REVOKED"); Check(f.Resolutions == 0);
        f.Grant(PermissionDecision.Granted);
        using var noProof = f.NewSession(proof: false);
        Error((await f.Send(session: noProof.Session)).Response, "NETWORK_ISOLATION_REQUIRED"); Check(f.Resolutions == 0);
        Ok((await f.Send()).Response); Check(f.Resolutions == 1 && f.Sends == 1);
    });
    private static Task InitiallyExpired() => InFixture(async f =>
    {
        f.Identity.Update(f.Identity.Snapshot with { State = IdentitySessionState.SessionExpired, AuthorityRevision = 1 });
        Error((await f.Send()).Response, "SESSION_EXPIRED"); Check(f.Resolutions == 0 && f.Sends == 0);
    });
    private static Task RevokeDns() => PendingDns(f =>
    { f.Grant(PermissionDecision.Revoked); f.Grant(PermissionDecision.Granted); }, "PERMISSION_REVOKED");
    private static Task ExpireDns() => PendingDns(f =>
        f.Identity.Update(f.Identity.Snapshot with { State = IdentitySessionState.SessionExpired, AuthorityRevision = 1 }), "SESSION_EXPIRED");
    private static Task RevokeRedirect() => PendingRedirect(f => { f.Grant(PermissionDecision.Revoked); f.Grant(PermissionDecision.Granted); }, "PERMISSION_REVOKED");
    private static Task ExpireRedirect() => PendingRedirect(f => f.Identity.Update(f.Identity.Snapshot with { State = IdentitySessionState.SessionExpired, AuthorityRevision = 1 }), "SESSION_EXPIRED");
    private static Task PendingRedirect(Action<Fixture> mutate, string code) => InFixture(async f =>
    {
        var ready = Signal(); var complete = Signal();
        f.Handler = (_, _) => new Handler(async (_, _) =>
        {
            ready.TrySetResult(true); await complete.Task;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new ByteArrayContent([]) };
            response.Headers.Location = new Uri("https://api.example.com/next"); return response;
        });
        Task<RuntimePreparedResponse> request = f.Send(); await ready.Task; mutate(f); complete.TrySetResult(true);
        Error((await request).Response, code); Check(f.Resolutions == 1 && f.Sends == 1);
    });
    private static Task OfflineDns() => PendingDns(f => { f.Simulation!.SetOffline(true); f.Simulation.SetOffline(false); }, "NETWORK_POLICY_CHANGED", preview: true);
    private static Task CloseDns() => PendingDns(f => f.Session.Close(), "SESSION_EXPIRED");
    private static Task SuspendDns() => PendingDns(f => { f.Session.Suspend(); f.Session.Resume(); }, "SESSION_SUSPENDED");
    private static Task PendingDns(Action<Fixture> mutation, string code, bool preview = false) => InFixture(async f =>
    {
        var waiting = Signal(); var resolution = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Resolve = (_, _) => { waiting.TrySetResult(true); return resolution.Task; };
        Task<RuntimePreparedResponse> work = f.Send(); await waiting.Task;
        mutation(f);
        // A policy-only scope mutation may have no cancellation event; complete DNS
        // to exercise the mandatory post-DNS gate rather than assuming an event.
        resolution.TrySetResult([IPAddress.Parse("93.184.216.34")]);
        Error((await work).Response, code); Check(f.Sends == 0);
    }, preview);

    private static Task RevokeWrite() => PendingWrite(f => f.Grant(PermissionDecision.Revoked), "PERMISSION_REVOKED");
    private static Task ExpireWrite() => PendingWrite(f =>
        f.Identity.Update(f.Identity.Snapshot with { State = IdentitySessionState.SessionExpired, AuthorityRevision = 1 }), "SESSION_EXPIRED");
    private static Task OfflineWrite() => PendingWrite(f => f.Simulation!.SetOffline(true), "NETWORK_POLICY_CHANGED", preview: true);
    private static Task ScopeWrite() => PendingWrite(f => f.Domains.Add("other.example.com"), "PERMISSION_DENIED");
    private static Task PendingWrite(Action<Fixture> mutation, string code, bool preview = false) => InFixture(async f =>
    {
        var raw = new PendingWriteStream(); var ready = Signal(); var proceed = Signal(); var finished = Signal();
        f.Handler = (guard, token) => new Handler(async (_, _) =>
        {
            await using var stream = new RuntimeNetworkAuthorityStream(raw, guard, token);
            ValueTask first = stream.WriteAsync(new byte[] { 1 }.AsMemory(), CancellationToken.None);
            ready.TrySetResult(true); await proceed.Task;
            raw.Finish.TrySetResult(true);
            try { await first; await stream.WriteAsync(new byte[] { 2 }.AsMemory(), CancellationToken.None); }
            catch (Exception error) when (error is RuntimeCapabilityException or OperationCanceledException) { }
            finally { finished.TrySetResult(true); }
            return GoodResponse();
        });
        Task<RuntimePreparedResponse> work = f.Send(); await ready.Task;
        // First raw I/O is pending. Mutation must complete without waiting for it:
        // the authority Monitor was released immediately after I/O initiation.
        await Task.Run(() => mutation(f)).WaitAsync(TimeSpan.FromSeconds(2));
        proceed.TrySetResult(true); await finished.Task;
        Error((await work).Response, code); Check(raw.Writes == 1);
    }, preview);

    private static Task ResponseRevoke() => InFixture(async f =>
    {
        RuntimePreparedResponse response = await f.Send(); Ok(response.Response);
        f.Grant(PermissionDecision.Revoked); f.Grant(PermissionDecision.Granted);
        int posts = 0; Error(f.Session.DeliverResponse("network.request", response.Stamp, response.Response, _ => posts++), "PERMISSION_REVOKED");
        Check(posts == 0);
    });
    private static Task ResponseOffline() => InFixture(async f =>
    {
        RuntimePreparedResponse response = await f.Send(); Ok(response.Response);
        f.Simulation!.SetOffline(true); f.Simulation.SetOffline(false);
        int posts = 0; Error(f.Session.DeliverResponse("network.request", response.Stamp, response.Response, _ => posts++), "NETWORK_POLICY_CHANGED");
        Check(posts == 0 && f.Simulation.Snapshot.SessionEpoch == f.Account.Epoch);
        Ok((await f.Send()).Response);
    }, preview: true);
    private static Task ResponseExpired() => InFixture(async f =>
    {
        RuntimePreparedResponse response = await f.Send();
        f.Identity.Update(f.Identity.Snapshot with { State = IdentitySessionState.SessionExpired, AuthorityRevision = 1 });
        int posts = 0; Error(f.Session.DeliverResponse("network.request", response.Stamp, response.Response, _ => posts++), "SESSION_EXPIRED");
        Check(posts == 0);
    });
    private static Task BoundedTimeout() => InFixture(async f =>
    {
        var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler = (_, _) => new Handler((_, _) => never.Task);
        Error((await f.Send(parameters: new { url = "https://api.example.com/", timeoutMs = 20 })).Response, "NETWORK_TIMEOUT");
        never.TrySetResult(GoodResponse());
    });
    private static Task FixedHeaders() => InFixture(async f =>
    {
        f.Handler = (_, _) => new Handler((request, _) =>
        {
            Check(request.Headers.UserAgent.ToString() == "AutumnOS-Network/0.4");
            Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Proxy-Authorization"));
            Check(request.Headers.Authorization?.ToString() == "Bearer app_owned.token==");
            Check(request.Version == HttpVersion.Version11 && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact);
            return Task.FromResult(GoodResponse());
        });
        Ok((await f.Send(parameters: new { url = "https://api.example.com/", headers = new Dictionary<string, string> { ["authorization"] = "Bearer app_owned.token==" } })).Response);
        Error((await f.Send(parameters: new { url = "https://api.example.com/", headers = new Dictionary<string, string> { ["user-agent"] = "attacker" } })).Response, "NETWORK_HEADERS_INVALID");
        using var handler = RuntimeNetworkService.CreatePinnedHandler(new Uri("https://api.example.com/"), [IPAddress.Parse("93.184.216.34")], () => new EmptyLease(), default);
        Check(!handler.UseCookies && !handler.UseProxy && !handler.PreAuthenticate && handler.Credentials is null && handler.DefaultProxyCredentials is null && !handler.AllowAutoRedirect && handler.AutomaticDecompression == DecompressionMethods.None);
    });
    private static Task UrlWhitespace() => InFixture(async f =>
    {
        foreach (string url in new[] { "https://api.example.com/a b", "https://api.example.com/a\u00a0b", "https://api.example.com/a\tb", " https://api.example.com/", "HTTPS://api.example.com/" })
            Error((await f.Send(parameters: new { url })).Response, "NETWORK_URL_NOT_ALLOWED");
        Check(f.Resolutions == 0 && f.Sends == 0);
    });
    private static Task TlsPolicy()
    {
        using var handler = RuntimeNetworkService.CreatePinnedHandler(new Uri("https://api.example.com/"),
            [IPAddress.Parse("93.184.216.34")], () => new EmptyLease(), default);
        var ssl = handler.SslOptions; var policy = ssl.CertificateChainPolicy;
        Check(policy is not null && policy.DisableCertificateDownloads && policy.RevocationMode == X509RevocationMode.NoCheck &&
            policy.RevocationFlag == X509RevocationFlag.ExcludeRoot && policy.TrustMode == X509ChainTrustMode.System &&
            policy.VerificationFlags == X509VerificationFlags.NoFlag && policy.CustomTrustStore.Count == 0 &&
            policy.ApplicationPolicy.Count == 1 && policy.ApplicationPolicy[0].Value == "1.3.6.1.5.5.7.3.1");
        Check(ssl.RemoteCertificateValidationCallback is null && ssl.LocalCertificateSelectionCallback is null &&
            ssl.ClientCertificates is null && ssl.CertificateRevocationCheckMode == X509RevocationMode.NoCheck);
        return Task.CompletedTask;
    }
    private static Task LivePolicyIo() => InFixture(async f =>
    {
        using var release = new ManualResetEventSlim(); var entered = Signal(); var afterMutation = Signal();
        var raw = new BlockingAdmissionStream(entered, release);
        f.Handler = (guard, token) => new Handler(async (_, _) =>
        {
            await using var stream = new RuntimeNetworkAuthorityStream(raw, guard, token);
            try { await stream.WriteAsync(new byte[] { 1 }.AsMemory()); }
            catch (Exception error) when (error is RuntimeCapabilityException or OperationCanceledException) { }
            await afterMutation.Task;
            return GoodResponse();
        });
        Task<RuntimePreparedResponse> request = Task.Run(() => f.Send());
        await entered.Task;
        await CompleteBlockedOffline(f, release);
        afterMutation.TrySetResult(true);
        Error((await request).Response, "NETWORK_POLICY_CHANGED"); Check(raw.Writes == 1);
        Check(f.Identity.Snapshot.State == IdentitySessionState.SignedIn);
    }, livePreview: true);
    private static Task LivePolicyPost() => InFixture(async f =>
    {
        RuntimePreparedResponse response = await f.Send();
        using var release = new ManualResetEventSlim(); var entered = Signal(); int posts = 0;
        Task<string> posting = Task.Run(() => f.Session.DeliverResponse("network.request", response.Stamp, response.Response, _ =>
        { Interlocked.Increment(ref posts); entered.TrySetResult(true); Check(release.Wait(TimeSpan.FromSeconds(3))); }));
        await entered.Task; await CompleteBlockedOffline(f, release); Ok(await posting); Check(posts == 1);
    }, livePreview: true);
    private static async Task CompleteBlockedOffline(Fixture f, ManualResetEventSlim release)
    {
        var finished = Signal(); Exception? error = null;
        var thread = new Thread(() =>
        {
            try { f.Simulation!.SetOffline(true); }
            catch (Exception exception) { error = exception; }
            finally { finished.TrySetResult(true); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            Check(SpinWait.SpinUntil(() => finished.Task.IsCompleted || (thread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(2)), "Offline writer did not reach its policy gate.");
            Check(!finished.Task.IsCompleted, "Live-preview offline commit escaped the short network-policy lease.");
        }
        finally { release.Set(); }
        await finished.Task; Check(thread.Join(TimeSpan.FromSeconds(1)) && error is null, error?.GetType().Name);
        Check(f.Simulation!.Offline);
    }
    private sealed class BlockingAdmissionStream(TaskCompletionSource<bool> entered, ManualResetEventSlim release) : Stream
    {
        internal int Writes;
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { entered.TrySetResult(true); Check(release.Wait(TimeSpan.FromSeconds(3))); Interlocked.Increment(ref Writes); return ValueTask.CompletedTask; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    private static Task PolicyOnly() => InFixture(async f =>
    {
        f.ManualPolicy = new CancellationTokenSource();
        RuntimePreparedResponse response = await f.Send();
        IdentitySnapshot identity = f.Identity.Snapshot;
        var waiting = Signal(); var result = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Resolve = (_, _) => { waiting.TrySetResult(true); return result.Task; };
        Task<RuntimePreparedResponse> work = f.Send(); await waiting.Task;
        var old = f.ManualPolicy; f.ManualPolicy = new CancellationTokenSource(); old.Cancel(); old.Dispose();
        Error((await work).Response, "NETWORK_POLICY_CHANGED");
        result.TrySetResult([IPAddress.Parse("93.184.216.34")]);
        int posts = 0; Error(f.Session.DeliverResponse("network.request", response.Stamp, response.Response, _ => posts++), "NETWORK_POLICY_CHANGED");
        Check(posts == 0 && f.Identity.Snapshot == identity && f.Sends == 1);
    });
    private static Task PreviewScope() => InFixture(async f =>
    {
        using var preview = new DeveloperPreviewPermissions(f.Permissions);
        preview.SetDecision(f.Account, f.Application, "network", PermissionDecision.Granted);
        Check(preview.Query(f.Account, f.Application, "network") == PermissionDecision.Granted);
        f.Domains.Add("second.example.com");
        Check(preview.Query(f.Account, f.Application, "network") == PermissionDecision.Prompt);
        preview.SetDecision(f.Account, f.Application, "network", PermissionDecision.Granted);
        Check(preview.Query(f.Account, f.Application, "network") == PermissionDecision.Granted);
    });

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AutumnOS-network-synthetic-" + Guid.NewGuid().ToString("N"));
        internal List<string> Domains = ["api.example.com"];
        internal Identity Identity = new();
        internal DeveloperPreviewSimulation? Simulation;
        internal RuntimeAccountContext Account;
        internal RuntimeApplication Application;
        internal PermissionService Permissions;
        internal RuntimeSession Session;
        internal RuntimeNetworkService Service;
        internal CancellationTokenSource? ManualPolicy;
        private readonly bool simulationIdentity;
        internal int Resolutions, Sends;
        internal bool Link = true;
        internal Func<string, CancellationToken, Task<IPAddress[]>> Resolve = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]);
        internal Func<Func<IDisposable>, CancellationToken, HttpMessageHandler> Handler;
        internal Fixture(bool preview, bool livePreview = false)
        {
            simulationIdentity = preview;
            Directory.CreateDirectory(Root); Permissions = new(Root);
            Handler = (_, _) => new Handler((_, _) => Task.FromResult(GoodResponse()));
            if (preview) { Simulation = new(); Simulation.SwitchAccount("guest"); Account = Simulation.BindAccount(CancellationToken.None); }
            else
            {
                if (livePreview) Simulation = new();
                Account = new("synthetic-account", 1, false, () => true, CancellationToken.None, () => Identity.EnterSessionLease("synthetic-account", 1));
            }
            Application = new(new("test.network", 42, null), "synthetic-package", "Synthetic network app", ["network"], NetworkDomains: Domains);
            Service = new((host, token) => { Interlocked.Increment(ref Resolutions); return Resolve(host, token); },
                (_, _, guard, token) => new CountingHandler(Handler(guard, token), () => Interlocked.Increment(ref Sends)), () => Link);
            Grant(PermissionDecision.Granted); Session = NewSession().Session;
        }
        internal SessionOwner NewSession(bool proof = true)
        {
            var session = new RuntimeSession("test.network", ["network"], Root, services: new()
            {
                Account = Account, Application = Application, Permissions = Permissions,
                RequestPermission = (_, _) => Task.FromResult(false), Identity = simulationIdentity ? Simulation : Identity,
                Network = Service, NetworkDisabled = () => Simulation?.Offline == true,
                NetworkPolicyInvalidated = () => ManualPolicy?.Token ?? Simulation?.NetworkPolicyInvalidated ?? default,
                EnterNetworkPolicyLease = token => Simulation?.EnterNetworkPolicyLease(token) ?? new EmptyLease()
            });
            if (proof) Check(session.ConfirmNetworkIsolation(Proof(session)));
            session.Foreground(); return new(session);
        }
        internal void Grant(PermissionDecision decision) => Permissions.SetDecision(Account, Application, "network", decision);
        internal Task<RuntimePreparedResponse> Send(string method = "network.request", object? parameters = null, RuntimeSession? session = null) =>
            (session ?? Session).HandleMessageForDeliveryAsync((session ?? Session).PageUri, JsonSerializer.Serialize(new
            { protocolVersion = 1, requestId = Guid.NewGuid().ToString("N"), method, @params = parameters ?? (method == "network.request" ? (object)new { url = "https://api.example.com/" } : new { }) }), (_, _) => Task.FromResult(false));
        public void Dispose() { Session.Close(); Simulation?.Dispose(); ManualPolicy?.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class SessionOwner(RuntimeSession session) : IDisposable
    { internal RuntimeSession Session = session; public void Dispose() => Session.Close(); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class CountingHandler(HttpMessageHandler inner, Action count) : DelegatingHandler(inner)
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { count(); return base.SendAsync(request, cancellationToken); } }
    private sealed class PendingWriteStream : Stream
    {
        internal int Writes; internal TaskCompletionSource<bool> Finish = Signal();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Writes); await Finish.Task; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    internal sealed class Identity : IIdentityService
    {
        private readonly object gate = new();
        private IdentitySnapshot snapshot = new(IdentitySessionState.SignedIn, "synthetic-account", 1, "Synthetic", null, false, false, null);
        public IdentitySnapshot Snapshot { get { lock (gate) return snapshot; } }
        public event EventHandler<IdentitySnapshot>? Changed;
        internal void Update(IdentitySnapshot value) { lock (gate) snapshot = value; Changed?.Invoke(this, value); }
        public IDisposable EnterSessionLease(string accountNamespace, long sessionEpoch)
        {
            Monitor.Enter(gate);
            if (snapshot.AccountNamespace == accountNamespace && snapshot.SessionEpoch == sessionEpoch) return new Lease(gate);
            Monitor.Exit(gate); throw new RuntimeCapabilityException("SESSION_EXPIRED");
        }
        private sealed class Lease(object gate) : IDisposable { public void Dispose() => Monitor.Exit(gate); }
        public Task<IdentitySnapshot> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<IdentitySnapshot> SignInAsync(bool rememberSignIn, bool reauthenticate = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IdentitySnapshot> SetRememberSignInAsync(bool rememberSignIn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IdentitySnapshot> RefreshAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IdentitySnapshot> SignOutAsync(bool browserSession = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void CancelSignIn() { }
    }
    private static RuntimeIsolationProof Proof(RuntimeSession session)
    {
        var challenge = RuntimeIsolationPolicy.CreateChallenge(session.Instance.Id.Value, session.Origin);
        var peers = new[] { "peer-1", "peer-2" }.Select(label => new { label, nativeConstructor = true, filteredIceServers = true, relayOnly = true,
            iceConnectionState = "failed", connectionState = "new", candidates = 0, restartedIceConnectionState = "failed" });
        string report = JsonSerializer.Serialize(new { protocolVersion = 1, nonce = challenge.Nonce, instanceId = challenge.InstanceId.ToString("D"),
            origin = challenge.Origin, completed = true, secureContext = true, probes = peers });
        return RuntimeIsolationPolicy.ValidateNativeResult(challenge, JsonSerializer.Serialize(report)) ?? throw new InvalidOperationException("Synthetic isolation proof rejected.");
    }
    private static HttpResponseMessage GoodResponse() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("synthetic")) };
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task InFixture(Func<Fixture, Task> run, bool preview = false, bool livePreview = false) { using var fixture = new Fixture(preview, livePreview); await run(fixture); }
    private static JsonElement Ok(string response)
    { using var document = JsonDocument.Parse(response); Check(document.RootElement.GetProperty("ok").GetBoolean(), response); return document.RootElement.GetProperty("result").Clone(); }
    private static void Error(string response, string code)
    { using var document = JsonDocument.Parse(response); Check(!document.RootElement.GetProperty("ok").GetBoolean() && document.RootElement.GetProperty("error").GetProperty("code").GetString() == code, response + " expected " + code); }
    private static void Check(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Synthetic network assertion failed."); }
}
