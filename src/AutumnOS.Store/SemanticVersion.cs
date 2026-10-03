using System.Text.RegularExpressions;

namespace AutumnOS.Store;

/// <summary>SemVer 2.0 ordering; arbitrary-width numeric identifiers, build metadata never changes precedence.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly string[] core, pre;
    private SemanticVersion(string value, Match match) { Value = value; core = [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value]; pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : []; }
    public string Value { get; }
    public bool IsPrerelease => pre.Length != 0;
    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (value is null || value.Length is < 5 or > 128) return false;
        Match match = Pattern.Match(value);
        if (!match.Success) return false;
        version = new(value, match); return true;
    }
    public static SemanticVersion Parse(string value) => TryParse(value, out var version) ? version! : throw new CatalogException("STORE_VERSION_INVALID");
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < 3; i++) { int result = CompareNumeric(core[i], other.core[i]); if (result != 0) return result; }
        if (pre.Length == 0 || other.pre.Length == 0) return pre.Length == other.pre.Length ? 0 : pre.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(pre.Length, other.pre.Length); i++)
        {
            bool a = pre[i].All(char.IsAsciiDigit), b = other.pre[i].All(char.IsAsciiDigit);
            int result = a && b ? CompareNumeric(pre[i], other.pre[i]) : a != b ? a ? -1 : 1 : string.CompareOrdinal(pre[i], other.pre[i]);
            if (result != 0) return result;
        }
        return pre.Length.CompareTo(other.pre.Length);
    }
    private static int CompareNumeric(string a, string b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    public override string ToString() => Value;
}
