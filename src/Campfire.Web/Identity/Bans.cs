using Microsoft.Data.Sqlite;

namespace Campfire.Web.Identity;

/// <summary>
/// <c>Ban.banned?</c>: an exact IP match. GET and HEAD are not blocked (see request guards).
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
}
