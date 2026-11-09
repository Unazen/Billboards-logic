using System.Net;
using System.Net.Sockets;
using Billboard.Application.Common;

namespace Billboard.Application.Media;

/// <summary>Защита импорта по URL от SSRF: запрещает обращения во внутренние сети.</summary>
public static class UrlGuard
{
    public static Uri ParseAndValidate(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw AppException.Validation("INVALID_URL", "Некорректный URL.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw AppException.Validation("INVALID_URL", "Разрешены только http и https.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw AppException.Validation("INVALID_URL", "URL с логином и паролем не допускается.");
        return uri;
    }

    /// <summary>true, если адрес публичный (не loopback, не частная сеть, не link-local и т.п.).</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                  || b[0] == 10
                  || b[0] == 127
                  || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // CGNAT 100.64.0.0/10
                  || (b[0] == 169 && b[1] == 254)                 // link-local, облачные metadata
                  || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                  || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                  || (b[0] == 192 && b[1] == 168)
                  || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                  || b[0] >= 224);                                // multicast и зарезервированные
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;              // unique local fc00::/7
            return true;
        }

        return false;
    }
}
