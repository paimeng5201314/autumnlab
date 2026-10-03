using System.Collections.ObjectModel;

namespace AutumnOS.Storage;

/// <summary>Trusted host path resolver. Never expose this service through the application SDK.</summary>
public sealed class InstallationRoot
{
    private static readonly string[] Names =
    [
        "Apps", "Packages", "Downloads", "Cache", "AppData", "Saves", "Screenshots", "Config",
        "Logs", "Runtime", "Temp", "Updates"
    ];

    public InstallationRoot(string absoluteInstallationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteInstallationDirectory);
        if (!Path.IsPathFullyQualified(absoluteInstallationDirectory))
            throw new ArgumentException("An absolute installation directory is required.", nameof(absoluteInstallationDirectory));

        ProgramDirectory = Path.GetFullPath(absoluteInstallationDirectory);
        InstallationDirectory = AutumnOS.Contracts.PortableLayout.EntryDirectory(ProgramDirectory);
        DataDirectory = Path.Combine(InstallationDirectory, "AutumnOS_Data");
        Directories = new ReadOnlyDictionary<string, string>(Names.ToDictionary(name => name,
            name => Path.Combine(DataDirectory, name), StringComparer.Ordinal));
    }

    public static InstallationRoot ForCurrentProcess() => new(AppContext.BaseDirectory);

    public string InstallationDirectory { get; }
    public string ProgramDirectory { get; }
    public string DataDirectory { get; }
    public IReadOnlyDictionary<string, string> Directories { get; }

    /// <summary>Creates only missing directories and probes real write access; never resets existing data.</summary>
    public DataRootResult EnsureCreated(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckDirectory(InstallationDirectory, mustExist: true);
            EnsureDirectory(DataDirectory);
            foreach (string directory in Directories.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDirectory(directory);
            }

            foreach (string directory in Directories.Values.Prepend(DataDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDirectories();
                // A directory ReadOnly attribute is not a Windows access check. Create, flush and delete a real file.
                string probe = Path.Combine(directory, $".autumnos-write-probe-{Guid.NewGuid():N}");
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1, FileOptions.DeleteOnClose | FileOptions.WriteThrough);
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }
            return DataRootResult.Ready();
        }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        {
            return DataRootResult.Failed(StorageExceptionMapper.Code(exception));
        }
    }

    internal void ValidateDirectories()
    {
        CheckDirectory(InstallationDirectory, mustExist: true);
        CheckDirectory(DataDirectory, mustExist: true);
        foreach (string directory in Directories.Values)
            CheckDirectory(directory, mustExist: true);
    }

    internal void ValidateManagedFile(string path)
    {
        ValidateDirectories();
        FileAttributes? attributes = TryAttributes(path);
        if (attributes is null) return;
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new StoragePathException(StorageErrors.UnsafePath);
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new StoragePathException(StorageErrors.PathConflict);
    }

    private static void EnsureDirectory(string path)
    {
        CheckDirectory(path, mustExist: false);
        Directory.CreateDirectory(path);
        CheckDirectory(path, mustExist: true);
    }

    private static void CheckDirectory(string path, bool mustExist)
    {
        FileAttributes? attributes = TryAttributes(path);
        if (attributes is null)
        {
            if (mustExist) throw new DirectoryNotFoundException();
            return;
        }
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new StoragePathException(StorageErrors.UnsafePath);
        if ((attributes.Value & FileAttributes.Directory) == 0)
            throw new StoragePathException(StorageErrors.PathConflict);
    }

    private static FileAttributes? TryAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
