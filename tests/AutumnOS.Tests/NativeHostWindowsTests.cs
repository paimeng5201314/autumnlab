using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

// Memory-only bootstrap negatives and fresh owned-file manifest checks. Directory
// pin checks use Windows file metadata APIs only. No process, USER object, profile,
// LSA, browser, clipboard, SDK native post, or actual OS isolation acceptance.
internal static class NativeHostWindowsTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ManifestHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Native host binding rejects forged authority and permits terminal channel value", BindingShape);
        yield return Case("Native host bootstrap roundtrips only finite facts with a bound controller PID and no retirement opcode", BootstrapRoundTrip);
        yield return Case("Native host bootstrap rejects nonce manifest PID and creation-time substitution", BootstrapBindings);
        yield return Case("Native host bootstrap never accepts a truncated original-object fact", BootstrapTruncation);
        yield return Case("Native host bootstrap rejects unknown kind reserved data and zero job", BootstrapShape);
        yield return ("Native host strict manifest accepts complete pinned bytes on its actual fixed local volume", ManifestComplete);
        yield return ("Native host strict manifest rejects duplicate unknown and missing fields", ManifestFields);
        yield return ("Native host strict manifest rejects payload hash length and complete-set mismatch", ManifestByteSet);
        yield return ("Native host strict manifest rejects traversal ADS device and duplicate relative paths", ManifestPaths);
        yield return ("Native host manifest keeps read pins until explicit dispose and never claims peer evidence", ManifestPins);
        yield return ("Native host strict manifest rejects authority mismatch overlapping roots and oversized input", ManifestBindingAndBounds);
        yield return ("Native host pin release fault remains sticky across repeated dispose and never reports released", PinReleaseFault);
        yield return ("Native host pin release reentrancy cannot turn a partial release into no-op success", PinReleaseReentrant);
        yield return Case("Native host close during held creation cannot retire zero resources or abandon original ownership", CreationCloseInterleaving);
    }
    private static (string, Action) Case(string name, Func<Task> run) => (name, () => run().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());
    private static NativeHostBinding Binding() => new(Guid.NewGuid(), "owned-fixture-account", 3, new string('a', 64), Guid.NewGuid(), 4, Guid.NewGuid(), 5, 6);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("NATIVE_HOST_PURE_OR_OWNED_FILE_ASSERTION"); }
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private static void BindingShape()
    {
        var binding = Binding(); binding.Validate(); (binding with { ChannelGeneration = long.MaxValue }).Validate();
        foreach (var changed in new[] { binding with { AuthorityId = Guid.Empty }, binding with { Account = "bad\naccount" },
            binding with { ApplicationBinding = new string('A', 64) }, binding with { InstanceId = Guid.Empty },
            binding with { DocumentId = Guid.Empty }, binding with { ChannelGeneration = 0 }, binding with { DocumentGeneration = 0 },
            binding with { AccountEpoch = -1 }, binding with { InstanceEpoch = -1 } }) Reject<ArgumentException>(changed.Validate);
    }
    private static async Task<byte[]> Frame(NativeBootstrapFact fact)
    {
        using var stream = new MemoryStream();
        await NativeHostBootstrapProtocol.WriteAsync(stream, Nonce, ManifestHash, fact);
        return stream.ToArray();
    }
    private static Task<NativeBootstrapFact> Read(byte[] bytes, string nonce = Nonce, string manifest = ManifestHash, uint pid = 123, ulong creation = 456)
        => NativeHostBootstrapProtocol.ReadAsync(new MemoryStream(bytes, false), nonce, manifest, pid, creation);
    private static async Task BootstrapRoundTrip()
    {
        foreach (var fact in new[] { new NativeBootstrapFact(NativeBootstrapKind.JobReady, 123, 456, 789),
            new NativeBootstrapFact(NativeBootstrapKind.BrowserAdapterUnsupported, 123, 456, 0),
            new NativeBootstrapFact(NativeBootstrapKind.QueryJobBound, 123, 456, 0),
            new NativeBootstrapFact(NativeBootstrapKind.ContextCreated, 123, 456, 0),
            new NativeBootstrapFact(NativeBootstrapKind.ContextBound, 123, 456, 0),
            new NativeBootstrapFact(NativeBootstrapKind.RestrictedRootCreated, 123, 456, 234),
            new NativeBootstrapFact(NativeBootstrapKind.RestrictedRootBound, 123, 456, 0),
            new NativeBootstrapFact(NativeBootstrapKind.BrowserControllerReady, 123, 456, 235),
            new NativeBootstrapFact(NativeBootstrapKind.ProfileCreated, 123, 456, 0),
            new NativeBootstrapFact(NativeBootstrapKind.ProfileBound, 123, 456, 0) })
            Check(await Read(await Frame(fact)) == fact);
        Check(Enum.GetValues<NativeBootstrapKind>().Length == 10);
        foreach (var kind in new[] { NativeBootstrapKind.RestrictedRootCreated, NativeBootstrapKind.BrowserControllerReady })
        {
            await RejectAsync<InvalidDataException>(() => Frame(new(kind, 123, 456, 0)));
            await RejectAsync<InvalidDataException>(() => Frame(new(kind, 123, 456, (ulong)uint.MaxValue + 1)));
        }
    }
    private static async Task BootstrapBindings()
    {
        byte[] bytes = await Frame(new(NativeBootstrapKind.JobReady, 123, 456, 789));
        await RejectAsync<InvalidDataException>(() => Read(bytes, nonce: new string('0', 64)));
        await RejectAsync<InvalidDataException>(() => Read(bytes, manifest: new string('0', 64)));
        await RejectAsync<InvalidDataException>(() => Read(bytes, pid: 124));
        await RejectAsync<InvalidDataException>(() => Read(bytes, creation: 457));
        await RejectAsync<InvalidDataException>(() => Read(bytes, pid: 0));
        await RejectAsync<InvalidDataException>(() => Read(bytes, creation: 0));
        await RejectAsync<InvalidDataException>(() => Read(bytes, nonce: "not-a-nonce"));
    }
    private static async Task BootstrapTruncation()
    {
        byte[] bytes = await Frame(new(NativeBootstrapKind.JobReady, 123, 456, 789));
        for (int length = 0; length < bytes.Length; length++)
            await RejectAsync<EndOfStreamException>(() => Read(bytes[..length]));
    }
    private static async Task BootstrapShape()
    {
        byte[] original = await Frame(new(NativeBootstrapKind.JobReady, 123, 456, 789));
        byte[] unknown = (byte[])original.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(6), 65535);
        byte[] reserved = (byte[])original.Clone(); reserved[95] = 1;
        byte[] zeroJob = (byte[])original.Clone(); zeroJob.AsSpan(84, 8).Clear();
        foreach (byte[] invalid in new[] { unknown, reserved, zeroJob }) await RejectAsync<InvalidDataException>(() => Read(invalid));
    }

    private static void ManifestComplete() => WithFixture(fixture =>
    {
        using var manifest = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding);
        Check(manifest.Binding == fixture.Binding && manifest.Nonce == Nonce && manifest.HostFiles.Count == 1 && manifest.RuntimeFiles.Count == 1 && manifest.PackageFiles.Count == 1);
        Check(manifest.SessionDirectory == fixture.Session && manifest.RuntimeVersion == "154.0.4258.53");
        Check(manifest.ManifestSha256 == Hash(File.ReadAllBytes(fixture.ManifestPath)));
        Check(manifest.ProcessAuditRoots == (fixture.Host, fixture.Runtime));
        Check(manifest.ProcessAuditRoots.Host != Path.Combine(fixture.Session, "host") && manifest.ProcessAuditRoots.Runtime != Path.Combine(fixture.Session, "runtime"));
    });
    private static void ManifestFields() => WithFixture(fixture =>
    {
        string good = fixture.Document.ToJsonString();
        foreach (string bad in new[] { good.Insert(1, "\"unexpected\":0,"), good.Insert(1, "\"schemaVersion\":1,"),
            good.Replace("\"parentProcessId\":123,", "", StringComparison.Ordinal) })
        {
            File.WriteAllText(fixture.ManifestPath, bad);
            Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
        }
        fixture.Document["binding"]!["unexpected"] = 1;
        fixture.Save();
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
    });
    private static void ManifestByteSet() => WithFixture(fixture =>
    {
        string body = Path.Combine(fixture.Package, "index.html"); byte[] original = File.ReadAllBytes(body);
        File.WriteAllText(body, "changed");
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
        File.WriteAllBytes(body, original); string extra = Path.Combine(fixture.Package, "unbound.js"); File.WriteAllText(extra, "unbound");
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
        File.Delete(extra); Directory.CreateDirectory(Path.Combine(fixture.Package, "unbound-empty-directory"));
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
    });
    private static void ManifestPaths() => WithFixture(fixture =>
    {
        foreach (string path in new[] { "../index.html", "nested/../index.html", "index.html:stream", "NUL.html", "index.html.", "/index.html" })
        {
            fixture.Document["packageFiles"]![0]!["path"] = path; fixture.Save();
            Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
        }
        fixture.Document["packageFiles"]![0]!["path"] = "index.html";
        ((JsonArray)fixture.Document["packageFiles"]!).Add(fixture.Document["packageFiles"]![0]!.DeepClone()); fixture.Save();
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
    });
    private static void ManifestPins() => WithFixture(fixture =>
    {
        string packageFile = Path.Combine(fixture.Package, "index.html");
        using (var manifest = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding))
        {
            Reject<IOException>(() => { using var denied = new FileStream(packageFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Reject<IOException>(() => File.Move(packageFile, packageFile + ".moved"));
            Check(manifest.ParentProcessId == 123 && manifest.ParentCreationFileTime == 456); // Parsed bindings only; not a real process.
        }
        using var nowWritable = new FileStream(packageFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    });
    private static void ManifestBindingAndBounds() => WithFixture(fixture =>
    {
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding with { InstanceId = Guid.NewGuid() }); });
        fixture.Document["runtimeDirectory"] = fixture.Host; fixture.Save();
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
        File.WriteAllBytes(fixture.ManifestPath, new byte[NativeHostLaunchManifest.MaximumManifestBytes + 1]);
        Reject<InvalidDataException>(() => { using var ignored = NativeHostLaunchManifest.LoadPinned(fixture.ManifestPath, fixture.Binding); });
    });
    private static void PinReleaseFault()
    {
        int first = 0, failed = 0, last = 0;
        var owned = new NativePinReleaseGroup(new IDisposable[]
        {
            new DisposeAction(() => first++),
            new DisposeAction(() => { failed++; if (failed == 1) throw new IOException("owned synthetic release fault"); }),
            new DisposeAction(() => last++)
        });
        Reject<AggregateException>(owned.Dispose);
        Check(first == 1 && failed == 1 && last == 1 && !owned.Released);
        // The injected disposable would succeed on its second call. The group
        // must retain uncertainty instead of laundering it through a retry/no-op.
        Reject<AggregateException>(owned.Dispose);
        Reject<AggregateException>(owned.Dispose);
        Check(first == 1 && failed == 1 && last == 1 && !owned.Released);
    }
    private static void PinReleaseReentrant()
    {
        NativePinReleaseGroup? owned = null; int calls = 0;
        owned = new(new[] { new DisposeAction(() => { calls++; owned!.Dispose(); }) });
        Reject<AggregateException>(owned.Dispose);
        Reject<AggregateException>(owned.Dispose);
        Check(!owned.Released && calls == 1);
        var normal = new NativePinReleaseGroup(Array.Empty<IDisposable>());
        normal.Dispose(); normal.Dispose(); Check(normal.Released);
    }
    private static async Task CreationCloseInterleaving()
    {
        var closeFirst = new NativeCreationOwnership();
        closeFirst.RequestClose();
        Check(!closeFirst.TryClaimCreation() && closeFirst.CanRetireNeverCreated && !closeFirst.CreationOwned);

        var creationFirst = new NativeCreationOwnership();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task factory = Task.Run(async () =>
        {
            Check(creationFirst.TryClaimCreation()); entered.TrySetResult();
            await resumeFactory.Task;
            creationFirst.CompleteCreation(nativeCallAttempted: true, actualNoChildCreatedProven: true);
        });
        try
        {
            await entered.Task;
            creationFirst.RequestClose();
            Check(creationFirst.CloseRequested && creationFirst.CreationOwned && !creationFirst.CanRetireNeverCreated);
            Reject<InvalidOperationException>(() => creationFirst.TryClaimCreation());
        }
        finally { resumeFactory.TrySetResult(); await factory; }
        Check(!creationFirst.CreationOwned && creationFirst.CanRetireNeverCreated);
        Reject<InvalidOperationException>(() => creationFirst.CompleteCreation(nativeCallAttempted: true, actualNoChildCreatedProven: true));

        // A real factory's uncertain/created-original result cannot be replaced
        // by Close or by a second completion claiming no child. These flags
        // are a pure fixture, not native retirement evidence.
        var originalOwned = new NativeCreationOwnership();
        Check(originalOwned.TryClaimCreation()); originalOwned.RequestClose();
        originalOwned.CompleteCreation(nativeCallAttempted: true, actualNoChildCreatedProven: false);
        Check(!originalOwned.CreationOwned && !originalOwned.CanRetireNeverCreated);
        Reject<InvalidOperationException>(() => originalOwned.CompleteCreation(nativeCallAttempted: true, actualNoChildCreatedProven: true));
        Check(!originalOwned.CanRetireNeverCreated);

        // Pipe/fixed-input failure after claiming creation but before the OS
        // factory was ever called has a different, definitive zero-child fact.
        var failedBeforeNativeCall = new NativeCreationOwnership();
        Check(failedBeforeNativeCall.TryClaimCreation()); failedBeforeNativeCall.RequestClose();
        Check(!failedBeforeNativeCall.CanRetireNeverCreated);
        failedBeforeNativeCall.CompleteCreation(nativeCallAttempted: false, actualNoChildCreatedProven: false);
        Check(failedBeforeNativeCall.CanRetireNeverCreated);
    }
    private sealed class DisposeAction(Action action) : IDisposable { public void Dispose() => action(); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void WithFixture(Action<Fixture> test)
    {
        using var fixture = new Fixture(); test(fixture);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "autumnos-native-manifest-" + Guid.NewGuid().ToString("N"));
        internal NativeHostBinding Binding { get; } = NativeHostWindowsTests.Binding();
        internal string Host { get; }
        internal string Package { get; }
        internal string Runtime { get; }
        internal string Session { get; }
        internal string ManifestPath { get; }
        internal JsonObject Document { get; }
        internal Fixture()
        {
            Host = Path.Combine(root, "host-source"); Package = Path.Combine(root, "package-source"); Session = Path.Combine(root, "session");
            Runtime = Path.Combine(root, "runtime-source"); string parent = Path.Combine(root, "parent.exe");
            foreach (string directory in new[] { Host, Package, Session, Runtime }) Directory.CreateDirectory(directory);
            File.WriteAllText(parent, "owned fake parent image; never executed");
            byte[] host = Encoding.UTF8.GetBytes("owned fake apphost; never executed"), engine = Encoding.UTF8.GetBytes("owned fake runtime; never executed"), html = Encoding.UTF8.GetBytes("<p>owned fixture</p>");
            File.WriteAllBytes(Path.Combine(Host, NativeHostLaunchManifest.RequiredHostExecutable), host);
            File.WriteAllBytes(Path.Combine(Runtime, "msedgewebview2.exe"), engine); File.WriteAllBytes(Path.Combine(Package, "index.html"), html);
            Document = new JsonObject
            {
                ["schemaVersion"] = 1, ["binding"] = JsonSerializer.SerializeToNode(Binding), ["nonce"] = Nonce,
                ["sessionDirectory"] = Session, ["hostDirectory"] = Host, ["hostExecutable"] = NativeHostLaunchManifest.RequiredHostExecutable,
                ["runtimeDirectory"] = Runtime, ["runtimeVersion"] = NativeHostLaunchManifest.RequiredRuntimeVersion,
                ["packageDirectory"] = Package, ["packageEntry"] = "index.html", ["parentProcessId"] = 123,
                ["parentCreationFileTime"] = 456, ["parentExecutable"] = parent, ["parentExecutableSha256"] = Hash(File.ReadAllBytes(parent)),
                ["hostFiles"] = Bind(NativeHostLaunchManifest.RequiredHostExecutable, host), ["runtimeFiles"] = Bind("msedgewebview2.exe", engine), ["packageFiles"] = Bind("index.html", html)
            };
            ManifestPath = Path.Combine(Session, "launch.json"); Save();
        }
        private static JsonArray Bind(string path, byte[] bytes) => new(new JsonObject { ["path"] = path, ["bytes"] = bytes.Length, ["sha256"] = Hash(bytes) });
        internal void Save() => File.WriteAllText(ManifestPath, Document.ToJsonString());
        public void Dispose()
        {
            if (!Directory.Exists(root)) return;
            string target = Path.GetFullPath(root);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!target.StartsWith(temporaryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target), "\\Aautumnos-native-manifest-[0-9a-f]{32}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("NATIVE_HOST_OWNED_FIXTURE_DELETE_GUARD");
            Directory.Delete(target, recursive: true);
        }
    }
}
