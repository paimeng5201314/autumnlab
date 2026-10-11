using AutumnOS.Contracts;
using AutumnOS.Update;

namespace AutumnOS.UpdateProtocol;

public sealed record PortableEntryRecord(int SchemaVersion, string SeedSha256, string ProductDirectory, string BuildId, string Version);

/// <summary>A durable record outside the extractable runtime prevents an old outer EXE
/// from silently reinstalling its embedded seed if an updated runtime disappears.</summary>
public static class PortableEntryBinding
{
    public static void Commit(string root, string buildId, string version)
    {
        if (!File.Exists(Path.Combine(root, PortableLayout.MarkerName))) return;
        _ = PortableLayout.EntryDirectory(root);
        string seedPath = Path.Combine(root, ".autumnos-single-file-seed");
        UpdatePaths.EnsureNoReparsePoints(seedPath);
        if (!File.Exists(seedPath) || new FileInfo(seedPath).Length != 64) throw new IOException("UPDATE_PORTABLE_SEED_INVALID");
        string seed = File.ReadAllText(seedPath);
        if (seed.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || Path.GetFileName(root) != "Product-" + seed[..20])
            throw new IOException("UPDATE_PORTABLE_SEED_INVALID");
        string destination = Path.Combine(Path.GetDirectoryName(root)!, "Entry-" + seed[..20] + ".json");
        UpdateFiles.Write(destination, new PortableEntryRecord(1, seed, Path.GetFileName(root), buildId, version));
    }
}
