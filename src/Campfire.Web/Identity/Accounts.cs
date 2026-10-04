using System.Security.Cryptography;
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

    /// <summary>
    /// Inserts one account. <paramref name="name"/> is the account name (first run always uses <see cref="FirstRunName"/>).
    /// Join code is generated only because <c>join_code</c> is NOT NULL (<c>Account::Joinable</c>); join-code routes are not implemented.
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
