using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Identity;

public readonly record struct BlobPayload(string Filename, string ContentType, byte[] Bytes);

public readonly record struct StoredBlob(byte[] Bytes, string ContentType, string Filename);

/// <summary>
/// Active Storage rows already in the schema. Bytes live on disk under a Rails DiskService
/// layout (<c>key[0..1]/key[2..3]/key</c>). Variants are not generated; show returns the
/// stored bytes and content type.
/// </summary>
public static class Attachments
{
    public const string UserType = "User";
    public const string AccountType = "Account";
    public const string MessageType = "Message";
    public const string AvatarName = "avatar";
    public const string LogoName = "logo";
    public const string AttachmentName = "attachment";
    public const string ServiceName = "local";

    public static string FilesRoot(string databasePath)
    {
        return databasePath + ".files";
    }

    public static string NewKey()
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> raw = stackalloc char[28];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(raw);
    }

    public static string Stage(string filesRoot, string key, byte[] bytes)
    {
        var path = PathFor(filesRoot, key);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static void DeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string recordType,
        long recordId,
        string name,
        string key,
        BlobPayload payload,
        string timestamp)
    {
        using var blob = connection.CreateCommand();
        blob.Transaction = transaction;
        blob.CommandText = """
            INSERT INTO active_storage_blobs (byte_size, checksum, content_type, created_at, filename, key, service_name)
            VALUES ($size, $checksum, $type, $created, $filename, $key, $service)
            """;
        blob.Parameters.AddWithValue("$size", payload.Bytes.LongLength);
        blob.Parameters.AddWithValue("$checksum", Checksum(payload.Bytes));
        blob.Parameters.AddWithValue("$type", string.IsNullOrEmpty(payload.ContentType) ? DBNull.Value : payload.ContentType);
        blob.Parameters.AddWithValue("$created", timestamp);
        blob.Parameters.AddWithValue("$filename", string.IsNullOrEmpty(payload.Filename) ? "blob" : payload.Filename);
        blob.Parameters.AddWithValue("$key", key);
        blob.Parameters.AddWithValue("$service", ServiceName);
        blob.ExecuteNonQuery();
        var blobId = Accounts.LastInsertRowId(connection, transaction);

        using var attachment = connection.CreateCommand();
        attachment.Transaction = transaction;
        attachment.CommandText = """
            INSERT INTO active_storage_attachments (blob_id, created_at, name, record_id, record_type)
            VALUES ($blob, $created, $name, $record, $type)
            """;
        attachment.Parameters.AddWithValue("$blob", blobId);
        attachment.Parameters.AddWithValue("$created", timestamp);
        attachment.Parameters.AddWithValue("$name", name);
        attachment.Parameters.AddWithValue("$record", recordId);
        attachment.Parameters.AddWithValue("$type", recordType);
        attachment.ExecuteNonQuery();
    }

    public static void Replace(
        SqliteConnection connection,
        string filesRoot,
        string recordType,
        long recordId,
        string name,
        BlobPayload payload)
    {
        var key = NewKey();
        var staged = Stage(filesRoot, key, payload.Bytes);
        var timestamp = FirstRun.Timestamp();
        List<string> oldFiles;
        using var transaction = connection.BeginTransaction();
        try
        {
            oldFiles = PurgeRows(connection, transaction, filesRoot, recordType, recordId, name);
            Insert(connection, transaction, recordType, recordId, name, key, payload, timestamp);
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            DeleteFile(staged);
            throw;
        }

        foreach (var old in oldFiles)
        {
            DeleteFile(old);
        }
    }

    public static StoredBlob? Read(
        SqliteConnection connection,
        string filesRoot,
        string recordType,
        long recordId,
        string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT blobs.key, blobs.content_type, blobs.filename
            FROM active_storage_attachments attachments
            JOIN active_storage_blobs blobs ON blobs.id = attachments.blob_id
            WHERE attachments.record_type = $type
              AND attachments.record_id = $id
              AND attachments.name = $name
            ORDER BY attachments.id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$type", recordType);
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var key = reader.GetString(0);
        var contentType = reader.IsDBNull(1) ? "application/octet-stream" : reader.GetString(1);
        var filename = reader.IsDBNull(2) ? "blob" : reader.GetString(2);
        var path = PathFor(filesRoot, key);
        if (!File.Exists(path))
        {
            return null;
        }

        return new StoredBlob(File.ReadAllBytes(path), contentType, filename);
    }

    /// <summary>Rails <c>attached?</c>: an attachment row exists, even if the file is missing.</summary>
    public static bool Attached(
        SqliteConnection connection,
        string recordType,
        long recordId,
        string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM active_storage_attachments
            WHERE record_type = $type
              AND record_id = $id
              AND name = $name
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$type", recordType);
        command.Parameters.AddWithValue("$id", recordId);
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Removes attachment and blob rows inside an existing transaction. Caller deletes the files after commit.
    /// </summary>
    public static List<string> Detach(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string filesRoot,
        string recordType,
        long recordId,
        string name)
    {
        return PurgeRows(connection, transaction, filesRoot, recordType, recordId, name);
    }

    public static Dictionary<long, string> Filenames(
        SqliteConnection connection,
        string recordType,
        string name,
        IReadOnlyCollection<long> recordIds)
    {
        var filenames = new Dictionary<long, string>();
        if (recordIds.Count == 0)
        {
            return filenames;
        }

        using var command = connection.CreateCommand();
        var placeholders = new string[recordIds.Count];
        var index = 0;
        foreach (var id in recordIds)
        {
            var parameter = "$id" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            placeholders[index] = parameter;
            command.Parameters.AddWithValue(parameter, id);
            index++;
        }

        command.CommandText = $"""
            SELECT attachments.record_id, blobs.filename
            FROM active_storage_attachments attachments
            JOIN active_storage_blobs blobs ON blobs.id = attachments.blob_id
            WHERE attachments.record_type = $type
              AND attachments.name = $name
              AND attachments.record_id IN ({string.Join(", ", placeholders)})
            ORDER BY attachments.id
            """;
        command.Parameters.AddWithValue("$type", recordType);
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            filenames[reader.GetInt64(0)] = reader.IsDBNull(1) ? "blob" : reader.GetString(1);
        }

        return filenames;
    }

    public static void Destroy(
        SqliteConnection connection,
        string filesRoot,
        string recordType,
        long recordId,
        string name)
    {
        using var transaction = connection.BeginTransaction();
        List<string> files;
        try
        {
            files = PurgeRows(connection, transaction, filesRoot, recordType, recordId, name);
            transaction.Commit();
        }
        catch
        {
            Rollback(transaction);
            throw;
        }

        foreach (var file in files)
        {
            DeleteFile(file);
        }
    }

    public static string PathFor(string filesRoot, string key)
    {
        var first = key.Length >= 2 ? key[..2] : key;
        var second = key.Length >= 4 ? key[2..4] : first;
        return Path.Combine(filesRoot, first, second, key);
    }

    private static List<string> PurgeRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string filesRoot,
        string recordType,
        long recordId,
        string name)
    {
        var blobs = new List<(long Id, string Key)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT blobs.id, blobs.key
                FROM active_storage_attachments attachments
                JOIN active_storage_blobs blobs ON blobs.id = attachments.blob_id
                WHERE attachments.record_type = $type
                  AND attachments.record_id = $id
                  AND attachments.name = $name
                """;
            select.Parameters.AddWithValue("$type", recordType);
            select.Parameters.AddWithValue("$id", recordId);
            select.Parameters.AddWithValue("$name", name);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                blobs.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        if (blobs.Count == 0)
        {
            return [];
        }

        using (var deleteAttachments = connection.CreateCommand())
        {
            deleteAttachments.Transaction = transaction;
            deleteAttachments.CommandText = """
                DELETE FROM active_storage_attachments
                WHERE record_type = $type AND record_id = $id AND name = $name
                """;
            deleteAttachments.Parameters.AddWithValue("$type", recordType);
            deleteAttachments.Parameters.AddWithValue("$id", recordId);
            deleteAttachments.Parameters.AddWithValue("$name", name);
            deleteAttachments.ExecuteNonQuery();
        }

        foreach (var blob in blobs)
        {
            using var variants = connection.CreateCommand();
            variants.Transaction = transaction;
            variants.CommandText = """
                DELETE FROM active_storage_variant_records
                WHERE blob_id = $blob
                  AND NOT EXISTS (SELECT 1 FROM active_storage_attachments WHERE blob_id = $blob)
                """;
            variants.Parameters.AddWithValue("$blob", blob.Id);
            variants.ExecuteNonQuery();

            using var deleteBlob = connection.CreateCommand();
            deleteBlob.Transaction = transaction;
            deleteBlob.CommandText = """
                DELETE FROM active_storage_blobs
                WHERE id = $blob
                  AND NOT EXISTS (SELECT 1 FROM active_storage_attachments WHERE blob_id = $blob)
                """;
            deleteBlob.Parameters.AddWithValue("$blob", blob.Id);
            deleteBlob.ExecuteNonQuery();
        }

        return blobs.Select(blob => PathFor(filesRoot, blob.Key)).ToList();
    }

    private static string Checksum(byte[] bytes)
    {
        return Convert.ToBase64String(MD5.HashData(bytes));
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
