using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Identity;

/// <summary>
/// <c>Ban.banned?</c>: an exact IP match. GET and HEAD are not blocked (see request guards).
/// <c>Ban#ip_address_is_public</c> rejects loopback, RFC1918, IPv6 unique-local, and link-local.
/// </summary>
public static class Bans
{
    public static bool IsBanned(SqliteConnection connection, string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM bans WHERE ip_address = $ip LIMIT 1";
        command.Parameters.AddWithValue("$ip", ipAddress);
        return command.ExecuteScalar() is not null;
    }

    public static bool IsRecordablePublicAddress(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var parsed))
        {
            return false;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = parsed.GetAddressBytes();
            if (bytes[0] == 127)
            {
                return false;
            }

            if (bytes[0] == 10)
            {
                return false;
            }

            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            {
                return false;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return false;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return false;
            }

            return true;
        }

        if (parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (parsed.Equals(IPAddress.IPv6Loopback))
        {
            return false;
        }

        var v6 = parsed.GetAddressBytes();
        if ((v6[0] & 0xfe) == 0xfc)
        {
            return false;
        }

        return !(v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80);
    }
}
