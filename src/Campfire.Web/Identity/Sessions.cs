using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Identity;

public readonly record struct SessionRow(long Id, long UserId, string Token, string? LastActiveAt);

/// <summary>
/// Session rows: <c>Session.start!</c>, cookie lookup, and <c>resume</c> (refresh after 1 hour).
/// </summary>
public static class Sessions
{
    public static readonly TimeSpan ActivityRefreshRate = TimeSpan.FromHours(1);

    public static string Start(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long userId,
        string? ipAddress,
        string? userAgent,
        string? timestamp = null)
    {
        var token = SessionToken.Generate();
        var now = timestamp ?? FirstRun.Timestamp();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions (created_at, ip_address, last_active_at, token, updated_at, user_agent, user_id)
            VALUES ($created, $ip, $active, $token, $updated, $agent, $user)
            """;
        command.Parameters.AddWithValue("$created", now);
        command.Parameters.AddWithValue("$ip", (object?)ipAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", now);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$updated", now);
        command.Parameters.AddWithValue("$agent", (object?)userAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("$user", userId);
        command.ExecuteNonQuery();
        return token;
    }

    public static SessionRow? FindByToken(SqliteConnection connection, string token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_id, token, last_active_at
            FROM sessions
            WHERE token = $token
            """;
        command.Parameters.AddWithValue("$token", token);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SessionRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>
    /// Rails <c>Session#resume</c>: update user agent, IP, and last_active_at only when the
    /// previous activity is older than one hour.
    /// </summary>
    public static SessionRow? Resume(
        SqliteConnection connection,
        string token,
        string? ipAddress,
        string? userAgent,
        DateTimeOffset? now = null)
    {
        var session = FindByToken(connection, token);
        if (session is null)
        {
            return null;
        }

        var clock = now ?? DateTimeOffset.UtcNow;
        if (!NeedsRefresh(session.Value.LastActiveAt, clock))
        {
            return session;
        }

        var timestamp = FirstRun.Timestamp(clock.UtcDateTime);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET user_agent = $agent, ip_address = $ip, last_active_at = $active, updated_at = $updated
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$agent", (object?)userAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("$ip", (object?)ipAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", timestamp);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$id", session.Value.Id);
        command.ExecuteNonQuery();
        return session.Value with { LastActiveAt = timestamp };
    }

    public static void Destroy(SqliteConnection connection, long sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        command.ExecuteNonQuery();
    }

    public static void RemovePushSubscription(SqliteConnection connection, long userId, string endpoint)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM push_subscriptions
            WHERE endpoint = $endpoint AND user_id = $user
            """;
        command.Parameters.AddWithValue("$endpoint", endpoint);
        command.Parameters.AddWithValue("$user", userId);
        command.ExecuteNonQuery();
    }

    public static long? Authenticate(SqliteConnection connection, string? emailAddress, string? password)
    {
        if (string.IsNullOrEmpty(emailAddress) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, password_digest
            FROM users
            WHERE email_address = $email AND status = 0
            """;
        command.Parameters.AddWithValue("$email", emailAddress);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var id = reader.GetInt64(0);
        var digest = reader.IsDBNull(1) ? null : reader.GetString(1);
        return PasswordDigest.Verify(password, digest) ? id : null;
    }

    public static bool ActiveUserExists(SqliteConnection connection, long userId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM users WHERE id = $id AND status = 0";
        command.Parameters.AddWithValue("$id", userId);
        return command.ExecuteScalar() is not null;
    }

    public static bool NeedsRefresh(string? lastActiveAt, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(lastActiveAt))
        {
            return true;
        }

        if (!DateTime.TryParseExact(
                lastActiveAt,
                ["yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            && !DateTime.TryParse(
                lastActiveAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
        {
            return true;
        }

        var active = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        return active < now - ActivityRefreshRate;
    }
}
