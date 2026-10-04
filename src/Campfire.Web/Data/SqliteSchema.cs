using System.Text;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Data;

/// <summary>
/// Applies the Campfire SQLite schema (Rails db/schema.rb version 2025_12_12_154340).
/// </summary>
public static class SqliteSchema
{
    public const string ResourceName = "Campfire.Web.Data.schema.sql";

    /// <summary>ActiveRecord schema version from db/schema.rb.</summary>
    public const string Version = "2025_12_12_154340";

    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException("SQLite connection must be open before applying the schema.");
        }

        var statements = SplitStatements(LoadDdl());
        foreach (var statement in statements)
        {
            if (IsPragma(statement))
            {
                Execute(connection, statement, transaction: null);
            }
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var statement in statements)
            {
                if (IsPragma(statement))
                {
                    continue;
                }

                Execute(connection, statement, transaction);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string statement, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = statement;
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException($"Failed to apply schema statement: {statement}", ex);
        }
    }

    private static bool IsPragma(string statement)
    {
        return statement.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase);
    }

    private static string LoadDdl()
    {
        var assembly = typeof(SqliteSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded schema SQL '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static IReadOnlyList<string> SplitStatements(string script)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var inLineComment = false;

        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (!inSingle && !inDouble && c == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (!inDouble && c == '\'')
            {
                if (inSingle && next == '\'')
                {
                    current.Append(c);
                    current.Append(next);
                    i++;
                    continue;
                }

                inSingle = !inSingle;
                current.Append(c);
                continue;
            }

            if (!inSingle && c == '"')
            {
                inDouble = !inDouble;
                current.Append(c);
                continue;
            }

            if (!inSingle && !inDouble && c == ';')
            {
                AddStatement(statements, current);
                continue;
            }

            current.Append(c);
        }

        AddStatement(statements, current);
        return statements;
    }

    private static void AddStatement(List<string> statements, StringBuilder current)
    {
        var statement = current.ToString().Trim();
        current.Clear();
        if (statement.Length > 0)
        {
            statements.Add(statement);
        }
    }
}
