using System.Globalization;
using System.Text.RegularExpressions;
using Campfire.Web.Identity;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Rooms;

public readonly record struct MessageRow(
    long Id,
    long RoomId,
    long CreatorId,
    string ClientMessageId,
    string Body,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Message rows, Action Text <c>body</c> storage, and <c>Message::Pagination</c>.
/// Page size is Rails <c>Message::Pagination::PAGE_SIZE</c> (40), not a local guess.
/// Ordering is <c>created_at</c> then <c>id</c> so a timestamp tie keeps insertion order
/// when the last page is taken from the reverse query (<c>ordered.last</c>).
/// </summary>
public static partial class MessageStore
{
    public const int PageSize = 40;
    public const string RichTextName = "body";
    public const string RichTextType = "Message";

    public static bool CanAdminister(UserRow user, long creatorId)
    {
        return user.Role == Users.AdministratorRole || user.Id == creatorId;
    }

    public static MessageRow? FindInRoom(SqliteConnection connection, long roomId, long messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Select + """
             WHERE messages.room_id = $room AND messages.id = $id
            """;
        command.Parameters.AddWithValue("$room", roomId);
        command.Parameters.AddWithValue("$id", messageId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public static IReadOnlyList<MessageRow>? IndexPage(
        SqliteConnection connection,
        long roomId,
        long? beforeId,
        long? afterId)
    {
        if (beforeId is long before)
        {
            var cursor = FindInRoom(connection, roomId, before);
            if (cursor is null)
            {
                return null;
            }

            return LastPage(connection, roomId, cursor.Value.CreatedAt);
        }

        if (afterId is long after)
        {
            var cursor = FindInRoom(connection, roomId, after);
            if (cursor is null)
            {
                return null;
            }

            return FirstPage(connection, roomId, cursor.Value.CreatedAt);
        }

        return LastPage(connection, roomId, createdBefore: null);
    }

    /// <summary>
    /// <c>RoomsController#find_messages</c>: page around the permalink target, else the last page.
    /// A missing <c>message_id</c> does not 404.
    /// </summary>
    public static IReadOnlyList<MessageRow> ShowPage(SqliteConnection connection, long roomId, long? messageId)
    {
        if (messageId is long id)
        {
            var cursor = FindInRoom(connection, roomId, id);
            if (cursor is not null)
            {
                var before = LastPage(connection, roomId, cursor.Value.CreatedAt);
                var after = FirstPage(connection, roomId, cursor.Value.CreatedAt);
                var page = new List<MessageRow>(before.Count + 1 + after.Count);
                page.AddRange(before);
                page.Add(cursor.Value);
                page.AddRange(after);
                return page;
            }
        }

        return LastPage(connection, roomId, createdBefore: null);
    }

    public static MessageRow Create(
        SqliteConnection connection,
        long roomId,
        long creatorId,
        string? body,
        string? clientMessageId,
        string? createdAt = null,
        BlobPayload? attachment = null,
        string? filesRoot = null)
    {
        var timestamp = createdAt ?? FirstRun.Timestamp();
        var clientId = clientMessageId ?? Guid.NewGuid().ToString("D");
        var stored = body ?? "";
        var plain = PlainText(stored);
        if (plain.Length == 0 && attachment is not null)
        {
            plain = string.IsNullOrEmpty(attachment.Value.Filename) ? "blob" : attachment.Value.Filename;
        }

        string? stagedKey = null;
        string? stagedPath = null;
        if (attachment is not null && !string.IsNullOrEmpty(filesRoot))
        {
            stagedKey = Attachments.NewKey();
            stagedPath = Attachments.Stage(filesRoot, stagedKey, attachment.Value.Bytes);
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            long id;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO messages (client_message_id, created_at, creator_id, room_id, updated_at)
                    VALUES ($client, $created, $creator, $room, $updated)
                    """;
                command.Parameters.AddWithValue("$client", clientId);
                command.Parameters.AddWithValue("$created", timestamp);
                command.Parameters.AddWithValue("$creator", creatorId);
                command.Parameters.AddWithValue("$room", roomId);
                command.Parameters.AddWithValue("$updated", timestamp);
                command.ExecuteNonQuery();
                id = Accounts.LastInsertRowId(connection, transaction);
            }

            InsertRichText(connection, transaction, id, stored, timestamp);
            ReplaceSearchIndex(connection, transaction, id, plain);
            if (attachment is not null && stagedKey is not null)
            {
                Attachments.Insert(
                    connection,
                    transaction,
                    Attachments.MessageType,
                    id,
                    Attachments.AttachmentName,
                    stagedKey,
                    attachment.Value,
                    timestamp);
            }

            TouchRoom(connection, transaction, roomId, timestamp);
            MembershipStore.MarkUnreadExcept(
                connection,
                roomId,
                creatorId,
                timestamp,
                transaction: transaction);
            transaction.Commit();
            return new MessageRow(id, roomId, creatorId, clientId, stored, timestamp, timestamp);
        }
        catch
        {
            Rollback(transaction);
            Attachments.DeleteFile(stagedPath);
            throw;
        }
    }

    public static MessageRow Update(
        SqliteConnection connection,
        MessageRow message,
        bool setBody,
        string? body,
        bool setClientId,
        string? clientMessageId)
    {
        var timestamp = FirstRun.Timestamp();
        var stored = setBody ? body ?? "" : message.Body;
        var clientId = setClientId ? clientMessageId ?? "" : message.ClientMessageId;
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE messages
                    SET client_message_id = $client, updated_at = $updated
                    WHERE id = $id
                    """;
                command.Parameters.AddWithValue("$client", clientId);
                command.Parameters.AddWithValue("$updated", timestamp);
                command.Parameters.AddWithValue("$id", message.Id);
                command.ExecuteNonQuery();
            }

            if (setBody)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE action_text_rich_texts
                    SET body = $body, updated_at = $updated
                    WHERE record_type = $type AND record_id = $id AND name = $name
                    """;
                command.Parameters.AddWithValue("$body", stored);
                command.Parameters.AddWithValue("$updated", timestamp);
                command.Parameters.AddWithValue("$type", RichTextType);
                command.Parameters.AddWithValue("$id", message.Id);
                command.Parameters.AddWithValue("$name", RichTextName);
                if (command.ExecuteNonQuery() == 0)
                {
                    InsertRichText(connection, transaction, message.Id, stored, timestamp);
                }

                ReplaceSearchIndex(connection, transaction, message.Id, PlainText(stored));
            }

            TouchRoom(connection, transaction, message.RoomId, timestamp);
            transaction.Commit();
            return message with { ClientMessageId = clientId, Body = stored, UpdatedAt = timestamp };
        }
        catch
        {
            Rollback(transaction);
            throw;
        }
    }

    public static void Destroy(SqliteConnection connection, MessageRow message, string? filesRoot = null)
    {
        var timestamp = FirstRun.Timestamp();
        List<string> files = [];
        using var transaction = connection.BeginTransaction();
        try
        {
            Execute(connection, transaction, "DELETE FROM boosts WHERE message_id = $id", message.Id);
            Execute(connection, transaction, "DELETE FROM message_search_index WHERE rowid = $id", message.Id);
            Execute(
                connection,
                transaction,
                """
                DELETE FROM action_text_rich_texts
                WHERE record_type = 'Message' AND record_id = $id
                """,
                message.Id);
            if (string.IsNullOrEmpty(filesRoot))
            {
                Execute(
                    connection,
                    transaction,
                    """
                    DELETE FROM active_storage_attachments
                    WHERE record_type = 'Message' AND record_id = $id
                    """,
                    message.Id);
            }
            else
            {
                files = Attachments.Detach(
                    connection,
                    transaction,
                    filesRoot,
                    Attachments.MessageType,
                    message.Id,
                    Attachments.AttachmentName);
            }

            Execute(connection, transaction, "DELETE FROM messages WHERE id = $id", message.Id);
            TouchRoom(connection, transaction, message.RoomId, timestamp);
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            throw;
        }

        foreach (var file in files)
        {
            Attachments.DeleteFile(file);
        }
    }

    public static string PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        var stripped = TagPattern().Replace(html, " ");
        return WhitespacePattern().Replace(System.Net.WebUtility.HtmlDecode(stripped), " ").Trim();
    }

    private static List<MessageRow> LastPage(SqliteConnection connection, long roomId, string? createdBefore)
    {
        var limit = PageSize.ToString(CultureInfo.InvariantCulture);
        var rows = createdBefore is null
            ? Query(connection, roomId, null, null, descending: true, limit)
            : Query(connection, roomId, createdBefore, null, descending: true, limit);
        rows.Reverse();
        return rows;
    }

    private static List<MessageRow> FirstPage(SqliteConnection connection, long roomId, string? createdAfter)
    {
        var limit = PageSize.ToString(CultureInfo.InvariantCulture);
        return Query(connection, roomId, null, createdAfter, descending: false, limit);
    }

    private static List<MessageRow> Query(
        SqliteConnection connection,
        long roomId,
        string? createdBefore,
        string? createdAfter,
        bool descending,
        string limit)
    {
        var direction = descending ? "DESC" : "ASC";
        var filter = createdBefore is not null
            ? " AND messages.created_at < $before"
            : createdAfter is not null
                ? " AND messages.created_at > $after"
                : "";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {Select}
            WHERE messages.room_id = $room{filter}
            ORDER BY messages.created_at {direction}, messages.id {direction}
            LIMIT {limit}
            """;
        command.Parameters.AddWithValue("$room", roomId);
        if (createdBefore is not null)
        {
            command.Parameters.AddWithValue("$before", createdBefore);
        }

        if (createdAfter is not null)
        {
            command.Parameters.AddWithValue("$after", createdAfter);
        }

        var rows = new List<MessageRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static void InsertRichText(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        string body,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO action_text_rich_texts (body, created_at, name, record_id, record_type, updated_at)
            VALUES ($body, $created, $name, $id, $type, $updated)
            """;
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$name", RichTextName);
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$type", RichTextType);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.ExecuteNonQuery();
    }

    private static void ReplaceSearchIndex(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        string plain)
    {
        Execute(connection, transaction, "DELETE FROM message_search_index WHERE rowid = $id", messageId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO message_search_index(rowid, body) VALUES ($id, $body)";
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$body", plain);
        command.ExecuteNonQuery();
    }

    private static void TouchRoom(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long roomId,
        string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE rooms SET updated_at = $updated WHERE id = $id";
        command.Parameters.AddWithValue("$updated", timestamp);
        command.Parameters.AddWithValue("$id", roomId);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static MessageRow ReadRow(SqliteDataReader reader)
    {
        return new MessageRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.IsDBNull(6) ? "" : reader.GetString(6),
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
        SELECT messages.id, messages.room_id, messages.creator_id, messages.client_message_id,
               messages.created_at, messages.updated_at, action_text_rich_texts.body
        FROM messages
        LEFT JOIN action_text_rich_texts
          ON action_text_rich_texts.record_type = 'Message'
         AND action_text_rich_texts.record_id = messages.id
         AND action_text_rich_texts.name = 'body'
        """;

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
