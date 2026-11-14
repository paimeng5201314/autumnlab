using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Store;

namespace AutumnOS.Tests;

internal static class NetworkPackageTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("network.package_old_offline_manifest_remains_compatible", Offline);
        yield return ("network.package_permission_requires_exact_nonempty_domains", PairedDeclaration);
        yield return ("network.package_rejects_wildcards_IP_URL_reserved_and_noncanonical_domains", InvalidDomains);
        yield return ("network.package_domain_count_duplicates_and_labels_are_bounded", DomainBounds);
        yield return ("network.package_roundtrip_preserves_frozen_sorted_domains_and_original_bytes", RoundTrip);
        yield return ("network.scope_hash_is_order_independent_but_changes_with_targets", ScopeHash);
        yield return ("network.release_generation_requires_SDK04_for_network_packages", MinimumSdk);
    }

    private static Dictionary<string, object?> Manifest() => new()
    {
        ["schemaVersion"] = 1, ["appId"] = "cn.autumn.networkfixture", ["name"] = "网络回归",
        ["version"] = "0.1.0", ["runtime"] = "web", ["entry"] = "index.html", ["permissions"] = Array.Empty<string>()
    };

    private static void Offline() => InTemp(root =>
    {
        var result = PackageInstaller.Inspect(Package(root, Manifest()));
        Assert(result.Manifest.NetworkDomains is null && result.Manifest.Permissions.Length == 0);
    });

    private static void PairedDeclaration() => InTemp(root =>
    {
        var m = Manifest(); m["permissions"] = new[] { "network" };
        Reject(root, m);
        foreach (object? invalid in new object?[] { null, "api.github.com", Array.Empty<string>(), new object[] { 1 } })
        { m["networkDomains"] = invalid; Reject(root, m); }
        m["permissions"] = Array.Empty<string>(); m["networkDomains"] = new[] { "api.github.com" }; Reject(root, m);
    });

    private static void InvalidDomains() => InTemp(root =>
    {
        foreach (string invalid in new[] { "*.github.com", "https://api.github.com", "api.github.com:443", "api.github.com/", "api.github.com.",
            "API.github.com", "127.0.0.1", "0x7f.0x1", "2130706433", "[::1]", "localhost", "host.local", "host.internal", "host.test",
            "host.invalid", "host.example", "host.onion", "a..com", "-a.com", "a-.com", " api.github.com", "api.github.com\n", "例子.cn", "api.github.com@other.com" })
        {
            var m = Manifest(); m["permissions"] = new[] { "network" }; m["networkDomains"] = new[] { invalid }; Reject(root, m);
        }
    });

    private static void DomainBounds() => InTemp(root =>
    {
        var m = Manifest(); m["permissions"] = new[] { "network" };
        foreach (string[] invalid in new[] { new[] { "api.github.com", "api.github.com" },
            Enumerable.Range(0, 17).Select(i => "host" + i + ".github.com").ToArray(), new[] { new string('a', 64) + ".com" },
            new[] { string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) } })
        { m["networkDomains"] = invalid; Reject(root, m); }
        m["networkDomains"] = Enumerable.Range(0, 16).Select(i => "host" + i + ".github.com").ToArray();
        Assert(PackageInstaller.Inspect(Package(root, m)).Manifest.NetworkDomains?.Length == 16);
    });

    private static void RoundTrip() => InTemp(root =>
    {
        var m = Manifest(); m["permissions"] = new[] { "network" }; m["networkDomains"] = new[] { "www.microsoft.com", "api.github.com" };
        m["permissionPurposes"] = new Dictionary<string, string> { ["network"] = "读取公开资料" };
        string package = Package(root, m); byte[] before = File.ReadAllBytes(package);
        var inspected = PackageInstaller.Inspect(package);
        var installed = PackageInstaller.Install(package, Path.Combine(root, "installed"));
        Assert(inspected.Manifest.NetworkDomains!.SequenceEqual(new[] { "api.github.com", "www.microsoft.com" }));
        Assert(installed.Manifest.NetworkDomains!.SequenceEqual(inspected.Manifest.NetworkDomains));
        Assert(File.ReadAllBytes(package).SequenceEqual(before));
    });

    private static void ScopeHash()
    {
        string[] original = ["www.microsoft.com", "api.github.com"];
        string[] copy = NetworkDomainPolicy.CopyValidated(original);
        string a = NetworkDomainPolicy.ScopeHash(original), b = NetworkDomainPolicy.ScopeHash(original.Reverse());
        original[0] = "learn.microsoft.com";
        Assert(a == b && a != NetworkDomainPolicy.ScopeHash(original) && copy.SequenceEqual(new[] { "api.github.com", "www.microsoft.com" }));
        var app = new RuntimeApplication(new("cn.autumn.networkfixture", 1, null), "github:1", "测试", ["network"], NetworkDomains: copy);
        Assert(app.BindingKey == (app with { NetworkDomains = original }).BindingKey);
    }

    private static void MinimumSdk() => InTemp(root =>
    {
        var m = Manifest(); m["permissions"] = new[] { "network" }; m["networkDomains"] = new[] { "api.github.com" };
        string path = Package(root, m);
        Assert(ReleaseMetadata.GenerateRelease(path).MinSdkVersion == "0.4.0");
        Assert(ReleaseMetadata.GenerateRelease(path, minimumSdkVersion: "1.2.0").MinSdkVersion == "1.2.0");
    });

    private static string Package(string root, Dictionary<string, object?> manifest)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".autumn");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)))
            writer.Write(JsonSerializer.Serialize(manifest));
        using (var writer = new StreamWriter(archive.CreateEntry("index.html").Open(), new UTF8Encoding(false))) writer.Write("<!doctype html><title>test</title>");
        return path;
    }

    private static void Reject(string root, Dictionary<string, object?> manifest)
    {
        try { _ = PackageInstaller.Inspect(Package(root, manifest)); }
        catch (PackageException e) when (e.Code == "PACKAGE_NETWORK_DOMAINS_INVALID") { return; }
        throw new InvalidOperationException("Invalid network authority was accepted.");
    }

    private static void InTemp(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "autumnos-network-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Network package invariant failed."); }
}
