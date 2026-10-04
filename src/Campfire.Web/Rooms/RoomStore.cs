using System.Globalization;
using Campfire.Web.Identity;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Rooms;

public readonly record struct RoomRow(long Id, long CreatorId, string? Name, string Type);

public readonly record struct RoomUser(long Id, string Name);

public readonly record struct RefreshMessage(long Id, long CreatorId, string CreatedAt, string UpdatedAt);

public enum RoomScope
{
    Member,
    WithoutDirects,
    Directs,
}

/// <summary>
/// Room rows and the create/update rules from <c>Room</c>, <c>Rooms::Open</c>, <c>Rooms::Closed</c>,
/// and <c>Rooms::Direct</c>. Direct rooms cannot change type. Open rooms grant every active user
/// when the type becomes <c>Rooms::Open</c>.
/// </summary>
public static class RoomStore
{
    public const string OpenType = "Rooms::Open";
    public const string ClosedType = "Rooms::Closed";
    public const string DirectType = "Rooms::Direct";
    public const string DefaultName = "New room";
    public const int RefreshPageSize = 40;

    public static RoomRow? Find(SqliteConnection connection, long roomId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, creator_id, name, type
            FROM rooms
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", roomId);
        return ReadOne(command);
    }

    public static RoomRow? FindForUser(SqliteConnection connection, long userId, long roomId, RoomScope scope)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT rooms.id, rooms.creator_id, rooms.name, rooms.type
            FROM rooms
            JOIN memberships ON memberships.room_id = rooms.id AND memberships.user_id = $user
            WHERE rooms.id = $id
            {ScopeSql(scope)}
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$id", roomId);
        return ReadOne(command);
    }

    public static long? LastIdForUser(SqliteConnection connection, long userId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rooms.id
            FROM rooms
            JOIN memberships ON memberships.room_id = rooms.id AND memberships.user_id = $user
            ORDER BY rooms.id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$user", userId);
        var value = command.ExecuteScalar();
        return value is long id ? id : null;
    }

    public static IReadOnlyList<RoomUser> ActiveUsers(SqliteConnection connection)
    {
        var users = new List<RoomUser>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name
            FROM users
            WHERE status = $active
            ORDER BY LOWER(name), id
            """;
        command.Parameters.AddWithValue("$active", Users.ActiveStatus);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            users.Add(new RoomUser(reader.GetInt64(0), reader.GetString(1)));
        }

        return users;
    }

    public static IReadOnlyList<long> MemberIds(SqliteConnection connection, long roomId)
    {
        var ids = new List<long>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM memberships WHERE room_id = $room ORDER BY user_id";
        command.Parameters.AddWithValue("$room", roomId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    public static IReadOnlyList<RoomUser> MembersExcept(SqliteConnection connection, long roomId, long exceptUserId)
    {
        var users = new List<RoomUser>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT users.id, users.name
            FROM users
            JOIN memberships ON memberships.user_id = users.id
            WHERE memberships.room_id = $room AND users.id != $except
            ORDER BY LOWER(users.name), users.id
            """;
        command.Parameters.AddWithValue("$room", roomId);
        command.Parameters.AddWithValue("$except", exceptUserId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            users.Add(new RoomUser(reader.GetInt64(0), reader.GetString(1)));
        }

        return users;
    }

    public static long CreateOpen(SqliteConnection connection, long creatorId, string? name)
    {
        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            var roomId = Insert(connection, transaction, creatorId, name, OpenType, timestamp);
            MembershipStore.GrantActiveUsers(connection, transaction, roomId, MembershipStore.Mentions, timestamp);
            transaction.Commit();
            return roomId;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static long CreateClosed(SqliteConnection connection, long creatorId, string? name, IReadOnlyList<long> userIds)
    {
        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            var roomId = Insert(connection, transaction, creatorId, name, ClosedType, timestamp);
            MembershipStore.Revise(connection, transaction, roomId, userIds, MembershipStore.Mentions, timestamp);
            transaction.Commit();
            return roomId;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static long FindOrCreateDirect(SqliteConnection connection, long creatorId, IReadOnlyList<long> userIds)
    {
        var distinct = userIds.Distinct().ToArray();
        var existing = FindDirect(connection, distinct);
        if (existing is not null)
        {
            return existing.Value;
        }

        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            var again = FindDirect(connection, distinct, transaction);
            if (again is not null)
            {
                transaction.Commit();
                return again.Value;
            }

            var roomId = Insert(connection, transaction, creatorId, null, DirectType, timestamp);
            MembershipStore.Grant(connection, transaction, roomId, distinct, MembershipStore.Everything, timestamp);
            transaction.Commit();
            return roomId;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static bool UpdateShared(
        SqliteConnection connection,
        RoomRow room,
        string? name,
        string type,
        IReadOnlyList<long>? reviseToUserIds)
    {
        if (room.Type == DirectType && type != DirectType)
        {
            return false;
        }

        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE rooms
                    SET name = $name, type = $type, updated_at = $updated
                    WHERE id = $id
                    """;
                command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
                command.Parameters.AddWithValue("$type", type);
                command.Parameters.AddWithValue("$updated", timestamp);
                command.Parameters.AddWithValue("$id", room.Id);
                command.ExecuteNonQuery();
            }

            if (type == OpenType && room.Type != OpenType)
            {
                MembershipStore.GrantActiveUsers(connection, transaction, room.Id, MembershipStore.Mentions, timestamp);
            }

            if (reviseToUserIds is not null)
            {
                MembershipStore.Revise(
                    connection,
                    transaction,
                    room.Id,
                    reviseToUserIds,
                    MembershipStore.Mentions,
                    timestamp);
            }

            transaction.Commit();
            return true;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static void Destroy(SqliteConnection connection, long roomId)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            Execute(connection, transaction, """
                DELETE FROM boosts
                WHERE message_id IN (SELECT id FROM messages WHERE room_id = $id)
                """, roomId);
            Execute(connection, transaction, """
                DELETE FROM message_search_index
                WHERE rowid IN (SELECT id FROM messages WHERE room_id = $id)
                """, roomId);
            Execute(connection, transaction, """
                DELETE FROM action_text_rich_texts
                WHERE record_type = 'Message'
                  AND record_id IN (SELECT id FROM messages WHERE room_id = $id)
                """, roomId);
            Execute(connection, transaction, "DELETE FROM messages WHERE room_id = $id", roomId);
            Execute(connection, transaction, "DELETE FROM memberships WHERE room_id = $id", roomId);
            Execute(connection, transaction, "DELETE FROM rooms WHERE id = $id", roomId);
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static (IReadOnlyList<RefreshMessage> Created, IReadOnlyList<RefreshMessage> Updated) RefreshSince(
        SqliteConnection connection,
        long roomId,
        string since)
    {
        var created = Page(
            connection,
            roomId,
            "created_at > $since",
            since,
            ascending: true);
        var updated = Page(
            connection,
            roomId,
            "updated_at > $since AND created_at <= $since",
            since,
            ascending: false);
        updated.Reverse();
        return (created, updated);
    }

    public static string DisplayName(SqliteConnection connection, RoomRow room, long userId, string? viewerName)
    {
        if (room.Type != DirectType)
        {
            return room.Name ?? "";
        }

        var others = MembersExcept(connection, room.Id, userId);
        if (others.Count == 0)
        {
            return viewerName ?? "";
        }

        if (others.Count == 1)
        {
            return others[0].Name;
        }

        return string.Join(", ", others.Take(others.Count - 1).Select(user => user.Name))
            + " and "
            + others[^1].Name;
    }

    public static bool CanAdminister(UserRow user, RoomRow room)
    {
        return user.Role == Users.AdministratorRole || user.Id == room.CreatorId;
    }

    public static bool CanCreateSharedRooms(SqliteConnection connection, long userId)
    {
        var user = Users.Find(connection, userId);
        if (user is null)
        {
            return false;
        }

        if (user.Value.Role == Users.AdministratorRole)
        {
            return true;
        }

        return !Accounts.RestrictsRoomCreation(Accounts.First(connection)?.Settings);
    }

    private static long? FindDirect(
        SqliteConnection connection,
        IReadOnlyList<long> userIds,
        SqliteTransaction? transaction = null)
    {
        if (userIds.Count == 0)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = new string[userIds.Count];
        for (var i = 0; i < userIds.Count; i++)
        {
            names[i] = "$u" + i.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(names[i], userIds[i]);
        }

        var list = string.Join(", ", names);
        command.CommandText = $"""
            SELECT rooms.id
            FROM rooms
            JOIN memberships ON memberships.room_id = rooms.id
            WHERE rooms.type = $type
            GROUP BY rooms.id
            HAVING COUNT(memberships.user_id) = $count
               AND SUM(CASE WHEN memberships.user_id IN ({list}) THEN 1 ELSE 0 END) = $count
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$type", DirectType);
        command.Parameters.AddWithValue("$count", userIds.Count);
        var value = command.ExecuteScalar();
        return value is long id ? id : null;
    }

    private static long Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long creatorId,
        string? name,
        string type,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rooms (created_at, creator_id, name, type, updated_at)
            VALUES ($created, $creator, $name, $type, $updated)
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$creator", creatorId);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.ExecuteNonQuery();
        return Accounts.LastInsertRowId(connection, transaction);
    }

    private static List<RefreshMessage> Page(
        SqliteConnection connection,
        long roomId,
        string predicate,
        string since,
        bool ascending)
    {
        var messages = new List<RefreshMessage>();
        using var command = connection.CreateCommand();
        var direction = ascending ? "ASC" : "DESC";
        command.CommandText = $"""
            SELECT id, creator_id, created_at, updated_at
            FROM messages
            WHERE room_id = $room AND {predicate}
            ORDER BY created_at {direction}, id {direction}
            LIMIT {RefreshPageSize.ToString(CultureInfo.InvariantCulture)}
            """;
        command.Parameters.AddWithValue("$room", roomId);
        command.Parameters.AddWithValue("$since", since);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(new RefreshMessage(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return messages;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long roomId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", roomId);
        command.ExecuteNonQuery();
    }

    private static string ScopeSql(RoomScope scope)
    {
        return scope switch
        {
            RoomScope.WithoutDirects => "AND rooms.type != 'Rooms::Direct'",
            RoomScope.Directs => "AND rooms.type = 'Rooms::Direct'",
            _ => "",
        };
    }

    private static RoomRow? ReadOne(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new RoomRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3));
    }

    private static void Rollback(SqliteTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
