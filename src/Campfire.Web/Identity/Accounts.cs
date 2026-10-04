using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Identity;

/// <summary>
/// Singleton account. <c>singleton_guard</c> is always 0, so the unique index rejects a second row
/// the same way Rails <c>Account.create!</c> raises <c>ActiveRecord::RecordNotUnique</c>.
/// </summary>
public static class Accounts
{
    public const string FirstRunName = "Campfire";

    public static bool Any(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM accounts LIMIT 1";
        return command.ExecuteScalar() is not null;
    }

    public readonly record struct AccountRow(long Id, string Name, string JoinCode, string? CustomStyles, string? Settings);

    public static AccountRow? First(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, join_code, custom_styles, settings
            FROM accounts
            ORDER BY id
            LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new AccountRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public static string ResetJoinCode(SqliteConnection connection, long accountId)
    {
        var current = First(connection)?.JoinCode;
        string code;
        do
        {
            code = GenerateJoinCode();
        }
        while (code == current);

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE accounts
            SET join_code = $code, updated_at = $updated
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$updated", FirstRun.Timestamp());
        command.Parameters.AddWithValue("$id", accountId);
        command.ExecuteNonQuery();
        return code;
    }

    public static void UpdateName(SqliteConnection connection, long accountId, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET name = $name, updated_at = $updated WHERE id = $id";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$updated", FirstRun.Timestamp());
        command.Parameters.AddWithValue("$id", accountId);
        command.ExecuteNonQuery();
    }

    public static void UpdateCustomStyles(SqliteConnection connection, long accountId, string? customStyles)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE accounts
            SET custom_styles = $styles, updated_at = $updated
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$styles", (object?)customStyles ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", FirstRun.Timestamp());
        command.Parameters.AddWithValue("$id", accountId);
        command.ExecuteNonQuery();
    }

    public static void Touch(SqliteConnection connection, long accountId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET updated_at = $updated WHERE id = $id";
        command.Parameters.AddWithValue("$updated", FirstRun.Timestamp());
        command.Parameters.AddWithValue("$id", accountId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts one account. <paramref name="name"/> is the account name (first run always uses <see cref="FirstRunName"/>).
    /// Join code matches <c>Account::Joinable</c> (<c>xxxx-xxxx-xxxx</c>).
    /// </summary>
    public static long Insert(SqliteConnection connection, SqliteTransaction? transaction, string name, string timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO accounts (created_at, join_code, name, singleton_guard, updated_at)
            VALUES ($created, $join, $name, 0, $updated)
            """;
        command.Parameters.AddWithValue("$created", timestamp);
        command.Parameters.AddWithValue("$join", GenerateJoinCode());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$updated", timestamp);
        command.ExecuteNonQuery();
        return LastInsertRowId(connection, transaction);
    }

    /// <summary>
    /// <c>Account.settings.restrict_room_creation_to_administrators?</c>.
    /// Missing, false, and the string <c>"false"</c> are unrestricted. Boolean true and <c>"true"</c> restrict.
    /// </summary>
    public static bool RestrictsRoomCreation(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(settings);
            if (!document.RootElement.TryGetProperty("restrict_room_creation_to_administrators", out var value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsSingletonViolation(SqliteException exception)
    {
        return exception.SqliteErrorCode == 19
            && exception.Message.Contains("singleton_guard", StringComparison.OrdinalIgnoreCase);
    }

    public static long LastInsertRowId(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid()";
        return (long)command.ExecuteScalar()!;
    }

    private static string GenerateJoinCode()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<char> raw = stackalloc char[12];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        var body = new string(raw);
        return $"{body[..4]}-{body[4..8]}-{body[8..12]}";
    }
}
