using Campfire.Web.Data;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests;

public sealed class SqliteSchemaTests
{
    private static readonly string[] ExpectedTables =
    [
        "accounts",
        "action_text_rich_texts",
        "active_storage_attachments",
        "active_storage_blobs",
        "active_storage_variant_records",
        "bans",
        "boosts",
        "memberships",
        "messages",
        "push_subscriptions",
        "rooms",
        "searches",
        "sessions",
        "users",
        "webhooks",
    ];

    /// <summary>Unique indexes named in db/schema.rb version 2025_12_12_154340.</summary>
    private static readonly string[] ExpectedUniqueIndexes =
    [
        "index_accounts_on_singleton_guard",
        "index_action_text_rich_texts_uniqueness",
        "index_active_storage_attachments_uniqueness",
        "index_active_storage_blobs_on_key",
        "index_active_storage_variant_records_uniqueness",
        "index_memberships_on_room_id_and_user_id",
        "index_sessions_on_token",
        "index_users_on_bot_token",
        "index_users_on_email_address",
    ];

    private static readonly string[] ExpectedNamedIndexes =
    [
        ..ExpectedUniqueIndexes,
        "index_active_storage_attachments_on_blob_id",
        "index_bans_on_ip_address",
        "index_bans_on_user_id",
        "index_boosts_on_booster_id",
        "index_boosts_on_message_id",
        "index_memberships_on_room_id_and_created_at",
        "index_memberships_on_room_id",
        "index_memberships_on_user_id",
        "index_messages_on_creator_id",
        "index_messages_on_room_id",
        "idx_on_endpoint_p256dh_key_auth_key_7553014576",
        "index_push_subscriptions_on_user_id",
        "index_searches_on_user_id",
        "index_sessions_on_user_id",
        "index_webhooks_on_user_id",
    ];

    private static readonly (string Table, string Column, string References)[] ExpectedForeignKeys =
    [
        ("active_storage_attachments", "blob_id", "active_storage_blobs"),
        ("active_storage_variant_records", "blob_id", "active_storage_blobs"),
        ("bans", "user_id", "users"),
        ("boosts", "message_id", "messages"),
        ("messages", "room_id", "rooms"),
        ("messages", "creator_id", "users"),
        ("push_subscriptions", "user_id", "users"),
        ("searches", "user_id", "users"),
        ("sessions", "user_id", "users"),
        ("webhooks", "user_id", "users"),
    ];

    [Fact]
    public void Apply_OnFreshDatabase_CreatesSchemaTablesUniqueIndexesAndMessageSearchIndex()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        SqliteSchema.Apply(connection);

        Assert.Equal("2025_12_12_154340", SqliteSchema.Version);
        Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));

        var tables = Query(connection, """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """);

        foreach (var expected in ExpectedTables)
        {
            Assert.Contains(expected, tables);
        }

        var extras = tables
            .Where(name => name != "message_search_index")
            .Where(name => !ExpectedTables.Contains(name))
            .Where(name => !name.StartsWith("message_search_index_", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(extras);

        var virtualSql = (string)Scalar(connection, """
            SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'message_search_index'
            """)!;
        Assert.Contains("VIRTUAL", virtualSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fts5", virtualSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tokenize=porter", virtualSql, StringComparison.OrdinalIgnoreCase);

        var uniqueIndexes = Query(connection, """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index' AND sql LIKE 'CREATE UNIQUE INDEX%'
            ORDER BY name
            """);
        Assert.Equal(ExpectedUniqueIndexes.OrderBy(name => name, StringComparer.Ordinal), uniqueIndexes);

        var namedIndexes = Query(connection, """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index' AND sql IS NOT NULL
            ORDER BY name
            """);
        Assert.Equal(ExpectedNamedIndexes.OrderBy(name => name, StringComparer.Ordinal), namedIndexes);

        var foreignKeys = new List<string>();
        foreach (var table in ExpectedTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"from\", \"table\" FROM pragma_foreign_key_list('{table}')";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                foreignKeys.Add($"{table}.{reader.GetString(0)}->{reader.GetString(1)}");
            }
        }

        var expectedKeys = ExpectedForeignKeys
            .Select(fk => $"{fk.Table}.{fk.Column}->{fk.References}")
            .OrderBy(key => key, StringComparer.Ordinal);
        Assert.Equal(expectedKeys, foreignKeys.OrderBy(key => key, StringComparer.Ordinal));

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO message_search_index(rowid, body) VALUES (1, 'hello campfire')";
            insert.ExecuteNonQuery();
        }

        Assert.Equal(1L, Scalar(connection, "SELECT rowid FROM message_search_index WHERE message_search_index MATCH 'campfire'"));
    }

    private static List<string> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
