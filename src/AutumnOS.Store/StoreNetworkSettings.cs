using System.Text.Json;

namespace AutumnOS.Store;

/// <summary>Public GitHub traffic only. This never changes Windows, Logto or application networking.</summary>
public sealed record StoreNetworkSettings(bool UseSystemProxy = true, string ApiUrlTemplate = "",
    string AssetUrlTemplate = "", bool AllowDirectFallback = true, int MaximumConcurrentDownloads = 2,
    long BytesPerSecond = 0);

public sealed class StoreNetworkSettingsService
{
    private readonly string _path;
    private readonly object _gate = new();
    public StoreNetworkSettingsService(string dataRoot)
    {
        _path = Path.Combine(Path.GetFullPath(dataRoot), "Config", "store-network.json");
        DownloadFiles.EnsureDirectory(Path.GetDirectoryName(_path)!);
    }

    public StoreNetworkSettings Load()
    {
        lock (_gate)
        {
            DownloadFiles.Validate(_path);
            if (!File.Exists(_path)) return new();
            if (new FileInfo(_path).Length > 16_384) throw new InvalidDataException("STORE_NETWORK_CONFIG_INVALID");
            var settings = JsonSerializer.Deserialize<StoreNetworkSettings>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("STORE_NETWORK_CONFIG_INVALID");
            Validate(settings);
            return settings;
        }
    }

    public void Save(StoreNetworkSettings settings)
    {
        Validate(settings);
        lock (_gate) DownloadFiles.WriteJson(_path, settings);
    }

    public static void Validate(StoreNetworkSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MaximumConcurrentDownloads is < 1 or > 4 || settings.BytesPerSecond is < 0 or > 1_073_741_824)
            throw new ArgumentException("STORE_NETWORK_LIMIT_INVALID");
        GitHubTransport.ValidateTemplate(settings.ApiUrlTemplate);
        GitHubTransport.ValidateTemplate(settings.AssetUrlTemplate);
    }
}

internal static class DownloadFiles
{
    internal static void Validate(string path)
    {
        for (string? item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
        {
            try
            {
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("STORE_UNSAFE_PATH");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static void EnsureDirectory(string path) { Validate(path); Directory.CreateDirectory(path); Validate(path); }
    internal static void DeleteOwned(string path) { Validate(path); if (File.Exists(path)) File.Delete(path); }
    internal static void WriteJson<T>(string path, T value)
    {
        Validate(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }
            Validate(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { DeleteOwned(temporary); }
    }
}
