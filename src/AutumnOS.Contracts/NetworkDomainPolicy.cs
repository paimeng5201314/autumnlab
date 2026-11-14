using System.Net;

namespace AutumnOS.Contracts;

/// <summary>Exact manifest DNS names, shared by package validation and the host network broker.</summary>
public static class NetworkDomainPolicy
{
    public const int MaximumDomains = 16;
    public const int MaximumDomainLength = 253;
    private static readonly string[] ReservedSuffixes = ["localhost", "local", "internal", "invalid", "test", "example", "onion"];

    public static bool IsValidDomain(string? domain)
    {
        if (domain is null || domain.Length is < 3 or > MaximumDomainLength ||
            domain.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')) ||
            IPAddress.TryParse(domain, out _)) return false;
        string[] labels = domain.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-') ||
            !labels[^1].Any(c => c is >= 'a' and <= 'z') || ReservedSuffixes.Contains(labels[^1], StringComparer.Ordinal)) return false;
        return true;
    }

    /// <summary>Returns a new canonical array; callers must not retain a mutable manifest collection as authority.</summary>
    public static string[] CopyValidated(IEnumerable<string>? domains)
    {
        if (domains is null) throw new RuntimeCapabilityException("NETWORK_DOMAINS_INVALID");
        string[] copy = domains.Take(MaximumDomains + 1).ToArray();
        if (copy.Length is < 1 or > MaximumDomains || copy.Any(domain => !IsValidDomain(domain)) ||
            copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new RuntimeCapabilityException("NETWORK_DOMAINS_INVALID");
        Array.Sort(copy, StringComparer.Ordinal);
        return copy;
    }

    public static string ScopeHash(IEnumerable<string>? domains) =>
        RuntimeApplication.Hash("autumnos.network-scope.v1\0" + string.Join('\n', CopyValidated(domains)));
}
