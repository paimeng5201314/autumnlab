using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AutumnOS.NativeHost.Windows;

// Bootstrap is separate from the application's delivery wire. There is no
// application JSON, script, arbitrary opcode or retirement assertion here.
internal enum NativeBootstrapKind : ushort { JobReady = 1, BrowserAdapterUnsupported = 2, QueryJobBound = 3, ContextCreated, ContextBound, RestrictedRootCreated, RestrictedRootBound, BrowserControllerReady, ProfileCreated, ProfileBound }
internal sealed record NativeBootstrapFact(NativeBootstrapKind Kind, uint ProcessId,
    ulong CreationFileTime, ulong OriginalJobHandle);

internal static class NativeHostBootstrapProtocol
{
    private const uint Magic = 0x31484e41;
    private const int Length = 96;
    internal static string PipeName(string nonce) => "AutumnOS.NativeHost." + nonce;

    internal static async Task WriteAsync(Stream stream, NativeHostLaunchManifest manifest, NativeBootstrapFact fact)
        => await WriteAsync(stream, manifest.Nonce, manifest.ManifestSha256, fact).ConfigureAwait(false);

    internal static async Task WriteAsync(Stream stream, string nonce, string manifestSha256, NativeBootstrapFact fact)
    {
        ValidateHashes(nonce, manifestSha256);
        if (!Enum.IsDefined(fact.Kind) || fact.ProcessId == 0 || fact.CreationFileTime == 0 ||
            !ValidOperand(fact.Kind, fact.OriginalJobHandle))
            throw new InvalidDataException("NATIVE_BOOTSTRAP_FACT_SHAPE_INVALID");
        byte[] bytes = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)fact.Kind);
        Convert.FromHexString(nonce).CopyTo(bytes, 8);
        Convert.FromHexString(manifestSha256).CopyTo(bytes, 40);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), fact.ProcessId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(76), fact.CreationFileTime);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(84), fact.OriginalJobHandle);
        // Last four bytes are reserved zero; no unbounded extension fields.
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    internal static async Task<NativeBootstrapFact> ReadAsync(Stream stream, NativeHostLaunchManifest manifest,
        uint originalPid, ulong originalCreation)
        => await ReadAsync(stream, manifest.Nonce, manifest.ManifestSha256, originalPid, originalCreation).ConfigureAwait(false);

    internal static async Task<NativeBootstrapFact> ReadAsync(Stream stream, string nonce, string manifestSha256,
        uint originalPid, ulong originalCreation)
    {
        ValidateHashes(nonce, manifestSha256);
        if (originalPid == 0 || originalCreation == 0) throw new InvalidDataException("NATIVE_BOOTSTRAP_EXPECTED_ORIGINAL_REQUIRED");
        byte[] bytes = new byte[Length];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(92)) != 0 ||
            !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(8, 32), Convert.FromHexString(nonce)) ||
            !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(40, 32), Convert.FromHexString(manifestSha256)))
            throw new InvalidDataException("NATIVE_BOOTSTRAP_BINDING_INVALID");
        var kind = (NativeBootstrapKind)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        uint pid = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(72));
        ulong creation = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(76));
        ulong job = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(84));
        if (!Enum.IsDefined(kind) || pid != originalPid || creation != originalCreation ||
            !ValidOperand(kind, job))
            throw new InvalidDataException("NATIVE_BOOTSTRAP_ORIGINAL_OBJECT_BINDING_INVALID");
        return new(kind, pid, creation, job);
    }
    private static bool ValidOperand(NativeBootstrapKind kind, ulong value) => kind switch
    {
        NativeBootstrapKind.JobReady => value != 0,
        NativeBootstrapKind.RestrictedRootCreated or NativeBootstrapKind.BrowserControllerReady => value is > 0 and <= uint.MaxValue,
        _ => value == 0
    };
    private static void ValidateHashes(string nonce, string manifestSha256)
    {
        if (!NativeHostBinding.IsLowerSha256(nonce) || !NativeHostBinding.IsLowerSha256(manifestSha256))
            throw new InvalidDataException("NATIVE_BOOTSTRAP_HASH_SHAPE_INVALID");
    }
}

// Pure creation-intent ownership. It grants no OS evidence. The real owner calls
// CompleteCreation only after its synchronous native factory has returned and
// supplied its actual no-child result; Close is solely a request throughout.
internal sealed class NativeCreationOwnership
{
    private readonly object sync = new();
    private bool closeRequested, claimed, creationOwned, settled, noChildCreatedProven;
    internal bool CloseRequested { get { lock (sync) return closeRequested; } }
    internal bool CreationOwned { get { lock (sync) return creationOwned; } }
    internal bool CanRetireNeverCreated
    { get { lock (sync) return !creationOwned && (!claimed || settled && noChildCreatedProven); } }
    internal bool TryClaimCreation()
    {
        lock (sync)
        {
            if (claimed) throw new InvalidOperationException("NATIVE_CREATION_INTENT_SINGLE_USE");
            if (closeRequested) return false;
            claimed = true; creationOwned = true;
            return true;
        }
    }
    internal void RequestClose() { lock (sync) closeRequested = true; }
    internal void CompleteCreation(bool nativeCallAttempted, bool actualNoChildCreatedProven)
    {
        lock (sync)
        {
            if (!creationOwned || settled) throw new InvalidOperationException("NATIVE_CREATION_INTENT_NOT_OWNED");
            noChildCreatedProven = !nativeCallAttempted || actualNoChildCreatedProven;
            settled = true; creationOwned = false;
        }
    }
}
