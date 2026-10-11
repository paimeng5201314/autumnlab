using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Runtime;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

/// <summary>Exercises the same account-bound data adapter that the native host wires into RuntimeSession.</summary>
internal static class DataBridgeTests
{
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 };
    private static readonly string LongRequestId = new('r', 64);

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("bridge.data.chinese_7000_characters_round_trip", ChineseRoundTrip);
        yield return ("bridge.data.emoji_controls_and_html_remain_literal_data", EscapedTextRoundTrip);
        yield return ("bridge.data.maximum_message_depth_round_trip", MaximumDepth);
        yield return ("bridge.saves.exact_response_budget_and_one_byte_over", () => ResponseBoundary("saves"));
        yield return ("bridge.preferences.exact_response_budget_and_one_byte_over", () => ResponseBoundary("preferences"));
        yield return ("bridge.data.surrogate_expansion_rejected_before_commit", SurrogateExpansion);
        yield return ("bridge.storage.maximum_20kib_write_can_be_read", PrivateFileBoundary);
        yield return ("bridge.preferences.private_storage_cannot_overwrite_or_delete_preferences", PreferenceIsolation);
        yield return ("bridge.preferences.logical_key_contract_is_preserved", PreferenceKeys);
        yield return ("bridge.data.cancellation_after_staging_preserves_committed_bytes", CancelBeforeCommit);
        yield return ("bridge.data.timeout_after_staging_preserves_committed_bytes", TimeoutBeforeCommit);
        yield return ("bridge.data.oversized_existing_values_are_preserved", ExistingOversizedValues);
        yield return ("bridge.guest.exact_response_budget_and_one_byte_over", GuestBoundary);
        yield return ("bridge.guest.oversized_existing_value_returns_bounded_error", GuestOversizedRead);
    }

    private static void ChineseRoundTrip() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        string text = new('秋', 7000);
        foreach (string capability in new[] { "saves", "preferences" })
        {
            Ok(Write(fixture.Session, capability, text));
            string requestId = NextLongRequestId();
            string response = Read(fixture.Session, capability, requestId);
            Equal(Ok(response).GetProperty("value").GetString(), text);
            Equal(RequestId(response), requestId);
            Assert(Encoding.UTF8.GetByteCount(response) <= 32768, "Chinese response exceeds the SDK message limit.");
        }
    });

    private static void EscapedTextRoundTrip() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        string[] values = [string.Concat(Enumerable.Repeat("🙂🚀", 600)), new string('\0', 4000),
            "秋天\n\r\t\b\f\0\\\" <script>alert('文本')</script> & </script> \u2028\u2029"];
        foreach (string capability in new[] { "saves", "preferences" })
            foreach (string value in values)
            {
                Ok(Write(fixture.Session, capability, value));
                Equal(Ok(Read(fixture.Session, capability)).GetProperty("value").GetString(), value);
            }
    });

    private static void MaximumDepth() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        // The request and result wrappers each add two levels to a 30-level value.
        using var acceptedDocument = JsonDocument.Parse(new string('[', 30) + "\"深度🙂\"" + new string(']', 30));
        using var rejectedDocument = JsonDocument.Parse(new string('[', 31) + "0" + new string(']', 31));
        JsonElement accepted = acceptedDocument.RootElement;
        JsonElement rejected = rejectedDocument.RootElement;
        foreach (string capability in new[] { "saves", "preferences" })
        {
            Ok(Write(fixture.Session, capability, accepted));
            JsonElement value = Ok(Read(fixture.Session, capability, NextLongRequestId())).GetProperty("value");
            for (int level = 0; level < 30; level++) { Equal(value.GetArrayLength(), 1); value = value[0]; }
            Equal(value.GetString(), "深度🙂");
            var before = Snapshot(root);
            Error(Write(fixture.Session, capability, rejected), "INVALID_REQUEST");
            Unchanged(root, before);
        }
    });

    private static void ResponseBoundary(string capability) => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        Ok(Write(fixture.Session, capability, "previous"));
        string value = MaximumString();
        Ok(Write(fixture.Session, capability, value));
        string response = Read(fixture.Session, capability, LongRequestId);
        Equal(Encoding.UTF8.GetByteCount(response), 32768);
        Equal(RequestId(response), LongRequestId);
        Equal(Ok(response).GetProperty("value").GetString(), value);
        var before = Snapshot(root);
        Error(Write(fixture.Session, capability, value + "x"), "RESPONSE_TOO_LARGE");
        Unchanged(root, before);
        Equal(Ok(Read(fixture.Session, capability)).GetProperty("value").GetString(), value);
    });

    private static void SurrogateExpansion() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        string emoji = string.Concat(Enumerable.Repeat("🙂", 3000));
        foreach (string capability in new[] { "saves", "preferences" })
        {
            Ok(Write(fixture.Session, capability, "previous"));
            var before = Snapshot(root);
            string method = capability == "saves" ? "saves.write" : "preferences.set";
            string key = capability == "saves" ? "slot" : "key";
            // JSON.stringify emits the literal supplementary character. The .NET JSON response
            // encoder escapes its surrogate pair, so a small request can produce an oversized reply.
            string request = "{\"protocolVersion\":1,\"requestId\":\"unicode-" + capability + "\",\"method\":\"" + method
                + "\",\"params\":{\"" + key + "\":\"game\",\"value\":\"" + emoji + "\"}}";
            Assert(Encoding.UTF8.GetByteCount(request) < 32768, "Regression request must reach the adapter.");
            Error(SendRaw(fixture.Session, request), "RESPONSE_TOO_LARGE");
            Unchanged(root, before);
        }
    });

    private static void PrivateFileBoundary() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        byte[] bytes = Enumerable.Range(0, 20 * 1024).Select(value => (byte)value).ToArray();
        Ok(Send(fixture.Session, "storage.write", new { key = "binary", data = Convert.ToBase64String(bytes) }));
        string response = Send(fixture.Session, "storage.read", new { key = "binary" }, LongRequestId);
        JsonElement result = Ok(response);
        Assert(result.GetProperty("exists").GetBoolean(), "Stored file disappeared.");
        Equal(result.GetProperty("encoding").GetString(), "base64");
        Assert(Convert.FromBase64String(result.GetProperty("data").GetString()!).SequenceEqual(bytes), "20 KiB binary contents changed.");
        Assert(Encoding.UTF8.GetByteCount(response) <= 32768, "20 KiB file must fit in a read reply.");
        var before = Snapshot(root);
        Error(Send(fixture.Session, "storage.write", new { key = "binary", data = Convert.ToBase64String(new byte[20 * 1024 + 1]) }), "FILE_TOO_LARGE");
        Unchanged(root, before);
        Assert(fixture.Store.ReadPrivate("binary").Value!.SequenceEqual(bytes), "Rejected write changed private bytes.");
    });

    private static void PreferenceIsolation() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        Ok(Send(fixture.Session, "preferences.set", new { key = "theme", value = "dark" }));
        Ok(Send(fixture.Session, "storage.write", new { key = "pref_theme", data = Convert.ToBase64String("not JSON"u8) }));
        Equal(Ok(Send(fixture.Session, "preferences.get", new { key = "theme" })).GetProperty("value").GetString(), "dark");
        Ok(Send(fixture.Session, "preferences.set", new { key = "theme", value = "light" }));
        Equal(Encoding.UTF8.GetString(Convert.FromBase64String(Ok(Send(fixture.Session, "storage.read", new { key = "pref_theme" })).GetProperty("data").GetString()!)), "not JSON");
        Ok(Send(fixture.Session, "storage.delete", new { key = "pref_theme" }));
        Equal(Ok(Send(fixture.Session, "preferences.get", new { key = "theme" })).GetProperty("value").GetString(), "light");
    });

    private static void PreferenceKeys() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        foreach (string key in new[] { "CON", "LPT1", new string('k', 59) })
        {
            Ok(Send(fixture.Session, "preferences.set", new { key, value = key }));
            Equal(Ok(Send(fixture.Session, "preferences.get", new { key })).GetProperty("value").GetString(), key);
        }
        var before = Snapshot(root);
        foreach (string key in new[] { "", new string('k', 60), "../theme", "a/b" })
            Error(Send(fixture.Session, "preferences.set", new { key, value = "rejected" }), "INVALID_PARAMS");
        Unchanged(root, before);
    });

    private static void CancelBeforeCommit()
    {
        foreach (string capability in new[] { "saves", "preferences", "storage" }) InTemp(root =>
        {
            using var cancellation = new CancellationTokenSource();
            bool armed = false, reached = false;
            using var fixture = new BridgeFixture(root, hooks: new(point =>
            {
                if (armed && point == StorageWritePoint.BeforeCommit) { reached = true; cancellation.Cancel(); }
            }));
            Ok(Write(fixture.Session, capability, "previous"));
            var before = Snapshot(root); armed = true;
            Error(Write(fixture.Session, capability, "cancelled", cancellation.Token), "USER_CANCELLED");
            Assert(reached, "Cancellation must happen after staging, at the real commit boundary.");
            Unchanged(root, before);
        });
    }

    private static void TimeoutBeforeCommit() => InTemp(root =>
    {
        bool armed = false, reached = false; BridgeFixture? fixture = null;
        using (fixture = new BridgeFixture(root, timeout: TimeSpan.FromSeconds(1), hooks: new(point =>
        {
            if (!armed || point != StorageWritePoint.BeforeCommit) return;
            reached = true;
            Assert(fixture!.ActiveToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)), "Runtime timeout did not cancel the staged operation.");
        })))
        {
            Ok(Write(fixture.Session, "saves", "previous"));
            var before = Snapshot(root); armed = true;
            Error(Write(fixture.Session, "saves", "timed out"), "REQUEST_TIMEOUT");
            Assert(reached, "Timeout must be observed at the real commit boundary.");
            Unchanged(root, before);
        }
    });

    private static void ExistingOversizedValues() => InTemp(root =>
    {
        using var fixture = new BridgeFixture(root);
        JsonElement value = JsonSerializer.SerializeToElement(new string('x', 40_000));
        Assert(fixture.Store.WriteSave("game", value).Success, "Seed the supported on-disk save format.");
        Assert(fixture.Store.WritePreference("game", value).Success, "Seed the supported on-disk preference format.");
        var before = Snapshot(root);
        foreach (string capability in new[] { "saves", "preferences" })
        {
            string response = Read(fixture.Session, capability, NextLongRequestId());
            Error(response, "RESPONSE_TOO_LARGE");
            Assert(Encoding.UTF8.GetByteCount(response) <= 32768, "Error reply must fit the bridge limit.");
        }
        Unchanged(root, before);
    });

    private static void GuestBoundary() => InTemp(root => WithGuest(root, session =>
    {
        Ok(Write(session, "saves", new string('秋', 7000)));
        Equal(Ok(Read(session, "saves")).GetProperty("value").GetString(), new string('秋', 7000));
        string value = MaximumString();
        Ok(Write(session, "saves", value));
        string response = Read(session, "saves", LongRequestId);
        Equal(Encoding.UTF8.GetByteCount(response), 32768);
        Equal(Ok(response).GetProperty("value").GetString(), value);
        var before = Snapshot(root);
        Error(Write(session, "saves", value + "x"), "RESPONSE_TOO_LARGE");
        Unchanged(root, before);
    }));

    private static void GuestOversizedRead() => InTemp(root => WithGuest(root, session =>
    {
        string directory = Path.Combine(root.Directories["Saves"], "test.bridge", "guest");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "game.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, appId = "test.bridge", accountMode = "guest", slot = "game", value = new string('x', 40_000)
        }));
        var before = Snapshot(root);
        string response = Read(session, "saves", LongRequestId);
        Error(response, "RESPONSE_TOO_LARGE");
        Assert(Encoding.UTF8.GetByteCount(response) <= 32768, "Guest error reply must fit the bridge limit.");
        Unchanged(root, before);
    }));

    private static void WithGuest(InstallationRoot root, Action<RuntimeSession> action)
    {
        var session = new RuntimeSession("test.bridge", ["saves"], root.Directories["Saves"]); session.Foreground();
        try { Ok(Send(session, "permissions.request", new { name = "saves" })); action(session); }
        finally { session.Close(); }
    }

    private static string MaximumString()
    {
        int overhead = JsonSerializer.SerializeToUtf8Bytes(new { requestId = LongRequestId, ok = true, result = new { exists = true, value = "" } }, WireJson).Length;
        return new string('x', 32768 - overhead);
    }

    private static int nextRequest;
    private static string NextLongRequestId() => Interlocked.Increment(ref nextRequest).ToString().PadLeft(64, 'r');
    private static string Write(RuntimeSession session, string capability, object value, CancellationToken token = default) => capability switch
    {
        "saves" => Send(session, "saves.write", new { slot = "game", value }, token: token),
        "preferences" => Send(session, "preferences.set", new { key = "game", value }, token: token),
        _ => Send(session, "storage.write", new { key = "game", data = Convert.ToBase64String(Encoding.UTF8.GetBytes((string)value)) }, token: token)
    };
    private static string Read(RuntimeSession session, string capability, string? requestId = null) => capability == "saves"
        ? Send(session, "saves.read", new { slot = "game" }, requestId)
        : Send(session, "preferences.get", new { key = "game" }, requestId);
    private static string Send(RuntimeSession session, string method, object parameters, string? requestId = null, CancellationToken token = default) =>
        SendRaw(session, JsonSerializer.Serialize(new { protocolVersion = 1, requestId = requestId ?? Interlocked.Increment(ref nextRequest).ToString(), method, @params = parameters }, WireJson), token);
    private static string SendRaw(RuntimeSession session, string request, CancellationToken token = default) =>
        session.HandleMessageAsync(session.PageUri, request, (_, _) => Task.FromResult(true), token).GetAwaiter().GetResult();
    private static string? RequestId(string response) { using var document = JsonDocument.Parse(response); return document.RootElement.GetProperty("requestId").GetString(); }
    private static JsonElement Ok(string response)
    {
        using var document = JsonDocument.Parse(response);
        Assert(document.RootElement.GetProperty("ok").GetBoolean(), response);
        return document.RootElement.GetProperty("result").Clone();
    }
    private static void Error(string response, string code)
    {
        using var document = JsonDocument.Parse(response);
        Assert(!document.RootElement.GetProperty("ok").GetBoolean(), "Expected " + code + ", received a successful response.");
        Equal(document.RootElement.GetProperty("error").GetProperty("code").GetString(), code);
    }
    private static Dictionary<string, byte[]> Snapshot(InstallationRoot root) => Directory.EnumerateFiles(root.DataDirectory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root.DataDirectory, path), File.ReadAllBytes, StringComparer.Ordinal);
    private static void Unchanged(InstallationRoot root, Dictionary<string, byte[]> before)
    {
        var after = Snapshot(root);
        Assert(before.Keys.Order(StringComparer.Ordinal).SequenceEqual(after.Keys.Order(StringComparer.Ordinal)), "Rejected operation added or removed persistent files.");
        foreach (var item in before) Assert(after[item.Key].SequenceEqual(item.Value), "Rejected operation changed persistent bytes: " + item.Key);
    }
    private static void Equal<T>(T actual, T expected) => Assert(EqualityComparer<T>.Default.Equals(actual, expected), $"Expected {expected}, got {actual}.");
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void InTemp(Action<InstallationRoot> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AutumnOS-data-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { var root = new InstallationRoot(directory); Assert(root.EnsureCreated().Success, "Temporary data root failed."); action(root); }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class BridgeFixture : IDisposable
    {
        internal RuntimeSession Session { get; }
        internal AccountDataStore Store { get; }
        internal CancellationToken ActiveToken { get; private set; }

        internal BridgeFixture(InstallationRoot root, StorageFaultHooks? hooks = null, TimeSpan? timeout = null)
        {
            var application = new RuntimeApplication(new("test.bridge", 123, null), "local-package:bridge-regression", "桥接测试", ["saves", "storage"]);
            var account = new RuntimeAccountContext("account-bridge", 1, false, () => true, default);
            var permissions = new PermissionService(root.Directories["Config"]);
            var services = new RuntimeSessionServices
            {
                Account = account, Application = application, Permissions = permissions,
                RequestPermission = (_, _) => Task.FromResult(true),
                DataRequest = (method, parameters, token) =>
                {
                    ActiveToken = token;
                    return Task.FromResult(AccountDataRequestHandler.Handle(Store!, method, parameters, token));
                }
            };
            Session = new RuntimeSession(application.Identity.AppId, application.DeclaredPermissions, root.Directories["Saves"], services: services, requestTimeout: timeout);
            var scope = new StorageScope(application.Identity, account.AccountKey, Session.Instance.Id, Session.Instance.Epoch, application.Source);
            Store = new AccountDataStore(root, scope, _ => account.IsCurrent(), Session.EnterCommitLease, testHooks: hooks);
            permissions.SetDecision(account, application, "saves", PermissionDecision.Granted);
            permissions.SetDecision(account, application, "storage", PermissionDecision.Granted);
            Session.Foreground();
        }
        public void Dispose() => Session.Close();
    }
}
