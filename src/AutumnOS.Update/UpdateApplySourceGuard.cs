using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using AutumnOS.Store;

namespace AutumnOS.Update;

/// <summary>Small metadata revalidation at the independent updater's destructive boundary.</summary>
public static class UpdateApplySourceGuard
{
    public static async Task VerifyDefaultAsync(UpdateManifest manifest, string tag, CancellationToken cancellationToken, string? manifestSha256 = null,
        StoreNetworkSettings? settings = null)
    {
        settings ??= new(); StoreNetworkSettingsService.Validate(settings);
        IGitHubTransport transport = UpdateTestFeed.TryCreateEmbedded() ?? (IGitHubTransport)new GitHubTransport(() => settings);
        try { await VerifyAsync(manifest, tag, transport, cancellationToken, manifestSha256).ConfigureAwait(false); }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    public static async Task VerifyAsync(UpdateManifest manifest, string tag, IGitHubTransport transport, CancellationToken cancellationToken, string? manifestSha256 = null)
    {
        UpdateManifestCodec.Validate(manifest);
        if (!(manifest.ReleaseTag is not null ? tag == manifest.ReleaseTag : tag == manifest.Channel + "-v" + manifest.Version || tag == manifest.Version || tag == "v" + manifest.Version))
            throw new UpdateException("UPDATE_RELEASE_TAG_MISMATCH");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(20));
        var uri = new Uri($"https://api.github.com/repos/{manifest.Repository}/releases/{manifest.Payload.ReleaseId}");
        using var response = await transport.SendAsync(uri, GitHubRequestKind.Api, new Dictionary<string, string> { ["Accept"] = "application/vnd.github+json", ["X-GitHub-Api-Version"] = "2022-11-28" }, budget.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateException("UPDATE_TARGET_WITHDRAWN");
        if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden) throw new UpdateException("UPDATE_RATE_LIMITED");
        if (response.StatusCode != HttpStatusCode.OK) throw new UpdateException("UPDATE_TARGET_RECHECK_FAILED");
        const int maximum = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new UpdateException("UPDATE_METADATA_ENCODING_OR_SIZE");
        using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var bytes = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) > 0)
        { if (bytes.Length + count > maximum) throw new UpdateException("UPDATE_METADATA_ENCODING_OR_SIZE"); bytes.Write(buffer, 0, count); }
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray()); var release = json.RootElement;
            if (release.GetProperty("draft").GetBoolean()) throw new UpdateException("UPDATE_TARGET_WITHDRAWN");
            if (release.GetProperty("id").GetInt64() != manifest.Payload.ReleaseId || release.GetProperty("tag_name").GetString() != tag ||
                release.GetProperty("prerelease").GetBoolean() != (manifest.Channel == "meta")) throw new UpdateException("UPDATE_TARGET_CHANGED");
            var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("id").GetInt64() == manifest.Payload.AssetId).ToArray();
            if (assets.Length != 1 || assets[0].GetProperty("name").GetString() != manifest.Payload.Asset || assets[0].GetProperty("size").GetInt64() != manifest.Payload.Bytes)
                throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
            if (!UpdateService.IsOfficialAssetUrl(assets[0].GetProperty("browser_download_url").GetString() ?? "", tag, manifest.Payload.Asset))
                throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
            if (assets[0].TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                digest.GetString() != "sha256:" + manifest.Payload.Sha256) throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
            if (manifestSha256 is not null)
            {
                var metadataAssets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == "autumn.update.json").ToArray();
                if (metadataAssets.Length != 1 || metadataAssets[0].GetProperty("size").GetInt64() is <= 0 or > UpdateManifestCodec.MaximumManifestBytes ||
                    !release.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == "autumn.update.sig")) throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
                var metadataUrl = new Uri($"https://github.com/{manifest.Repository}/releases/download/{Uri.EscapeDataString(tag)}/autumn.update.json");
                if (!UpdateService.IsOfficialAssetUrl(metadataAssets[0].GetProperty("browser_download_url").GetString() ?? "", tag, "autumn.update.json"))
                    throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
                using var metadataResponse = await transport.SendAsync(metadataUrl, GitHubRequestKind.Asset, null, budget.Token).ConfigureAwait(false);
                if (metadataResponse.StatusCode != HttpStatusCode.OK || metadataResponse.Content.Headers.ContentLength > UpdateManifestCodec.MaximumManifestBytes)
                    throw new UpdateException("UPDATE_TARGET_RECHECK_FAILED");
                using var metadataInput = await metadataResponse.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
                using var metadataBytes = new MemoryStream();
                while ((count = await metadataInput.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) > 0)
                { if (metadataBytes.Length + count > UpdateManifestCodec.MaximumManifestBytes) throw new UpdateException("UPDATE_METADATA_ENCODING_OR_SIZE"); metadataBytes.Write(buffer, 0, count); }
                if (Convert.ToHexStringLower(SHA256.HashData(metadataBytes.ToArray())) != manifestSha256) throw new UpdateException("UPDATE_TARGET_CHANGED");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new UpdateException("UPDATE_RELEASE_METADATA_INVALID", e); }
    }
}
