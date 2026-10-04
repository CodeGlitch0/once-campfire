using Campfire.Web.Identity;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Rooms;

public readonly record struct BoostRow(
    long Id,
    long MessageId,
    long BoosterId,
    string Content,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Boost rows. <c>belongs_to :message, touch: true</c> updates the message, which touches the room.
/// <c>content</c> is stored as posted. HTML <c>maxlength="16"</c> is the only cap; SQLite does not enforce VARCHAR(16).
/// </summary>
public static class BoostStore
{
    public static bool Reachable(SqliteConnection connection, long userId, long messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM messages
            JOIN memberships ON memberships.room_id = messages.room_id AND memberships.user_id = $user
            WHERE messages.id = $id
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$id", messageId);
        return command.ExecuteScalar() is not null;
    }

    public static IReadOnlyList<BoostRow> Ordered(SqliteConnection connection, long messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Select + """
             WHERE message_id = $message
             ORDER BY created_at, id
            """;
        command.Parameters.AddWithValue("$message", messageId);
        var rows = new List<BoostRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public static BoostRow? FindOwn(SqliteConnection connection, long messageId, long boostId, long boosterId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Select + """
             WHERE id = $id AND message_id = $message AND booster_id = $booster
            """;
        command.Parameters.AddWithValue("$id", boostId);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$booster", boosterId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static BoostRow Create(SqliteConnection connection, long messageId, long boosterId, string content)
    {
        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            long id;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO boosts (booster_id, content, created_at, message_id, updated_at)
                    VALUES ($booster, $content, $created, $message, $updated)
                    """;
                command.Parameters.AddWithValue("$booster", boosterId);
                command.Parameters.AddWithValue("$content", content);
                command.Parameters.AddWithValue("$created", timestamp);
                command.Parameters.AddWithValue("$message", messageId);
                command.Parameters.AddWithValue("$updated", timestamp);
                command.ExecuteNonQuery();
                id = Accounts.LastInsertRowId(connection, transaction);
            }

            TouchMessage(connection, transaction, messageId, timestamp);
            transaction.Commit();
            return new BoostRow(id, messageId, boosterId, content, timestamp, timestamp);
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static void Destroy(SqliteConnection connection, BoostRow boost)
    {
        var timestamp = FirstRun.Timestamp();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM boosts WHERE id = $id AND booster_id = $booster";
            command.Parameters.AddWithValue("$id", boost.Id);
            command.Parameters.AddWithValue("$booster", boost.BoosterId);
            command.ExecuteNonQuery();
            TouchMessage(connection, transaction, boost.MessageId, timestamp);
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    private static void TouchMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        string timestamp)
    {
        using (var message = connection.CreateCommand())
        {
            message.Transaction = transaction;
            message.CommandText = "UPDATE messages SET updated_at = $updated WHERE id = $id";
            message.Parameters.AddWithValue("$updated", timestamp);
            message.Parameters.AddWithValue("$id", messageId);
            message.ExecuteNonQuery();
        }

        using var room = connection.CreateCommand();
        room.Transaction = transaction;
        room.CommandText = """
            UPDATE rooms SET updated_at = $updated
            WHERE id = (SELECT room_id FROM messages WHERE id = $id)
            """;
        room.Parameters.AddWithValue("$updated", timestamp);
        room.Parameters.AddWithValue("$id", messageId);
        room.ExecuteNonQuery();
    }

    private static BoostRow Read(SqliteDataReader reader)
    {
        return new BoostRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
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

    private const string Select = """
        SELECT id, message_id, booster_id, content, created_at, updated_at
        FROM boosts
        """;
}
