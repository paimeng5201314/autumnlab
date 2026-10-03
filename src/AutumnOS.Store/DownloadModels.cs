namespace AutumnOS.Store;

public enum DownloadState { Queued, Downloading, Paused, Verifying, AwaitingInstall, Installing, Completed, Cancelled, Failed }

/// <summary>Stable public source identity, not a temporary redirected CDN address.</summary>
public sealed record DownloadRequest(string AppId, string Version, Uri Url, long ExpectedBytes, string Sha256,
    long RepositoryId = 0, long ReleaseId = 0, long AssetId = 0);

public sealed record DownloadSnapshot(string Id, DownloadRequest Request, DownloadState State,
    long BytesReceived, long TotalBytes, double BytesPerSecond, TimeSpan? EstimatedRemaining,
    string? LocalPath, string? ErrorCode, int Attempts, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    string Route = "direct", DateTimeOffset? RetryAfterUtc = null);

public interface IDownloadService
{
    event EventHandler? Changed;
    DownloadSnapshot Enqueue(DownloadRequest request);
    IReadOnlyList<DownloadSnapshot> Snapshot();
    bool Pause(string id);
    bool Resume(string id);
    bool Cancel(string id);
    bool Retry(string id);
    bool MarkInstalling(string id);
    bool MarkInstalled(string id);
    bool MarkInstallFailed(string id, string errorCode);
}
