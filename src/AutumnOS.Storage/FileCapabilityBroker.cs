using AutumnOS.Contracts;

namespace AutumnOS.Storage;

public sealed record FileCapability(string Handle, string Name, long Bytes, DateTimeOffset ExpiresUtc);
public sealed record FileExportResult(long Bytes, bool BackupRetained);

/// <summary>
/// Capabilities originate exclusively in host system pickers. A read capability pins the selected
/// file; a distinct one-shot save capability exports atomically and preserves the selected original.
/// </summary>
public sealed class FileCapabilityBroker : IDisposable
{
    public const int MaximumHandles = 64;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(15);
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Func<StorageScope, bool> permittedAndCurrent;
    private readonly TimeProvider time;
    private readonly Func<StorageScope, IDisposable> commitLease;
    private readonly Func<StorageScope, long> permissionRevision;
    private readonly CriticalOperationCoordinator coordinator;
    private bool disposed;

    public FileCapabilityBroker(Func<StorageScope, bool> permittedAndCurrent, TimeProvider? timeProvider = null,
        Func<StorageScope, IDisposable>? commitLease = null, CriticalOperationCoordinator? coordinator = null,
        Func<StorageScope, long>? permissionRevision = null)
    {
        this.permittedAndCurrent = permittedAndCurrent; time = timeProvider ?? TimeProvider.System;
        this.commitLease = commitLease ?? (_ => new CriticalOperationCoordinator.Lease(() => { }));
        this.coordinator = coordinator ?? new();
        this.permissionRevision = permissionRevision ?? (_ => 0);
    }

    /// <summary>Trusted host API. Never expose this path parameter to the SDK or accept an application supplied path.</summary>
    public DataResult<FileCapability> RegisterPickedFile(string absolutePath, StorageScope scope, TimeSpan? lifetime = null,
        long? expectedPermissionRevision = null)
    {
        ScopedStorageSafety.ValidateScope(scope);
        if (!Path.IsPathFullyQualified(absolutePath)) return DataResult<FileCapability>.Fail("INVALID_PARAMS");
        TimeSpan duration = lifetime ?? MaximumLifetime;
        if (duration <= TimeSpan.Zero || duration > MaximumLifetime) return DataResult<FileCapability>.Fail("INVALID_PARAMS");
        lock (sync)
        {
            if (disposed) return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
            FileStream? stream = null;
            try
            {
                // Lock order: broker -> permission usage lease -> runtime/session lease.
                // The host lease must serialize the grant decision and its monotonic revision.
                using IDisposable lease = commitLease(scope);
                long revision = permissionRevision(scope);
                if (expectedPermissionRevision.HasValue && expectedPermissionRevision.Value != revision)
                    return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
                if (!Authorized(scope, revision)) return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
                PruneExpired();
                if (entries.Count >= MaximumHandles) return DataResult<FileCapability>.Fail("FILE_HANDLE_LIMIT");
                ScopedStorageSafety.File(absolutePath);
                stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaximumFileBytes) return DataResult<FileCapability>.Fail("FILE_TOO_LARGE");
                if (!Authorized(scope, revision)) return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
                string id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                DateTimeOffset expiry = time.GetUtcNow() + duration;
                string name = Path.GetFileName(absolutePath);
                // Only basename, size and opaque token cross the boundary. No disk path or stream handle is exposed.
                var capability = new FileCapability(id, name, stream.Length, expiry);
                entries.Add(id, new(scope, stream, expiry, null, null, revision)); stream = null;
                return DataResult<FileCapability>.Ok(capability);
            }
            catch (RuntimeCapabilityException e) { return DataResult<FileCapability>.Fail(e.Code); }
            catch (Exception e) when (ScopedStorageSafety.Handled(e)) { return DataResult<FileCapability>.Fail(ScopedStorageSafety.Error(e)); }
            finally { stream?.Dispose(); }
        }
    }

    /// <summary>Only a completed native save picker may supply this path. Registration does not create/truncate a file.</summary>
    public DataResult<FileCapability> RegisterPickedSaveFile(string absolutePath, StorageScope scope, TimeSpan? lifetime = null,
        long? expectedPermissionRevision = null)
    {
        ScopedStorageSafety.ValidateScope(scope);
        if (!Path.IsPathFullyQualified(absolutePath)) return DataResult<FileCapability>.Fail("INVALID_PARAMS");
        TimeSpan duration = lifetime ?? MaximumLifetime;
        if (duration <= TimeSpan.Zero || duration > MaximumLifetime) return DataResult<FileCapability>.Fail("INVALID_PARAMS");
        lock (sync)
        {
            if (disposed) return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
            FileStream? stream = null;
            try
            {
                using IDisposable lease = commitLease(scope);
                long revision = permissionRevision(scope);
                if (expectedPermissionRevision.HasValue && expectedPermissionRevision.Value != revision)
                    return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
                if (!Authorized(scope, revision)) return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
                PruneExpired();
                if (entries.Count >= MaximumHandles) return DataResult<FileCapability>.Fail("FILE_HANDLE_LIMIT");
                ScopedStorageSafety.File(absolutePath);
                if (!Directory.Exists(Path.GetDirectoryName(absolutePath))) return DataResult<FileCapability>.Fail("STORAGE_IO_ERROR");
                byte[]? expectedHash = null;
                if (File.Exists(absolutePath))
                {
                    stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (stream.Length > MaximumFileBytes) return DataResult<FileCapability>.Fail("FILE_TOO_LARGE");
                    expectedHash = System.Security.Cryptography.SHA256.HashData(stream); stream.Position = 0;
                }
                if (!Authorized(scope, revision)) return DataResult<FileCapability>.Fail("PERMISSION_REVOKED");
                string id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                DateTimeOffset expiry = time.GetUtcNow() + duration;
                var capability = new FileCapability(id, Path.GetFileName(absolutePath), stream?.Length ?? 0, expiry);
                entries.Add(id, new(scope, stream, expiry, absolutePath, expectedHash, revision)); stream = null;
                return DataResult<FileCapability>.Ok(capability);
            }
            catch (RuntimeCapabilityException e) { return DataResult<FileCapability>.Fail(e.Code); }
            catch (Exception e) when (ScopedStorageSafety.Handled(e)) { return DataResult<FileCapability>.Fail(ScopedStorageSafety.Error(e)); }
            finally { stream?.Dispose(); }
        }
    }

    /// <summary>One-shot, atomic export. Existing selected bytes get a unique adjacent backup; other paths are never accepted.</summary>
    public DataResult<FileExportResult> Write(string handle, StorageScope caller, byte[] bytes, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed || handle.Length != 64 || !entries.TryGetValue(handle, out Entry? entry)) return DataResult<FileExportResult>.Fail("FILE_HANDLE_INVALID");
            if (entry.Scope != caller) return DataResult<FileExportResult>.Fail("FILE_HANDLE_WRONG_OWNER");
            if (entry.SavePath is null) return DataResult<FileExportResult>.Fail("FILE_HANDLE_ACCESS_DENIED");
            if (entry.Expiry <= time.GetUtcNow()) { Remove(handle); return DataResult<FileExportResult>.Fail("FILE_HANDLE_EXPIRED"); }
            string staging = Path.Combine(Path.GetDirectoryName(entry.SavePath)!, $".AutumnOS-export-{Guid.NewGuid():N}.tmp");
            try
            {
                using (commitLease(caller))
                    if (!Authorized(caller, entry.PermissionRevision)) { Remove(handle); return DataResult<FileExportResult>.Fail("PERMISSION_REVOKED"); }
                if (bytes.Length > MaximumFileBytes) return DataResult<FileExportResult>.Fail("FILE_TOO_LARGE");
                using IDisposable critical = coordinator.EnterWrite("用户文件导出");
                cancellationToken.ThrowIfCancellationRequested(); ScopedStorageSafety.File(staging);
                using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
                using (commitLease(caller))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Authorized(caller, entry.PermissionRevision)) { Remove(handle); return DataResult<FileExportResult>.Fail("PERMISSION_REVOKED"); }
                    ScopedStorageSafety.File(entry.SavePath); ScopedStorageSafety.File(staging);
                    bool existed = File.Exists(entry.SavePath);
                    if (existed != (entry.ExpectedHash is not null)) return DataResult<FileExportResult>.Fail("FILE_CHANGED_SINCE_PICK");
                    if (existed)
                    {
                        using var current = new FileStream(entry.SavePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        if (current.Length > MaximumFileBytes || !System.Security.Cryptography.SHA256.HashData(current).SequenceEqual(entry.ExpectedHash!))
                            return DataResult<FileExportResult>.Fail("FILE_CHANGED_SINCE_PICK");
                        string backup = entry.SavePath + $".AutumnOS-backup-{Guid.NewGuid():N}";
                        ScopedStorageSafety.File(backup);
                        File.Replace(staging, entry.SavePath, backup);
                    }
                    else File.Move(staging, entry.SavePath, overwrite: false);
                    Remove(handle);
                    return DataResult<FileExportResult>.Ok(new(bytes.LongLength, existed));
                }
            }
            catch (RuntimeCapabilityException e) { Remove(handle); return DataResult<FileExportResult>.Fail(e.Code); }
            catch (Exception e) when (ScopedStorageSafety.Handled(e)) { return DataResult<FileExportResult>.Fail(ScopedStorageSafety.Error(e)); }
            finally
            {
                try { ScopedStorageSafety.File(staging); File.Delete(staging); }
                catch (Exception e) when (ScopedStorageSafety.Handled(e)) { }
            }
        }
    }

    public DataResult<byte[]> Read(string handle, StorageScope caller, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (disposed || handle.Length != 64 || !entries.TryGetValue(handle, out Entry? entry)) return DataResult<byte[]>.Fail("FILE_HANDLE_INVALID");
            if (entry.Scope != caller) return DataResult<byte[]>.Fail("FILE_HANDLE_WRONG_OWNER");
            if (entry.SavePath is not null) return DataResult<byte[]>.Fail("FILE_HANDLE_ACCESS_DENIED");
            if (entry.Expiry <= time.GetUtcNow()) { Remove(handle); return DataResult<byte[]>.Fail("FILE_HANDLE_EXPIRED"); }
            try
            {
                using (commitLease(caller))
                    if (!Authorized(caller, entry.PermissionRevision)) { Remove(handle); return DataResult<byte[]>.Fail("PERMISSION_REVOKED"); }
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Stream!.Length > MaximumFileBytes) return DataResult<byte[]>.Fail("FILE_TOO_LARGE");
                byte[] bytes = new byte[checked((int)entry.Stream.Length)];
                entry.Stream.Position = 0; entry.Stream.ReadExactly(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                using (commitLease(caller))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Authorized(caller, entry.PermissionRevision)) { Remove(handle); return DataResult<byte[]>.Fail("PERMISSION_REVOKED"); }
                    return DataResult<byte[]>.Ok(bytes);
                }
            }
            catch (RuntimeCapabilityException e) { Remove(handle); return DataResult<byte[]>.Fail(e.Code); }
            catch (Exception e) when (ScopedStorageSafety.Handled(e)) { return DataResult<byte[]>.Fail(ScopedStorageSafety.Error(e)); }
        }
    }

    public DataResult<bool> Close(string handle, StorageScope caller)
    {
        lock (sync)
        {
            if (disposed || handle.Length != 64 || !entries.TryGetValue(handle, out Entry? entry)) return DataResult<bool>.Fail("FILE_HANDLE_INVALID");
            if (entry.Scope != caller) return DataResult<bool>.Fail("FILE_HANDLE_WRONG_OWNER");
            if (entry.Expiry <= time.GetUtcNow()) { Remove(handle); return DataResult<bool>.Fail("FILE_HANDLE_EXPIRED"); }
            try
            {
                using IDisposable lease = commitLease(caller);
                if (!Authorized(caller, entry.PermissionRevision)) { Remove(handle); return DataResult<bool>.Fail("PERMISSION_REVOKED"); }
                Remove(handle); return DataResult<bool>.Ok(true);
            }
            catch (RuntimeCapabilityException e) { Remove(handle); return DataResult<bool>.Fail(e.Code); }
            catch (Exception e) when (ScopedStorageSafety.Handled(e)) { return DataResult<bool>.Fail(ScopedStorageSafety.Error(e)); }
        }
    }
    /// <summary>
    /// Releases idle capabilities whose current permission, generation or session is invalid.
    /// Queue this operation after permission notifications; never call it while holding a permission
    /// lease. The configured commit lease must explicitly acquire this broker's permission before
    /// the runtime/session lease, including when called without an SDK request context.
    /// </summary>
    public int PruneRevoked()
    {
        lock (sync)
        {
            if (disposed) return 0;
            int removed = 0;
            // Notifications may arrive after revoke -> grant -> a new picker. Inspect each entry's
            // current binding instead of applying the old notification's decision to every handle.
            foreach (var pair in entries.ToArray())
            {
                try
                {
                    using IDisposable lease = commitLease(pair.Value.Scope);
                    if (Authorized(pair.Value.Scope, pair.Value.PermissionRevision)) continue;
                }
                catch (RuntimeCapabilityException error) when (error.Code is "PERMISSION_REVOKED" or "PERMISSION_DENIED"
                    or "PERMISSION_NOT_DECLARED" or "SESSION_EXPIRED" or "SESSION_STALE")
                {
                    // A usage lease can itself reject the now-invalid scope. Releasing this old
                    // entry is safe even if a later grant wins before its stream is disposed.
                }
                // Unknown failures propagate: a failed provider is not evidence that a current
                // capability was revoked. Registration cannot race this removal under broker sync.
                Remove(pair.Key);
                removed++;
            }
            return removed;
        }
    }
    public void RevokeInstance(AutumnOS.Contracts.AppInstanceId instance)
    { lock (sync) foreach (string key in entries.Where(p => p.Value.Scope.InstanceId == instance).Select(p => p.Key).ToArray()) Remove(key); }
    public void RevokeAll() { lock (sync) foreach (string key in entries.Keys.ToArray()) Remove(key); }
    public void Dispose() { lock (sync) { disposed = true; RevokeAll(); } }
    private void PruneExpired()
    { foreach (string key in entries.Where(p => p.Value.Expiry <= time.GetUtcNow()).Select(p => p.Key).ToArray()) Remove(key); }
    private void Remove(string key) { if (entries.Remove(key, out Entry? entry)) entry.Stream?.Dispose(); }
    // Regranting a permission never revalidates an already issued capability. Checking both sides
    // of the decision also rejects a changing provider; the host lease supplies atomicity.
    private bool Authorized(StorageScope scope, long revision) => permissionRevision(scope) == revision &&
        permittedAndCurrent(scope) && permissionRevision(scope) == revision;
    private sealed record Entry(StorageScope Scope, FileStream? Stream, DateTimeOffset Expiry, string? SavePath, byte[]? ExpectedHash, long PermissionRevision);
}
