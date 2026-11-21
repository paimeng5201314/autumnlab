using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using AutumnOS.Store;

namespace AutumnOS.Update;

/// <summary>Small metadata revalidation at the independent updater's destructive boundary.</summary>
public static class UpdateApplySourceGuard
{
    public static async Task VerifyDefaultAsync(UpdateManifest manifest, string tag, CancellationToken cancellationToken, string? manifestSha256 = null,
        StoreNetworkSettings? settings = null, string? signatureSha256 = null)
    {
        settings ??= new(); StoreNetworkSettingsService.Validate(settings);
        IGitHubTransport transport = UpdateTestFeed.TryCreateEmbedded() ?? (IGitHubTransport)new GitHubTransport(() => settings);
        try { await VerifyAsync(manifest, tag, transport, cancellationToken, manifestSha256, signatureSha256).ConfigureAwait(false); }
        finally { (transport as IDisposable)?.Dispose(); }
    }

    public static async Task VerifyAsync(UpdateManifest manifest, string tag, IGitHubTransport transport, CancellationToken cancellationToken, string? manifestSha256 = null, string? signatureSha256 = null)
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
                release.GetProperty("prerelease").GetBoolean() != manifest.IsPreview) throw new UpdateException("UPDATE_TARGET_CHANGED");
            var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("id").GetInt64() == manifest.Payload.AssetId || a.GetProperty("name").GetString() == manifest.Payload.Asset).ToArray();
            if (assets.Length != 1 || assets[0].GetProperty("id").GetInt64() != manifest.Payload.AssetId || assets[0].GetProperty("name").GetString() != manifest.Payload.Asset || assets[0].GetProperty("size").GetInt64() != manifest.Payload.Bytes)
                throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
            if (!UpdateService.IsOfficialAssetUrl(assets[0].GetProperty("browser_download_url").GetString() ?? "", tag, manifest.Payload.Asset))
                throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
            if (assets[0].TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String &&
                digest.GetString() != "sha256:" + manifest.Payload.Sha256) throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
            if (manifestSha256 is not null)
            {
                await CheckAssetAsync("autumn.update.json", UpdateManifestCodec.MaximumManifestBytes, manifestSha256).ConfigureAwait(false);
                await CheckAssetAsync("autumn.update.sig", 16 * 1024, signatureSha256).ConfigureAwait(false);
            }
            async Task CheckAssetAsync(string name, int limit, string? expectedSha)
            {
                var matches = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
                if (matches.Length != 1 || matches[0].GetProperty("id").GetInt64() <= 0 ||
                    !UpdateService.IsOfficialAssetUrl(matches[0].GetProperty("browser_download_url").GetString() ?? "", tag, name)) throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
                long length = matches[0].GetProperty("size").GetInt64();
                if (length <= 0 || length > limit) throw new UpdateException("UPDATE_TARGET_ASSET_CHANGED");
                if (expectedSha is null) return;
                var url = new Uri($"https://github.com/{manifest.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}");
                using var reply = await transport.SendAsync(url, GitHubRequestKind.Asset, null, budget.Token).ConfigureAwait(false);
                if (reply.StatusCode != HttpStatusCode.OK || reply.Content.Headers.ContentLength is long declared && declared != length)
                    throw new UpdateException("UPDATE_TARGET_RECHECK_FAILED");
                using var input = await reply.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0; int read;
                while ((read = await input.ReadAsync(buffer, budget.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > length) throw new UpdateException("UPDATE_METADATA_ENCODING_OR_SIZE");
                    hash.AppendData(buffer, 0, read);
                }
                if (received != length || Convert.ToHexStringLower(hash.GetHashAndReset()) != expectedSha) throw new UpdateException("UPDATE_TARGET_CHANGED");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new UpdateException("UPDATE_RELEASE_METADATA_INVALID", e); }
    }
}
