using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Identity;

public readonly record struct UserRow(long Id, string Name, string? EmailAddress, string? Bio, int Role, int Status);

public readonly record struct JoinCreateResult(bool DuplicateEmail, long? UserId, string? SessionToken);

public readonly record struct ProfileUpdate(
    bool SetName,
    string? Name,
    bool SetEmail,
    string? EmailAddress,
    bool SetPassword,
    string? Password,
    bool SetBio,
    string? Bio);

public readonly record struct SidebarRoom(long Id, string? Name, bool Direct, bool Unread);

/// <summary>
/// User rows, join, profile, deactivate, and ban/unban. Role and status integers match the
/// Rails enums (<c>member administrator bot</c>, <c>active deactivated banned</c>).
/// </summary>
public static class Users
{
    public const int MemberRole = 0;
    public const int AdministratorRole = 1;
    public const int BotRole = 2;
    public const int ActiveStatus = 0;
    public const int DeactivatedStatus = 1;
    public const int BannedStatus = 2;
    public const int DirectPlaceholders = 20;

    public static UserRow? Find(SqliteConnection connection, long id)
    {
        return Find(connection, id, activeOnly: false);
    }

    public static UserRow? FindActive(SqliteConnection connection, long id)
    {
        return Find(connection, id, activeOnly: true);
    }

    public static bool CanAdminister(SqliteConnection connection, long userId)
    {
        var user = Find(connection, userId);
        return user is not null && user.Value.Role == AdministratorRole;
    }

    public static string Initials(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        var matches = Regex.Matches(name, @"\b\w");
        var chars = new char[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            chars[i] = matches[i].Value[0];
        }

        return new string(chars);
    }

    public static JoinCreateResult TryJoin(
        SqliteConnection connection,
        string filesRoot,
        string? name,
        string? emailAddress,
        string? password,
        string? ipAddress,
        string? userAgent,
        BlobPayload? avatar)
    {
        var timestamp = FirstRun.Timestamp();
        string? stagedKey = null;
        string? stagedPath = null;
        if (avatar is not null)
        {
            stagedKey = Attachments.NewKey();
            stagedPath = Attachments.Stage(filesRoot, stagedKey, avatar.Value.Bytes);
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            var userId = InsertMember(connection, transaction, name, emailAddress, password, timestamp);
            if (avatar is not null && stagedKey is not null)
            {
                Attachments.Insert(
                    connection,
                    transaction,
                    Attachments.UserType,
                    userId,
                    Attachments.AvatarName,
                    stagedKey,
                    avatar.Value,
                    timestamp);
            }

            GrantOpenRooms(connection, transaction, userId, timestamp);
            var token = Sessions.Start(connection, transaction, userId, ipAddress, userAgent, timestamp);
            transaction.Commit();
            return new JoinCreateResult(false, userId, token);
        }
        catch (SqliteException exception) when (IsDuplicateEmail(exception))
        {
            Rollback(transaction);
            Attachments.DeleteFile(stagedPath);
            return new JoinCreateResult(true, null, null);
        }
        catch
        {
            Rollback(transaction);
            Attachments.DeleteFile(stagedPath);
            throw;
        }
    }

    public static void UpdateProfile(
        SqliteConnection connection,
        string filesRoot,
        long userId,
        ProfileUpdate update,
        BlobPayload? avatar)
    {
        var timestamp = FirstRun.Timestamp();
        if (update.SetName || update.SetEmail || update.SetPassword || update.SetBio)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE users
                SET name = CASE WHEN $setName THEN $name ELSE name END,
                    email_address = CASE WHEN $setEmail THEN $email ELSE email_address END,
                    password_digest = CASE WHEN $setPassword THEN $digest ELSE password_digest END,
                    bio = CASE WHEN $setBio THEN $bio ELSE bio END,
                    updated_at = $updated
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$setName", update.SetName ? 1 : 0);
            command.Parameters.AddWithValue("$name", update.Name ?? "");
            command.Parameters.AddWithValue("$setEmail", update.SetEmail ? 1 : 0);
            command.Parameters.AddWithValue("$email", (object?)update.EmailAddress ?? DBNull.Value);
            var changePassword = update.SetPassword && !string.IsNullOrEmpty(update.Password);
            command.Parameters.AddWithValue("$setPassword", changePassword ? 1 : 0);
            command.Parameters.AddWithValue("$digest", (object?)PasswordDigest.Hash(update.Password) ?? DBNull.Value);
            command.Parameters.AddWithValue("$setBio", update.SetBio ? 1 : 0);
            command.Parameters.AddWithValue("$bio", (object?)update.Bio ?? DBNull.Value);
            command.Parameters.AddWithValue("$updated", timestamp);
            command.Parameters.AddWithValue("$id", userId);
            command.ExecuteNonQuery();
        }

        if (avatar is not null)
        {
            Attachments.Replace(
                connection,
                filesRoot,
                Attachments.UserType,
                userId,
                Attachments.AvatarName,
                avatar.Value);
        }
    }

    public static void SetRole(SqliteConnection connection, long userId, string? role)
    {
        var chosen = LastSubmitted(role);
        var stored = chosen == "administrator" ? AdministratorRole : MemberRole;
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET role = $role, updated_at = $updated WHERE id = $id";
        command.Parameters.AddWithValue("$role", stored);
        command.Parameters.AddWithValue("$updated", FirstRun.Timestamp());
        command.Parameters.AddWithValue("$id", userId);
        command.ExecuteNonQuery();
    }

    private static string? LastSubmitted(string? role)
    {
        if (string.IsNullOrEmpty(role))
        {
            return role;
        }

        var comma = role.LastIndexOf(',');
        return (comma < 0 ? role : role[(comma + 1)..]).Trim();
    }

    public static void Deactivate(SqliteConnection connection, long userId)
    {
        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            string? email = null;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT email_address FROM users WHERE id = $id";
                read.Parameters.AddWithValue("$id", userId);
                if (read.ExecuteScalar() is string value)
                {
                    email = value;
                }
            }

            using (var memberships = connection.CreateCommand())
            {
                memberships.Transaction = transaction;
                memberships.CommandText = """
                    DELETE FROM memberships
                    WHERE id IN (
                        SELECT memberships.id
                        FROM memberships
                        JOIN rooms ON rooms.id = memberships.room_id
                        WHERE memberships.user_id = $user AND rooms.type != 'Rooms::Direct'
                    )
                    """;
                memberships.Parameters.AddWithValue("$user", userId);
                memberships.ExecuteNonQuery();
            }

            DeleteByUser(connection, transaction, "push_subscriptions", userId);
            DeleteByUser(connection, transaction, "searches", userId);
            DeleteByUser(connection, transaction, "sessions", userId);

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE users
                SET status = $status, email_address = $email, updated_at = $updated
                WHERE id = $id
                """;
            update.Parameters.AddWithValue("$status", DeactivatedStatus);
            update.Parameters.AddWithValue("$email", (object?)DeactivatedEmail(email) ?? DBNull.Value);
            update.Parameters.AddWithValue("$updated", timestamp);
            update.Parameters.AddWithValue("$id", userId);
            update.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    /// <summary>
    /// <c>User#ban</c>. A private or invalid session IP fails <c>Ban</c> validation and rolls
    /// the whole ban back, matching <c>bans.create!</c>. Message removal is a later job and is
    /// not done here.
    /// </summary>
    public static bool TryBan(SqliteConnection connection, long userId)
    {
        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            var ips = SessionIps(connection, transaction, userId);
            foreach (var ip in ips)
            {
                if (!Bans.IsRecordablePublicAddress(ip))
                {
                    Rollback(transaction);
                    return false;
                }
            }

            foreach (var ip in ips)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO bans (created_at, ip_address, updated_at, user_id)
                    VALUES ($created, $ip, $updated, $user)
                    """;
                insert.Parameters.AddWithValue("$created", timestamp);
                insert.Parameters.AddWithValue("$ip", ip);
                insert.Parameters.AddWithValue("$updated", timestamp);
                insert.Parameters.AddWithValue("$user", userId);
                insert.ExecuteNonQuery();
            }

            SetStatus(connection, transaction, userId, BannedStatus, timestamp);
            DeleteByUser(connection, transaction, "sessions", userId);
            transaction.Commit();
            return true;
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static void Unban(SqliteConnection connection, long userId)
    {
        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM bans WHERE user_id = $user";
                delete.Parameters.AddWithValue("$user", userId);
                delete.ExecuteNonQuery();
            }

            SetStatus(connection, transaction, userId, ActiveStatus, timestamp);
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static IReadOnlyList<UserRow> List(SqliteConnection connection, bool includeBanned, int limit, int offset)
    {
        var status = includeBanned ? "status IN (0, 2)" : "status = 0";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, name, email_address, bio, role, status
            FROM users
            WHERE {status} AND role != {BotRole}
            ORDER BY LOWER(name), id
            LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return ReadUsers(command);
    }

    public static (IReadOnlyList<SidebarRoom> Directs, IReadOnlyList<SidebarRoom> Others, IReadOnlyList<string> Placeholders) Sidebar(
        SqliteConnection connection,
        long userId)
    {
        var rooms = new List<SidebarRoom>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT rooms.id, rooms.name, rooms.type, rooms.updated_at, memberships.unread_at
                FROM memberships
                JOIN rooms ON rooms.id = memberships.room_id
                WHERE memberships.user_id = $user
                  AND memberships.involvement != 'invisible'
                """;
            command.Parameters.AddWithValue("$user", userId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                rooms.Add(new SidebarRoom(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    type == "Rooms::Direct",
                    !reader.IsDBNull(4)));
            }
        }

        var directs = rooms.Where(room => room.Direct).ToList();
        var others = rooms.Where(room => !room.Direct).OrderBy(room => room.Name, StringComparer.OrdinalIgnoreCase).ToList();
        directs.Sort((left, right) => string.Compare(UpdatedAt(connection, right.Id), UpdatedAt(connection, left.Id), StringComparison.Ordinal));

        var exclude = DirectRoomUserIds(connection, userId);
        exclude.Add(userId);
        var limit = Math.Max(0, DirectPlaceholders - exclude.Count);
        var placeholders = new List<string>();
        if (limit > 0)
        {
            using var command = connection.CreateCommand();
            var parameters = new List<string>();
            var index = 0;
            foreach (var id in exclude)
            {
                var name = "$e" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parameters.Add(name);
                command.Parameters.AddWithValue(name, id);
                index++;
            }

            command.CommandText = $"""
                SELECT name
                FROM users
                WHERE status = {ActiveStatus} AND id NOT IN ({string.Join(", ", parameters)})
                ORDER BY created_at, id
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                placeholders.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            }
        }

        return (directs, others, placeholders);
    }

    private static string? UpdatedAt(SqliteConnection connection, long roomId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT updated_at FROM rooms WHERE id = $id";
        command.Parameters.AddWithValue("$id", roomId);
        return command.ExecuteScalar() as string;
    }

    private static HashSet<long> DirectRoomUserIds(SqliteConnection connection, long userId)
    {
        var ids = new HashSet<long>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT memberships.user_id
            FROM memberships
            WHERE memberships.room_id IN (
                SELECT rooms.id
                FROM rooms
                JOIN memberships mine ON mine.room_id = rooms.id
                WHERE mine.user_id = $user AND rooms.type = 'Rooms::Direct'
            )
            """;
        command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static UserRow? Find(SqliteConnection connection, long id, bool activeOnly)
    {
        using var command = connection.CreateCommand();
        command.CommandText = activeOnly
            ? """
                SELECT id, name, email_address, bio, role, status
                FROM users
                WHERE id = $id AND status = 0
                """
            : """
                SELECT id, name, email_address, bio, role, status
                FROM users
                WHERE id = $id
                """;
        command.Parameters.AddWithValue("$id", id);
        var users = ReadUsers(command);
        return users.Count == 0 ? null : users[0];
    }

    private static List<UserRow> ReadUsers(SqliteCommand command)
    {
        var users = new List<UserRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            users.Add(new UserRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5)));
        }

        return users;
    }

    private static long InsertMember(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? name,
        string? emailAddress,
        string? password,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO users (created_at, email_address, name, password_digest, role, status, updated_at)
            VALUES ($created, $email, $name, $digest, $role, $status, $updated)
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$email", (object?)emailAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", name ?? "");
        command.Parameters.AddWithValue("$digest", (object?)PasswordDigest.Hash(password) ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", MemberRole);
        command.Parameters.AddWithValue("$status", ActiveStatus);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.ExecuteNonQuery();
        return Accounts.LastInsertRowId(connection, transaction);
    }

    private static void GrantOpenRooms(SqliteConnection connection, SqliteTransaction transaction, long userId, string timestamp)
    {
        var roomIds = new List<long>();
        using (var rooms = connection.CreateCommand())
        {
            rooms.Transaction = transaction;
            rooms.CommandText = "SELECT id FROM rooms WHERE type = $type";
            rooms.Parameters.AddWithValue("$type", FirstRun.OpenRoomType);
            using var reader = rooms.ExecuteReader();
            while (reader.Read())
            {
                roomIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var roomId in roomIds)
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
    }

    private static List<string> SessionIps(SqliteConnection connection, SqliteTransaction transaction, long userId)
    {
        var ips = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT ip_address
            FROM sessions
            WHERE user_id = $user AND ip_address IS NOT NULL AND ip_address != ''
            """;
        command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var ip = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(ip))
            {
                ips.Add(ip.Trim());
            }
        }

        return ips;
    }

    private static void SetStatus(SqliteConnection connection, SqliteTransaction transaction, long userId, int status, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE users SET status = $status, updated_at = $updated WHERE id = $id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$id", userId);
        command.ExecuteNonQuery();
    }

    private static void DeleteByUser(SqliteConnection connection, SqliteTransaction transaction, string table, long userId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM \"{table}\" WHERE user_id = $user";
        command.Parameters.AddWithValue("$user", userId);
        command.ExecuteNonQuery();
    }

    private static string? DeactivatedEmail(string? emailAddress)
    {
        if (string.IsNullOrEmpty(emailAddress))
        {
            return emailAddress;
        }

        var uuid = Guid.NewGuid().ToString("D");
        return emailAddress.Replace("@", $"-deactivated-{uuid}@", StringComparison.Ordinal);
    }

    private static bool IsDuplicateEmail(SqliteException exception)
    {
        return exception.SqliteErrorCode == 19
            && exception.Message.Contains("email_address", StringComparison.OrdinalIgnoreCase);
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
