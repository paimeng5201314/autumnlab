using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.NativeHost.Windows;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Synthetic dispatcher and streams; real .autumn execution and Win10/11
// acceptance must be verified through the actual native host independently.
internal static class BrowserRoleProtocolTests
{
    private static readonly string Nonce = new('a', 64);
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("bounded startup descriptor rejects truncation and extra properties", DescriptorFraming);
        yield return Case("private descriptor rejects duplicate nonce, traversal and device paths", DescriptorFields);
        yield return Case("verified resource image rejects changed bytes before any package navigation", ChangedPackage);
        yield return Case("identical bytes in a different file cannot substitute an admitted file identity", DifferentFileIdentity);
        yield return Case("each volume and file-index identity component is enforced", TamperedFileIdentity);
        yield return Case("a hard link refers to the same admitted file object", HardLinkIdentity);
        yield return Case("an admitted file identity still requires the declared length", DeclaredLength);
        yield return Case("a fence before parser release advances the native delivery channel", FenceBeforeRelease);
        yield return Case("unreleased package cannot receive a native Post", BeforeRelease);
        yield return Case("a second nested frame cannot hide after a valid delivery", TrailingBytes);
        yield return Case("lost document yields an explicit before-Post rejection", LostDocument);
        yield return Case("a Post exception after native entry cannot produce a receipt", UncertainPost);
    }
    private static (string, Action) Case(string name, Func<Task> test) =>
        ("Native browser " + name, () => test().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());
    private static NativeHostBinding Original() => new(Guid.NewGuid(), "fixture", 1, new string('c', 64),
        Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
    private static NativeBrowserStartup Descriptor(string root, byte[] bytes, NativeHostFileIdentity? identity = null) => new(Nonce, Original(), root, root, "index.html",
        [new("index.html", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), identity ?? new(1, 0, 1))]);
    private static MemoryStream Packet(byte[] bytes)
    {
        var result = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)bytes.Length));
        result.Write(header); result.Write(bytes); result.Position = 0;
        return result;
    }
    private static async Task Reject<T>(Func<Task> test) where T : Exception
    {
        try { await test(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("BROWSER_ROLE_TEST_ASSERTION"); }
    private static async Task DescriptorFraming()
    {
        using var shortFrame = new MemoryStream([50,0,0,0,123]);
        await Reject<EndOfStreamException>(() => NativeBrowserStartup.ReadAsync(shortFrame, Nonce));
        using var hugeFrame = new MemoryStream([0,0,4,0]);
        await Reject<InvalidDataException>(() => NativeBrowserStartup.ReadAsync(hugeFrame, Nonce));
        string fixture = JsonSerializer.Serialize(Descriptor(Path.GetTempPath().TrimEnd('\\'), "A"u8.ToArray()));
        using var extra = Packet(Encoding.UTF8.GetBytes(fixture.Insert(1, "\"RunCommand\":true,")));
        await Reject<JsonException>(() => NativeBrowserStartup.ReadAsync(extra, Nonce));
    }
    private static async Task DescriptorFields()
    {
        var fixture = Descriptor(Path.GetTempPath().TrimEnd('\\'), "A"u8.ToArray());
        using var good = Packet(JsonSerializer.SerializeToUtf8Bytes(fixture));
        Check((await NativeBrowserStartup.ReadAsync(good, Nonce)).Binding == fixture.Binding);
        using var duplicate = Packet(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fixture).Insert(1, "\"Nonce\":\"" + Nonce + "\",")));
        await Reject<JsonException>(() => NativeBrowserStartup.ReadAsync(duplicate, Nonce));
        foreach (var name in new[] { "../index.html", "con.html", "index.html:stream", "/index.html" })
        {
            var bad = fixture with { PackageEntry = name, PackageFiles = [fixture.PackageFiles[0] with { Path = name }] };
            using var invalid = Packet(JsonSerializer.SerializeToUtf8Bytes(bad));
            await Reject<InvalidDataException>(() => NativeBrowserStartup.ReadAsync(invalid, Nonce));
        }
    }
    private static async Task ChangedPackage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "autumnos-browser-bytes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "index.html");
        try
        {
            File.WriteAllBytes(file, "A"u8.ToArray());
            using var packet = Packet(JsonSerializer.SerializeToUtf8Bytes(Descriptor(directory, "A"u8.ToArray(), Identity(file))));
            var startup = await NativeBrowserStartup.ReadAsync(packet, Nonce);
            Check(startup.ReadPackage()["index.html"].AsSpan().SequenceEqual("A"u8));
            File.WriteAllBytes(file, "B"u8.ToArray());
            await Reject<InvalidDataException>(() => Task.FromResult(startup.ReadPackage()));
        }
        finally { File.Delete(file); Directory.Delete(directory, recursive: false); }
    }

    private static NativeHostFileIdentity Identity(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return NativeHostLaunchManifest.ReadPlainFileIdentity(stream.SafeFileHandle);
    }
    private static async Task WithOwnedFiles(Func<string, string, Task> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "autumnos-browser-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "index.html");
        try { File.WriteAllBytes(file, "A"u8.ToArray()); await test(directory, file); }
        finally
        {
            File.Delete(Path.Combine(directory, "other.html")); File.Delete(file);
            Directory.Delete(directory, recursive: false);
        }
    }
    private static Task DifferentFileIdentity() => WithOwnedFiles(async (directory, file) =>
    {
        string other = Path.Combine(directory, "other.html"); File.WriteAllBytes(other, "A"u8.ToArray());
        Check(Identity(file) != Identity(other));
        var original = Descriptor(directory, "A"u8.ToArray(), Identity(file));
        var substituted = original with { PackageEntry = "other.html", PackageFiles = [original.PackageFiles[0] with { Path = "other.html" }] };
        await Reject<InvalidDataException>(() => Task.FromResult(substituted.ReadPackage()));
    });
    private static Task TamperedFileIdentity() => WithOwnedFiles(async (directory, file) =>
    {
        var original = Descriptor(directory, "A"u8.ToArray(), Identity(file));
        var id = original.PackageFiles[0].Identity;
        foreach (var changed in new[] { id with { VolumeSerial = id.VolumeSerial ^ 1 }, id with { FileIndexHigh = id.FileIndexHigh ^ 1 }, id with { FileIndexLow = id.FileIndexLow ^ 1 } })
        {
            var substituted = original with { PackageFiles = [original.PackageFiles[0] with { Identity = changed }] };
            await Reject<InvalidDataException>(() => Task.FromResult(substituted.ReadPackage()));
        }
        var missing = original with { PackageFiles = [original.PackageFiles[0] with { Identity = default }] };
        using var packet = Packet(JsonSerializer.SerializeToUtf8Bytes(missing));
        await Reject<InvalidDataException>(() => NativeBrowserStartup.ReadAsync(packet, Nonce));
    });
    private static Task HardLinkIdentity() => WithOwnedFiles((directory, file) =>
    {
        string other = Path.Combine(directory, "other.html");
        if (!CreateHardLinkW(other, file, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "OWNED_TEST_HARDLINK_FAILED");
        Check(Identity(file) == Identity(other));
        var original = Descriptor(directory, "A"u8.ToArray(), Identity(file));
        var alias = original with { PackageEntry = "other.html", PackageFiles = [original.PackageFiles[0] with { Path = "other.html" }] };
        Check(alias.ReadPackage()["other.html"].AsSpan().SequenceEqual("A"u8));
        return Task.CompletedTask;
    });
    private static Task DeclaredLength() => WithOwnedFiles(async (directory, file) =>
    {
        var original = Descriptor(directory, "A"u8.ToArray(), Identity(file));
        var changed = original with { PackageFiles = [original.PackageFiles[0] with { Bytes = 2 }] };
        await Reject<InvalidDataException>(() => Task.FromResult(changed.ReadPackage()));
    });
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    private sealed class Dispatcher : IRuntimeNativeDeliveryDispatcher
    {
        internal bool Current = true, ThrowAfterNative;
        internal int Calls;
        public bool IsDispatcherThread => true;
        public bool TryEnqueue(Action callback) { callback(); return true; }
        public bool IsCurrentDocument(RuntimeDeliveryBinding binding) => Current;
        public void PostJson(string json) { Calls++; if (ThrowAfterNative) throw new InvalidOperationException("fixture_after_native_entry"); }
    }
    private sealed class Endpoint : IDisposable
    {
        internal readonly RuntimeDeliveryBinding Binding = new(Guid.NewGuid(), "fixture", 1, new string('c', 64), Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
        internal readonly Dispatcher Dispatch = new();
        internal readonly BrowserDeliveryChannel Channel;
        private readonly MemoryStream data = new(), receipts = new();
        private readonly RuntimeDeliveryWireWriter writer;
        private readonly RuntimeDeliveryWireReader reader;
        internal Endpoint()
        {
            Channel = new(Binding, Dispatch, Nonce); writer = new(data, Nonce); reader = new(receipts, Nonce);
        }
        internal async Task<byte[]> Data(RuntimeDeliveryBinding? binding = null, long id = 1)
        {
            data.SetLength(0); byte[] json = "{}"u8.ToArray();
            await writer.WriteDeliveryAsync(new(id, binding ?? Binding, new(json.Length, Convert.ToHexStringLower(SHA256.HashData(json)), 1), json));
            return data.ToArray();
        }
        internal async Task<byte[]> Fence()
        {
            data.SetLength(0);
            await writer.WriteFenceAsync(new(1, Binding, 0, 2)); return data.ToArray();
        }
        internal async Task<RuntimeDeliveryWireMessage> Receipt(byte[] response, RuntimeDeliveryBinding binding)
        {
            receipts.SetLength(0); receipts.Write(response); receipts.Position = 0;
            return await reader.ReadAsync(binding);
        }
        public void Dispose() { Channel.Dispose(); data.Dispose(); receipts.Dispose(); }
    }
    private static async Task FenceBeforeRelease()
    {
        using var e = new Endpoint();
        var ack = await e.Channel.DispatchAsync(await e.Fence(), false, default);
        Check(await e.Receipt(ack, e.Binding) is RuntimeDeliveryWireFenceReceipt { Receipt.NextChannelGeneration: 2 });
        var advanced = e.Binding with { ChannelGeneration = 2 };
        var response = await e.Channel.DispatchAsync(await e.Data(advanced), true, default);
        Check(await e.Receipt(response, advanced) is RuntimeDeliveryWirePostReceipt { Receipt.Result: RuntimeNativePostResult.Posted });
        Check(e.Dispatch.Calls == 1); await e.Channel.CloseAsync();
    }
    private static async Task BeforeRelease()
    {
        using var e = new Endpoint(); byte[] data = await e.Data();
        await Reject<InvalidDataException>(() => e.Channel.DispatchAsync(data, false, default));
        Check(e.Dispatch.Calls == 0); await e.Channel.CloseAsync();
    }
    private static async Task TrailingBytes()
    {
        using var e = new Endpoint(); byte[] data = await e.Data();
        await Reject<InvalidDataException>(() => e.Channel.DispatchAsync([..data, ..data], true, default));
        Check(e.Dispatch.Calls == 0); await e.Channel.CloseAsync();
    }
    private static async Task LostDocument()
    {
        using var e = new Endpoint(); e.Dispatch.Current = false;
        var response = await e.Channel.DispatchAsync(await e.Data(), true, default);
        Check(await e.Receipt(response, e.Binding) is RuntimeDeliveryWirePostReceipt { Receipt.Result: RuntimeNativePostResult.RejectedBeforePost });
        Check(e.Dispatch.Calls == 0); await e.Channel.CloseAsync();
    }
    private static async Task UncertainPost()
    {
        using var e = new Endpoint(); e.Dispatch.ThrowAfterNative = true; byte[] data = await e.Data();
        await Reject<InvalidDataException>(() => e.Channel.DispatchAsync(data, true, default));
        Check(e.Dispatch.Calls == 1); await e.Channel.CloseAsync();
    }
}
