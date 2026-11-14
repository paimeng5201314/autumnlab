using System.Net;
using System.Net.Sockets;

namespace AutumnOS.Runtime;

/// <summary>Conservative ordinary global-unicast destinations only. Special-purpose
/// ranges are denied even where a registry has a globally-reachable exception.</summary>
internal static class RuntimeNetworkAddressPolicy
{
    internal static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            if (b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31 ||
                b[0] == 192 && (b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99 || b[1] == 168) ||
                b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100) ||
                b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
            return true;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6 || address.ScopeId != 0) return false;
        byte[] v = address.GetAddressBytes();
        if ((v[0] & 0xe0) != 0x20) return false; // 2000::/3 only: no local/mapped/NAT64/multicast address.
        return !(v[0] == 0x20 && (v[1] == 1 && ((v[2] & 0xfe) == 0 || v[2] == 0x0d && v[3] == 0xb8) || v[1] == 2) ||
            v[0] == 0x3f && (v[1] == 0xfe || v[1] == 0xff && (v[2] & 0xf0) == 0));
    }
}
