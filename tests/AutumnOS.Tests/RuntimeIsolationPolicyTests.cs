using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class RuntimeIsolationPolicyTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach (var test in RuntimeParserGateTests.Cases()) yield return test;
        yield return ("runtime.isolation_accepts_only_the_validated_engine_version", ValidatedEngineVersion);
        yield return ("runtime.isolation_headers_deny_both_async_clipboard_directions", ClipboardPolicyHeaders);
        yield return ("runtime.isolation_challenges_bind_unique_nonce_instance_and_origin", ChallengeBinding);
        yield return ("runtime.isolation_accepts_complete_native_result_for_current_challenge", CompleteNativeResult);
        yield return ("runtime.isolation_rejects_replay_between_challenges_and_instances", ChallengeReplay);
        yield return ("runtime.isolation_rejects_replay_between_origins", OriginReplay);
        yield return ("runtime.isolation_rejects_raw_webmessage_and_pending_results", RejectUntrustedEnvelopes);
        yield return ("runtime.isolation_rejects_duplicate_and_unknown_properties", RejectDuplicateProperties);
        yield return ("runtime.isolation_result_limit_counts_utf8_bytes", ByteLimit);
        yield return ("runtime.isolation_challenge_is_not_caller_constructible_or_mutable", ChallengeOwnership);
        yield return ("runtime.isolation_proof_is_not_caller_constructible_or_mutable", ProofOwnership);
        foreach (string field in new[] { "protocolVersion", "nonce", "instanceId", "origin", "completed", "secureContext", "probes" })
        {
            string selected = field;
            yield return ("runtime.isolation_rejects_missing_" + field, () => RejectMutation(root => root.Remove(selected)));
        }
        foreach ((string name, Action<JsonObject> mutate) in new (string, Action<JsonObject>)[]
        {
            ("protocol_number", root => root["protocolVersion"] = 2),
            ("protocol_string", root => root["protocolVersion"] = "1"),
            ("nonce_case_change", root => root["nonce"] = root["nonce"]!.GetValue<string>().ToUpperInvariant()),
            ("empty_nonce", root => root["nonce"] = ""),
            ("instance_case_change", root => root["instanceId"] = root["instanceId"]!.GetValue<string>().ToUpperInvariant()),
            ("instance_other", root => root["instanceId"] = Guid.NewGuid().ToString("D")),
            ("origin_path", root => root["origin"] = Origin + "index.html"),
            ("origin_no_slash", root => root["origin"] = Origin.TrimEnd('/')),
            ("origin_suffix", root => root["origin"] = "https://bound.autumn.invalid.attacker.example/"),
            ("incomplete", root => root["completed"] = false),
            ("insecure", root => root["secureContext"] = false),
            ("truthy_completed", root => root["completed"] = 1),
            ("null_probes", root => root["probes"] = null),
            ("empty_probes", root => root["probes"] = new JsonArray()),
            ("single_peer", root => root["probes"]!.AsArray().RemoveAt(1)),
            ("extra_peer", root => root["probes"]!.AsArray().Add(root["probes"]![0]!.DeepClone())),
            ("peer_reorder", root => { var peers = root["probes"]!.AsArray(); var first = peers[0]!.DeepClone(); peers[0] = peers[1]!.DeepClone(); peers[1] = first; }),
            ("unknown_root", root => root["networkSandboxVerified"] = true),
            ("unknown_peer", root => root["probes"]![0]!["safe"] = true)
        })
            yield return ("runtime.isolation_rejects_" + name, () => RejectMutation(mutate));
        foreach (string field in new[] { "label", "nativeConstructor", "filteredIceServers", "relayOnly", "iceConnectionState", "connectionState", "candidates", "restartedIceConnectionState" })
        {
            string selected = field;
            yield return ("runtime.isolation_requires_both_peers_" + field, () =>
            {
                for (int peer = 0; peer < 2; peer++)
                {
                    int selectedPeer = peer;
                    RejectMutation(root => root["probes"]![selectedPeer]!.AsObject().Remove(selected));
                    RejectMutation(root => root["probes"]![selectedPeer]![selected] = null);
                }
            });
        }
        foreach ((string name, string field, JsonNode value) in new[]
        {
            ("shim", "nativeConstructor", JsonValue.Create(false)!),
            ("retained_ice_servers", "filteredIceServers", JsonValue.Create(false)!),
            ("all_interfaces", "relayOnly", JsonValue.Create(false)!),
            ("ice_new", "iceConnectionState", JsonValue.Create("new")!),
            ("ice_disconnected", "iceConnectionState", JsonValue.Create("disconnected")!),
            ("ice_connected", "iceConnectionState", JsonValue.Create("connected")!),
            ("connection_connected", "connectionState", JsonValue.Create("connected")!),
            ("connection_closed", "connectionState", JsonValue.Create("closed")!),
            ("candidate_escaped", "candidates", JsonValue.Create(1)!),
            ("negative_candidate_count", "candidates", JsonValue.Create(-1)!),
            ("restarted_ice", "restartedIceConnectionState", JsonValue.Create("checking")!)
        })
            yield return ("runtime.isolation_rejects_peer_" + name, () =>
            {
                for (int peer = 0; peer < 2; peer++)
                {
                    int selectedPeer = peer;
                    RejectMutation(root => root["probes"]![selectedPeer]![field] = value.DeepClone());
                }
            });
        foreach (string invalidOrigin in new[] { "", "http://bound.autumn.invalid/", "https://bound.autumn.invalid", "https://bound.autumn.invalid/path", "https://user@bound.autumn.invalid/", "https://bound.autumn.invalid/?query", "https://bound.autumn.invalid/#fragment" })
        {
            string selected = invalidOrigin;
            yield return ("runtime.isolation_rejects_challenge_origin_" + Convert.ToHexString(Encoding.UTF8.GetBytes(selected)), () =>
            {
                try { RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), selected); }
                catch (ArgumentException) { return; }
                throw new InvalidOperationException("A noncanonical or untrusted origin was accepted.");
            });
        }
    }

    private const string Origin = "https://bound.autumn.invalid/";

    private static void ValidatedEngineVersion()
    {
        Check(RuntimeIsolationPolicy.IsSupportedBrowserVersion("154.0.4258.53"), "The validated fixed engine must be usable.");
        foreach (string? version in new[] { null, "", "154", "154.0.4258.52", "154.0.4258.54", "155.0.4258.53",
            "154.0.4258.53 beta", "154.0.4258.53 dev", "154.0.4258.53 ", " 154.0.4258.53", "154.0.4258.53\0", "154.00.4258.53" })
            Check(!RuntimeIsolationPolicy.IsSupportedBrowserVersion(version), "An absent, changed, decorated, or ambiguous engine must fail closed.");
    }

    private static void ClipboardPolicyHeaders()
    {
        var permissions = RuntimeIsolationPolicy.PermissionsPolicy.Split(',', StringSplitOptions.TrimEntries);
        Check(permissions.Count(value => value == "clipboard-read=()") == 1
            && permissions.Count(value => value == "clipboard-write=()") == 1,
            "Both async clipboard operations need explicit native deny policies; ClipboardRead events alone are insufficient.");
        Check(RuntimeIsolationPolicy.ResponseHeaders.Split("\r\n").Count(value =>
            value == "Permissions-Policy: " + RuntimeIsolationPolicy.PermissionsPolicy) == 1,
            "Every host response must carry the complete policy once. This is a header contract test, not runtime acceptance.");
    }

    private static void ChallengeBinding()
    {
        var instanceId = Guid.NewGuid();
        var first = RuntimeIsolationPolicy.CreateChallenge(instanceId, Origin);
        var second = RuntimeIsolationPolicy.CreateChallenge(instanceId, Origin);
        Check(first.Nonce != second.Nonce && first.Nonce.Length == 64 && first.Nonce.All(char.IsAsciiHexDigitLower), "Challenge nonce must be unique cryptographic bytes.");
        Check(first.InstanceId == instanceId && first.Origin == Origin && first.PageUri == Origin + first.DocumentPath, "Challenge must retain the current instance and exact native page binding.");
        Check(first.DocumentPath != first.ScriptPath && first.DocumentHtml.Contains(first.ScriptPath, StringComparison.Ordinal), "Challenge document must use its own host-generated external resource.");
        Check(!first.DocumentHtml.Contains("<script>", StringComparison.Ordinal), "Inline code must not require weakening CSP.");
        try { RuntimeIsolationPolicy.CreateChallenge(Guid.Empty, Origin); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("An empty instance cannot own a startup challenge.");
    }

    private static void CompleteNativeResult()
    {
        var challenge = RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), Origin);
        var proof = RuntimeIsolationPolicy.ValidateNativeResult(challenge, NativeResult(Report(challenge)));
        Check(proof is not null && proof.InstanceId == challenge.InstanceId && proof.Origin == challenge.Origin, "Complete evidence must produce only its bound host proof.");
    }

    private static void ChallengeReplay()
    {
        var first = RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), Origin);
        string result = NativeResult(Report(first));
        Check(RuntimeIsolationPolicy.ValidateNativeResult(RuntimeIsolationPolicy.CreateChallenge(first.InstanceId, Origin), result) is null, "A prior challenge for the same instance cannot satisfy a fresh challenge.");
        Check(RuntimeIsolationPolicy.ValidateNativeResult(RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), Origin), result) is null, "A different runtime instance cannot inherit an earlier proof.");
    }

    private static void OriginReplay()
    {
        var first = RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), Origin);
        var second = RuntimeIsolationPolicy.CreateChallenge(first.InstanceId, "https://different.autumn.invalid/");
        var replay = Report(first);
        replay["nonce"] = second.Nonce;
        Check(RuntimeIsolationPolicy.ValidateNativeResult(second, NativeResult(replay)) is null, "Even matching instance and nonce cannot replace the actual origin.");
    }

    private static void RejectUntrustedEnvelopes()
    {
        var challenge = RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), Origin);
        foreach (string? value in new[] { null, "", "null", "undefined", "true", "{}", "[]", "\"null\"", "\"pending\"", "\"{broken\"", Report(challenge).ToJsonString(), JsonSerializer.Serialize(JsonSerializer.Serialize(Report(challenge).ToJsonString())) })
            Check(RuntimeIsolationPolicy.ValidateNativeResult(challenge, value) is null, "Pending, malformed, raw message, and nested fake envelope must fail closed.");
        Check(RuntimeIsolationPolicy.ValidateNativeResult(challenge, NativeResult(Report(challenge)) + " true") is null, "Trailing payload must be rejected.");
    }

    private static void RejectDuplicateProperties()
    {
        var challenge = RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), Origin);
        string valid = Report(challenge).ToJsonString();
        foreach (string malformed in new[]
        {
            valid.Replace("\"completed\":true", "\"completed\":false,\"completed\":true", StringComparison.Ordinal),
            valid.Replace("\"completed\":true", "\"completed\":true,\"completed\":true", StringComparison.Ordinal),
            valid.Replace("\"candidates\":0", "\"candidates\":1,\"candidates\":0", StringComparison.Ordinal),
            valid.Replace("\"completed\":true", "\"COMPLETED\":true,\"completed\":true", StringComparison.Ordinal)
        })
            Check(RuntimeIsolationPolicy.ValidateNativeResult(challenge, JsonSerializer.Serialize(malformed)) is null, "Duplicate or case-aliased fields must fail closed instead of last-property-wins parsing.");
    }

    private static void ByteLimit()
    {
        var challenge = RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), Origin);
        string result = NativeResult(Report(challenge));
        string padding = new(' ', RuntimeIsolationPolicy.MaximumNativeResultBytes);
        Check(RuntimeIsolationPolicy.ValidateNativeResult(challenge, result + padding) is null, "Raw outer JSON byte budget must be enforced.");
        string unicode = "\"" + new string('\u4e2d', RuntimeIsolationPolicy.MaximumNativeResultBytes / 2) + "\"";
        Check(unicode.Length < RuntimeIsolationPolicy.MaximumNativeResultBytes && Encoding.UTF8.GetByteCount(unicode) > RuntimeIsolationPolicy.MaximumNativeResultBytes, "Fixture must exercise bytes rather than UTF-16 length.");
        Check(RuntimeIsolationPolicy.ValidateNativeResult(challenge, unicode) is null, "Non-ASCII input cannot bypass the UTF-8 byte budget.");
    }

    private static void ChallengeOwnership() => AssertHostOwned(typeof(RuntimeIsolationChallenge));
    private static void ProofOwnership() => AssertHostOwned(typeof(RuntimeIsolationProof));
    private static void AssertHostOwned(Type type)
    {
        Check(type.IsSealed && type.GetConstructors().Length == 0, "External code must not construct or subclass proof/challenge types.");
        Check(type.GetProperties().All(property => property.SetMethod is null || !property.SetMethod.IsPublic), "External callers must not mutate a validated binding.");
    }

    private static void RejectMutation(Action<JsonObject> mutate)
    {
        // Use a fixed lower-case identity containing letters for case-normalization attack cases.
        var challenge = RuntimeIsolationPolicy.CreateChallenge(Guid.Parse("abcdef01-2345-4678-9012-abcdef012345"), Origin);
        var report = Report(challenge);
        mutate(report);
        Check(RuntimeIsolationPolicy.ValidateNativeResult(challenge, NativeResult(report)) is null, "Incomplete, changed, or truthy evidence cannot enable application execution.");
    }

    private static JsonObject Report(RuntimeIsolationChallenge challenge) => new()
    {
        ["protocolVersion"] = 1, ["nonce"] = challenge.Nonce, ["instanceId"] = challenge.InstanceId.ToString("D"),
        ["origin"] = challenge.Origin, ["completed"] = true, ["secureContext"] = true,
        ["probes"] = new JsonArray(Peer("peer-1"), Peer("peer-2"))
    };
    private static JsonObject Peer(string label) => new()
    {
        ["label"] = label, ["nativeConstructor"] = true, ["filteredIceServers"] = true, ["relayOnly"] = true,
        ["iceConnectionState"] = "failed", ["connectionState"] = "failed", ["candidates"] = 0,
        ["restartedIceConnectionState"] = "failed"
    };
    private static string NativeResult(JsonObject report) => JsonSerializer.Serialize(report.ToJsonString());
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
