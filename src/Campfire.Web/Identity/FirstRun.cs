using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Identity;

public readonly record struct FirstRunRequest(
    string? Name,
    string? EmailAddress,
    string? Password,
    string? IpAddress,
    string? UserAgent);

public readonly record struct FirstRunOutcome(bool Created, string? SessionToken)
{
    public static FirstRunOutcome Closed { get; } = new(false, null);
}

/// <summary>
/// Ports <c>FirstRun.create!</c> plus the session row from <c>start_new_session_for</c>.
/// </summary>
public static class FirstRun
{
    public const string FirstRoomName = "All Talk";
    public const string OpenRoomType = "Rooms::Open";
    public const int AdministratorRole = 1;
    public const int ActiveStatus = 0;

    public static FirstRunOutcome TryCreate(SqliteConnection connection, FirstRunRequest request)
    {
        var timestamp = Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            if (Accounts.Any(connection, transaction))
            {
                transaction.Rollback();
                return FirstRunOutcome.Closed;
            }

            try
            {
                Accounts.Insert(connection, transaction, Accounts.FirstRunName, timestamp);
            }
            catch (SqliteException exception) when (Accounts.IsSingletonViolation(exception))
            {
                transaction.Rollback();
                return FirstRunOutcome.Closed;
            }

            var userId = InsertUser(connection, transaction, request, timestamp);
            var roomId = InsertRoom(connection, transaction, userId, timestamp);
            // One membership: FirstRun grants the admin, and the user/open-room callbacks would
            // insert the same (room_id, user_id). The unique index allows one row (user.rooms.one?).
            InsertMembership(connection, transaction, roomId, userId, timestamp);
            var token = InsertSession(connection, transaction, userId, request, timestamp);
            transaction.Commit();
            return new FirstRunOutcome(true, token);
        }
        catch
        {
            RollbackQuietly(transaction);
            throw;
        }
    }

    public static string Timestamp(DateTime? utc = null)
    {
        return (utc ?? DateTime.UtcNow).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    private static long InsertUser(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FirstRunRequest request,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO users (created_at, email_address, name, password_digest, role, status, updated_at)
            VALUES ($created, $email, $name, $digest, $role, $status, $updated)
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$email", (object?)request.EmailAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", request.Name ?? "");
        command.Parameters.AddWithValue("$digest", (object?)PasswordDigest.Hash(request.Password) ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", AdministratorRole);
        command.Parameters.AddWithValue("$status", ActiveStatus);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.ExecuteNonQuery();
        return Accounts.LastInsertRowId(connection, transaction);
    }

    private static long InsertRoom(SqliteConnection connection, SqliteTransaction transaction, long creatorId, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rooms (created_at, creator_id, name, type, updated_at)
            VALUES ($created, $creator, $name, $type, $updated)
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$creator", creatorId);
        command.Parameters.AddWithValue("$name", FirstRoomName);
        command.Parameters.AddWithValue("$type", OpenRoomType);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.ExecuteNonQuery();
        return Accounts.LastInsertRowId(connection, transaction);
    }

    private static void InsertMembership(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long roomId,
        long userId,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO memberships (created_at, involvement, room_id, updated_at, user_id)
            VALUES ($created, 'mentions', $room, $updated, $user)
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$room", roomId);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$user", userId);
        command.ExecuteNonQuery();
    }

    // has_secure_token default: 24-character base58. Cookie signing is not this row.
    private static string InsertSession(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long userId,
        FirstRunRequest request,
        string timestamp)
    {
        var token = SessionToken.Generate();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions (created_at, ip_address, last_active_at, token, updated_at, user_agent, user_id)
            VALUES ($created, $ip, $active, $token, $updated, $agent, $user)
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$ip", (object?)request.IpAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", timestamp);
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$agent", (object?)request.UserAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("$user", userId);
        command.ExecuteNonQuery();
        return token;
    }

    private static void RollbackQuietly(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
            // Already completed by an earlier rollback.
        }
    }
}
