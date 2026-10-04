using System.Net;
using System.Net.Sockets;

namespace Campfire.Web.Http;

/// <summary>
/// Rails <c>request.remote_ip</c> for these endpoints. Loopback and private peers are trusted
/// proxies, so <c>X-Forwarded-For</c> supplies the client address (the test host is loopback,
/// matching Rails' default trusted proxies). A public peer ignores a spoofed forwarded header.
/// </summary>
public static class RemoteIp
{
    public static string? Resolve(HttpContext context)
    {
        var peer = Normalize(context.Connection.RemoteIpAddress);
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded) && (peer is null || IsTrustedProxy(peer)))
        {
            var client = ClientFromForwarded(forwarded);
            if (client is not null)
            {
                return client;
            }
        }

        return peer;
    }

    internal static string? ClientFromForwarded(string forwarded)
    {
        var parts = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var ip = Normalize(Parse(parts[i]));
            if (ip is null)
            {
                continue;
            }

            if (!IsTrustedProxy(ip))
            {
                return ip;
            }
        }

        return parts.Length == 0 ? null : Normalize(Parse(parts[0]));
    }

    internal static bool IsTrustedProxy(string ipAddress)
    {
        var parsed = Parse(ipAddress);
        if (parsed is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(parsed))
        {
            return true;
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = parsed.GetAddressBytes();
            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            return false;
        }

        var v6 = parsed.GetAddressBytes();
        if ((v6[0] & 0xfe) == 0xfc)
        {
            return true;
        }

        return v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80;
    }

    private static string? Normalize(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    private static IPAddress? Parse(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(value, out var address))
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        return null;
    }
}
