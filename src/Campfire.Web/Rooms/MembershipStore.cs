using System.Globalization;
using Campfire.Web.Identity;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Rooms;

public readonly record struct MembershipRow(
    long Id,
    long RoomId,
    long UserId,
    string? Involvement,
    string? ConnectedAt,
    int Connections,
    string? UnreadAt);

/// <summary>
/// Membership grant/revoke plus Rails <c>Membership</c> unread and <c>Membership::Connectable</c> state.
/// Connection TTL is 60 seconds. <c>connected?</c> is <c>connected_at</c> within that window, not the counter alone.
/// </summary>
public static class MembershipStore
{
    public const int ConnectionTtlSeconds = 60;
    public const string Mentions = "mentions";
    public const string Everything = "everything";

    public static readonly string[] Involvements = ["invisible", "nothing", "mentions", "everything"];

    public static bool IsInvolvement(string? value)
    {
        return value is "invisible" or "nothing" or "mentions" or "everything";
    }

    public static MembershipRow? Find(SqliteConnection connection, long membershipId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, room_id, user_id, involvement, connected_at, connections, unread_at
            FROM memberships
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", membershipId);
        return ReadOne(command);
    }

    public static MembershipRow? FindForUser(SqliteConnection connection, long userId, long roomId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, room_id, user_id, involvement, connected_at, connections, unread_at
            FROM memberships
            WHERE user_id = $user AND room_id = $room
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$room", roomId);
        return ReadOne(command);
    }

    public static void Grant(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long roomId,
        IReadOnlyList<long> userIds,
        string involvement,
        string timestamp)
    {
        foreach (var userId in userIds.Distinct())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // insert_all of an existing member would raise. Revise still grants the users who stay,
            // and the Rails closed-room update test expects that to succeed, so duplicates are ignored.
            command.CommandText = """
                INSERT OR IGNORE INTO memberships (created_at, involvement, room_id, updated_at, user_id)
                VALUES ($created, $involvement, $room, $updated, $user)
                """;
            command.Parameters.AddWithValue("$created", timestamp);
            command.Parameters.AddWithValue("$involvement", involvement);
            command.Parameters.AddWithValue("$room", roomId);
            command.Parameters.AddWithValue("$updated", timestamp);
            command.Parameters.AddWithValue("$user", userId);
            command.ExecuteNonQuery();
        }
    }

    public static void GrantActiveUsers(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long roomId,
        string involvement,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO memberships (created_at, involvement, room_id, updated_at, user_id)
            SELECT $created, $involvement, $room, $updated, users.id
            FROM users
            WHERE users.status = $active
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$involvement", involvement);
        command.Parameters.AddWithValue("$room", roomId);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$active", Users.ActiveStatus);
        command.ExecuteNonQuery();
    }

    public static void RevokeExcept(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long roomId,
        IReadOnlyList<long> keepUserIds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (keepUserIds.Count == 0)
        {
            command.CommandText = "DELETE FROM memberships WHERE room_id = $room";
            command.Parameters.AddWithValue("$room", roomId);
            command.ExecuteNonQuery();
            return;
        }

        var names = new string[keepUserIds.Count];
        for (var i = 0; i < keepUserIds.Count; i++)
        {
            names[i] = "$k" + i.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(names[i], keepUserIds[i]);
        }

        command.CommandText = $"""
            DELETE FROM memberships
            WHERE room_id = $room AND user_id NOT IN ({string.Join(", ", names)})
            """;
        command.Parameters.AddWithValue("$room", roomId);
        command.ExecuteNonQuery();
    }

    public static void Revise(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long roomId,
        IReadOnlyList<long> grantedUserIds,
        string involvement,
        string timestamp)
    {
        var existing = ExistingUserIds(connection, transaction, grantedUserIds);
        if (existing.Count > 0)
        {
            Grant(connection, transaction, roomId, existing, involvement, timestamp);
        }

        RevokeExcept(connection, transaction, roomId, existing);
    }

    public static bool UpdateInvolvement(SqliteConnection connection, long membershipId, string involvement)
    {
        if (!IsInvolvement(involvement))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memberships
            SET involvement = $involvement, updated_at = $updated
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$involvement", involvement);
        command.Parameters.AddWithValue("$updated", FirstRun.Timestamp());
        command.Parameters.AddWithValue("$id", membershipId);
        return command.ExecuteNonQuery() == 1;
    }

    public static void Read(SqliteConnection connection, long membershipId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memberships
            SET unread_at = NULL, updated_at = $updated
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$updated", FirstRun.Timestamp());
        command.Parameters.AddWithValue("$id", membershipId);
        command.ExecuteNonQuery();
    }

    public static bool IsUnread(MembershipRow row)
    {
        return !string.IsNullOrEmpty(row.UnreadAt);
    }

    public static bool IsConnected(MembershipRow row, DateTime? utcNow = null)
    {
        if (!TryParse(row.ConnectedAt, out var connectedAt))
        {
            return false;
        }

        var now = utcNow ?? DateTime.UtcNow;
        return connectedAt >= now.AddSeconds(-ConnectionTtlSeconds);
    }

    public static void Connected(SqliteConnection connection, long membershipId, DateTime? utcNow = null)
    {
        var row = Find(connection, membershipId);
        if (row is null)
        {
            return;
        }

        var now = utcNow ?? DateTime.UtcNow;
        var connections = IsConnected(row.Value, now) ? row.Value.Connections + 1 : 1;
        TouchConnection(connection, membershipId, connections, now, clearUnread: false);
    }

    public static void Disconnected(SqliteConnection connection, long membershipId, DateTime? utcNow = null)
    {
        var row = Find(connection, membershipId);
        if (row is null)
        {
            return;
        }

        var now = utcNow ?? DateTime.UtcNow;
        var connections = IsConnected(row.Value, now) ? row.Value.Connections - 1 : 0;
        if (connections < 1)
        {
            ClearConnection(connection, membershipId, now);
            return;
        }

        TouchConnection(connection, membershipId, connections, now, clearUnread: false, touchConnectedAt: false);
    }

    public static void Present(SqliteConnection connection, long membershipId, DateTime? utcNow = null)
    {
        var row = Find(connection, membershipId);
        if (row is null)
        {
            return;
        }

        var now = utcNow ?? DateTime.UtcNow;
        var connections = IsConnected(row.Value, now) ? row.Value.Connections + 1 : 1;
        TouchConnection(connection, membershipId, connections, now, clearUnread: true);
    }

    public static void RefreshConnection(SqliteConnection connection, long membershipId, DateTime? utcNow = null)
    {
        var row = Find(connection, membershipId);
        if (row is null)
        {
            return;
        }

        var now = utcNow ?? DateTime.UtcNow;
        if (!IsConnected(row.Value, now))
        {
            TouchConnection(connection, membershipId, 1, now, clearUnread: false);
            return;
        }

        TouchConnection(connection, membershipId, row.Value.Connections, now, clearUnread: false);
    }

    public static void DisconnectAll(SqliteConnection connection, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memberships
            SET connected_at = NULL, connections = 0, updated_at = $updated
            WHERE connected_at IS NOT NULL AND connected_at >= $cutoff
            """;
        command.Parameters.AddWithValue("$updated", Format(now));
        command.Parameters.AddWithValue("$cutoff", Format(now.AddSeconds(-ConnectionTtlSeconds)));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// <c>Room#unread_memberships</c>: visible, disconnected members other than the creator.
    /// </summary>
    public static void MarkUnreadExcept(
        SqliteConnection connection,
        long roomId,
        long exceptUserId,
        string unreadAt,
        DateTime? utcNow = null,
        SqliteTransaction? transaction = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE memberships
            SET unread_at = $unread, updated_at = $updated
            WHERE room_id = $room
              AND user_id != $except
              AND involvement != 'invisible'
              AND (connected_at IS NULL OR connected_at < $cutoff)
            """;
        command.Parameters.AddWithValue("$unread", unreadAt);
        command.Parameters.AddWithValue("$updated", Format(now));
        command.Parameters.AddWithValue("$room", roomId);
        command.Parameters.AddWithValue("$except", exceptUserId);
        command.Parameters.AddWithValue("$cutoff", Format(now.AddSeconds(-ConnectionTtlSeconds)));
        command.ExecuteNonQuery();
    }

    public static string Format(DateTime utc)
    {
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    public static string FromEpochMilliseconds(long milliseconds)
    {
        return Format(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime);
    }

    private static void TouchConnection(
        SqliteConnection connection,
        long membershipId,
        int connections,
        DateTime utcNow,
        bool clearUnread,
        bool touchConnectedAt = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText = clearUnread
            ? """
                UPDATE memberships
                SET connections = $connections, connected_at = $connected, unread_at = NULL, updated_at = $updated
                WHERE id = $id
                """
            : touchConnectedAt
                ? """
                    UPDATE memberships
                    SET connections = $connections, connected_at = $connected, updated_at = $updated
                    WHERE id = $id
                    """
                : """
                    UPDATE memberships
                    SET connections = $connections, updated_at = $updated
                    WHERE id = $id
                    """;
        command.Parameters.AddWithValue("$connections", connections);
        command.Parameters.AddWithValue("$connected", Format(utcNow));
        command.Parameters.AddWithValue("$updated", Format(utcNow));
        command.Parameters.AddWithValue("$id", membershipId);
        command.ExecuteNonQuery();
    }

    private static void ClearConnection(SqliteConnection connection, long membershipId, DateTime utcNow)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memberships
            SET connections = 0, connected_at = NULL, updated_at = $updated
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$updated", Format(utcNow));
        command.Parameters.AddWithValue("$id", membershipId);
        command.ExecuteNonQuery();
    }

    private static List<long> ExistingUserIds(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IReadOnlyList<long> userIds)
    {
        var existing = new List<long>();
        if (userIds.Count == 0)
        {
            return existing;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = new string[userIds.Count];
        for (var i = 0; i < userIds.Count; i++)
        {
            names[i] = "$u" + i.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(names[i], userIds[i]);
        }

        command.CommandText = $"SELECT id FROM users WHERE id IN ({string.Join(", ", names)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            existing.Add(reader.GetInt64(0));
        }

        return existing;
    }

    private static MembershipRow? ReadOne(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MembershipRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static bool TryParse(string? stored, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(stored))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                stored,
                "yyyy-MM-dd HH:mm:ss.ffffff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc))
        {
            return DateTime.TryParse(
                stored,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc);
        }

        return true;
    }
}
