using Microsoft.Data.Sqlite;

namespace Campfire.Web.Data;

/// <summary>
/// Opens the Campfire SQLite database and applies <see cref="SqliteSchema"/> once.
/// </summary>
public sealed class CampfireDatabase
{
    private readonly string _path;
    private readonly object _schemaGate = new();
    private bool _schemaReady;

    public CampfireDatabase(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Campfire:DatabasePath"];
        _path = string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(environment.ContentRootPath, "storage", "db", "development.sqlite3")
            : configured;
    }

    public string Path => _path;

    public SqliteConnection Open()
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }

        using (var busy = connection.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout = 5000;";
            busy.ExecuteNonQuery();
        }

        EnsureSchema(connection);
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        lock (_schemaGate)
        {
            if (_schemaReady)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'accounts'";
            if (command.ExecuteScalar() is null)
            {
                SqliteSchema.Apply(connection);
            }

            _schemaReady = true;
        }
    }
}
