using System.Text.Json;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class RuntimeIsolationSessionTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("runtime.isolation_session_proof_does_not_transfer_to_another_instance", () => InSession((first, root) =>
        {
            var second = new RuntimeSession("test.isolation", [], root);
            var proof = Proof(first);
            Check(!first.NetworkSandboxVerified && !second.NetworkSandboxVerified, "New sessions must start unverified.");
            Check(!second.ConfirmNetworkIsolation(proof), "The same app ID cannot inherit another instance's proof.");
            Check(first.ConfirmNetworkIsolation(proof), "The matching starting instance must accept the validated native proof.");
            Check(!first.ConfirmNetworkIsolation(proof), "A proof cannot be applied twice.");
            Check(Capabilities(first) && !Capabilities(second), "SDK output must reflect each instance, not global readiness.");
            first.Foreground(); first.Background(); first.Foreground();
            Check(first.NetworkSandboxVerified, "Foreground transitions retain the verified controller.");
            second.Close();
        }));
        yield return ("runtime.isolation_close_and_crash_revoke_attestation", () => InSession((first, root) =>
        {
            var proof = Proof(first);
            Check(first.ConfirmNetworkIsolation(proof), "Fixture proof must bind.");
            first.MarkCrashed();
            Check(!first.NetworkSandboxVerified && !first.ConfirmNetworkIsolation(proof), "A crashed instance cannot retain or reacquire proof.");
            var second = new RuntimeSession("test.isolation", [], root);
            var secondProof = Proof(second);
            second.Close();
            Check(!second.ConfirmNetworkIsolation(secondProof) && !second.NetworkSandboxVerified, "Closing prevents late attestation.");
        }));
        yield return ("runtime.isolation_attestation_cannot_be_added_after_game_execution", () => InSession((session, _) =>
        {
            var proof = Proof(session); session.Foreground();
            Check(!session.ConfirmNetworkIsolation(proof) && !session.NetworkSandboxVerified, "Attestation must precede execution.");
        }));
        yield return ("runtime.isolation_sdk_cannot_self_attest_or_smuggle_verified_state", () => InSession((session, _) =>
        {
            foreach (var request in new[] {
                new { method = "platform.getCapabilities", parameters = (object)new { networkSandboxVerified = true } },
                new { method = "runtime.confirmNetworkIsolation", parameters = (object)new { verified = true } }
            })
            {
                using var result = JsonDocument.Parse(Send(session, request.method, request.parameters));
                Check(!result.RootElement.GetProperty("ok").GetBoolean() && !session.NetworkSandboxVerified,
                    "SDK messages cannot supply native attestation.");
            }
        }));
        yield return ("runtime.isolation_legacy_session_close_rejects_already_completed_response", () => InSession((session, _) =>
        {
            string response = Send(session, "platform.getCapabilities", new { });
            using (var allowed = JsonDocument.Parse(session.RevalidateResponse("platform.getCapabilities", session.EventRevision, response)))
                Check(allowed.RootElement.GetProperty("ok").GetBoolean(), "Active-session response must be deliverable before close.");
            session.Close();
            string? posted = null;
            session.DeliverResponse("platform.getCapabilities", 0, response, value => posted = value);
            Error(posted!, "SESSION_EXPIRED");
        }));
        yield return ("runtime.isolation_legacy_revocation_rejects_already_completed_save_read", () => InSession((_, root) =>
        {
            var session = new RuntimeSession("test.isolation", ["saves"], root);
            try
            {
                string approval = session.HandleMessageAsync(session.PageUri, JsonSerializer.Serialize(new
                {
                    protocolVersion = 1, requestId = "approve", method = "permissions.request", @params = new { name = "saves" }
                }), (_, _) => Task.FromResult(true)).GetAwaiter().GetResult();
                using (var approved = JsonDocument.Parse(approval))
                    Check(approved.RootElement.GetProperty("ok").GetBoolean(), "Fixture must grant save access.");
                string response = Send(session, "saves.read", new { slot = "game" });
                using (var allowed = JsonDocument.Parse(session.RevalidateResponse("saves.read", 0, response)))
                    Check(allowed.RootElement.GetProperty("ok").GetBoolean(), "A current save grant must permit delivery.");
                session.RevokeSavePermission();
                string? posted = null;
                session.DeliverResponse("saves.read", 0, response, value => posted = value);
                Error(posted!, "PERMISSION_REVOKED");
                session.DeliverResponse("permissions.request", 0, approval, value => posted = value);
                Error(posted!, "PERMISSION_REVOKED");
            }
            finally { session.Close(); }
        }));
    }

    // Synthetic evidence for unit tests only; native policy acceptance is exercised separately.
    private static RuntimeIsolationProof Proof(RuntimeSession session)
    {
        var challenge = RuntimeIsolationPolicy.CreateChallenge(session.Instance.Id.Value, session.Origin);
        var peers = new[] { "peer-1", "peer-2" }.Select(label => new
        {
            label, nativeConstructor = true, filteredIceServers = true, relayOnly = true,
            iceConnectionState = "failed", connectionState = "new", candidates = 0,
            restartedIceConnectionState = "failed"
        });
        string report = JsonSerializer.Serialize(new { protocolVersion = 1, nonce = challenge.Nonce,
            instanceId = challenge.InstanceId.ToString("D"), origin = challenge.Origin,
            completed = true, secureContext = true, probes = peers });
        return RuntimeIsolationPolicy.ValidateNativeResult(challenge, JsonSerializer.Serialize(report))
            ?? throw new InvalidOperationException("Synthetic evidence must satisfy the validator before testing admission.");
    }
    private static bool Capabilities(RuntimeSession session)
    {
        using var result = JsonDocument.Parse(Send(session, "platform.getCapabilities", new { }));
        Check(result.RootElement.GetProperty("ok").GetBoolean(), "Capability request must succeed.");
        return result.RootElement.GetProperty("result").GetProperty("networkSandboxVerified").GetBoolean();
    }
    private static void Error(string response, string expected)
    {
        using var result = JsonDocument.Parse(response);
        Check(!result.RootElement.GetProperty("ok").GetBoolean() && result.RootElement.GetProperty("error").GetProperty("code").GetString() == expected,
            "A stale response must be replaced with the expected denial before native delivery.");
    }
    private static string Send(RuntimeSession session, string method, object parameters) => session.HandleMessageAsync(session.PageUri,
        JsonSerializer.Serialize(new { protocolVersion = 1, requestId = Guid.NewGuid().ToString("N"), method, @params = parameters }),
        (_, _) => Task.FromResult(false)).GetAwaiter().GetResult();
    private static void InSession(Action<RuntimeSession, string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "autumn-isolation-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = new RuntimeSession("test.isolation", [], root);
        try { test(session, root); }
        finally
        {
            session.Close();
            string resolved = Path.GetFullPath(root);
            string ownedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar + "autumn-isolation-session-";
            if (!resolved.StartsWith(ownedParent, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing cleanup outside the newly owned test directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
