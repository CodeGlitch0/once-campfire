using System.Net;
using Campfire.Web.Data;
using Campfire.Web.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests;

public sealed class FirstRunTests
{
    [Fact]
    public async Task Create_CreatesAdministratorAndSingletonAccount()
    {
        await using var host = new FirstRunHost();
        using var client = host.CreateClient();

        using var open = await client.GetAsync("/first_run");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Contains("user[name]", await open.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var response = await client.PostAsync("/first_run", Form(
            ("user[name]", "New Person"),
            ("user[email_address]", "new@37signals.com"),
            ("user[password]", "secret123456"),
            ("account[name]", "37signals")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/", response.Headers.Location.AbsolutePath);
        Assert.Equal(client.BaseAddress!.Host, response.Headers.Location.Host);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookie));
        var cookie = string.Join("\n", setCookie!);
        Assert.Contains("session_token=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        using var connection = host.Open();
        Assert.Equal(1L, Count(connection, "accounts"));
        Assert.Equal("Campfire", Scalar(connection, "SELECT name FROM accounts"));
        Assert.Equal(0L, Scalar(connection, "SELECT singleton_guard FROM accounts"));
        Assert.Matches("^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$", (string)Scalar(connection, "SELECT join_code FROM accounts")!);

        Assert.Equal(1L, Count(connection, "users"));
        Assert.Equal("New Person", Scalar(connection, "SELECT name FROM users"));
        Assert.Equal("new@37signals.com", Scalar(connection, "SELECT email_address FROM users"));
        Assert.Equal(1L, Scalar(connection, "SELECT role FROM users"));
        Assert.Equal(0L, Scalar(connection, "SELECT status FROM users"));

        var digest = (string)Scalar(connection, "SELECT password_digest FROM users")!;
        Assert.StartsWith("$2a$12$", digest, StringComparison.Ordinal);
        Assert.Equal(60, digest.Length);
        Assert.True(PasswordDigest.Verify("secret123456", digest));

        Assert.Equal(1L, Count(connection, "rooms"));
        Assert.Equal("All Talk", Scalar(connection, "SELECT name FROM rooms"));
        Assert.Equal("Rooms::Open", Scalar(connection, "SELECT type FROM rooms"));
        Assert.Equal(1L, Count(connection, "memberships"));
        Assert.Equal("mentions", Scalar(connection, "SELECT involvement FROM memberships"));

        Assert.Equal(1L, Count(connection, "sessions"));
        var token = (string)Scalar(connection, "SELECT token FROM sessions")!;
        Assert.Equal(24, token.Length);
        Assert.Contains($"session_token={token}", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondRun_IsClosed()
    {
        await using var host = new FirstRunHost();
        using var client = host.CreateClient();
        using var created = await client.PostAsync("/first_run", Form(
            ("user[name]", "Admin"),
            ("user[email_address]", "admin@example.com"),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        using var show = await client.GetAsync("/first_run");
        Assert.Equal(HttpStatusCode.Redirect, show.StatusCode);
        Assert.Equal("/", show.Headers.Location?.AbsolutePath);

        using var again = await client.PostAsync("/first_run", Form(
            ("user[name]", "Second"),
            ("user[email_address]", "second@example.com"),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Equal("/", again.Headers.Location?.AbsolutePath);

        using var connection = host.Open();
        Assert.Equal(1L, Count(connection, "accounts"));
        Assert.Equal(1L, Count(connection, "users"));
        Assert.Equal("Admin", Scalar(connection, "SELECT name FROM users"));
    }

    [Fact]
    public void TryCreate_WhenAccountAlreadyExists_ReturnsClosedWithoutAnotherUser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"campfire-try-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            SqliteSchema.Apply(connection);

            var first = FirstRun.TryCreate(connection, new FirstRunRequest(
                "Admin", "admin@example.com", "secret123456", null, null));
            Assert.True(first.Created);
            Assert.False(string.IsNullOrEmpty(first.SessionToken));

            var second = FirstRun.TryCreate(connection, new FirstRunRequest(
                "Second", "second@example.com", "secret123456", null, null));
            Assert.False(second.Created);
            Assert.Null(second.SessionToken);
            Assert.Equal(1L, Count(connection, "accounts"));
            Assert.Equal(1L, Count(connection, "users"));
            Assert.Equal(1L, Count(connection, "sessions"));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void SecondAccount_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"campfire-account-{Guid.NewGuid():N}.sqlite");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            SqliteSchema.Apply(connection);

            using (var first = connection.BeginTransaction())
            {
                Accounts.Insert(connection, first, "Campfire", FirstRun.Timestamp());
                first.Commit();
            }

            using var second = connection.BeginTransaction();
            var exception = Assert.Throws<SqliteException>(() =>
                Accounts.Insert(connection, second, "Other", FirstRun.Timestamp()));
            Assert.True(Accounts.IsSingletonViolation(exception));
            second.Rollback();

            Assert.Equal(1L, Count(connection, "accounts"));
            Assert.Equal(0L, Scalar(connection, "SELECT singleton_guard FROM accounts"));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task UnimplementedFirstRunActions_ReturnNotFound()
    {
        await using var host = new FirstRunHost();
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/first_run/new")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/first_run/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsync("/first_run", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/first_run", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/first_run")).StatusCode);

        using var created = await client.PostAsync("/first_run", Form(
            ("user[name]", "Admin"),
            ("user[email_address]", "admin@example.com"),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/first_run/new")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/first_run/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/first_run")).StatusCode);
    }

    [Fact]
    public async Task Create_WithoutUserParams_ReturnsBadRequest()
    {
        await using var host = new FirstRunHost();
        using var client = host.CreateClient();
        using var response = await client.PostAsync("/first_run", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["account[name]"] = "37signals",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var connection = host.Open();
        Assert.Equal(0L, Count(connection, "accounts"));
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
    }

    private static long Count(SqliteConnection connection, string table)
    {
        return (long)Scalar(connection, $"SELECT COUNT(*) FROM \"{table}\"")!;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FirstRunHost : IAsyncDisposable
    {
        private readonly string _path;
        private readonly WebApplicationFactory<Program> _factory;

        public FirstRunHost()
        {
            _path = Path.Combine(Path.GetTempPath(), $"campfire-first-run-{Guid.NewGuid():N}.sqlite");
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Campfire:DatabasePath", _path);
            });
        }

        public HttpClient CreateClient()
        {
            return _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={_path}");
            connection.Open();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            TryDelete(_path);
            TryDelete(_path + "-wal");
            TryDelete(_path + "-shm");
        }
    }
}
