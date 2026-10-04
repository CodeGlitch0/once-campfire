using System.Globalization;
using System.Net;
using Campfire.Web.Http;
using Campfire.Web.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task ValidLogin_CreatesSession_AndSubsequentRequestIsAuthenticated()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();
        await CreateAdmin(client, "new@37signals.com");

        using var login = new HttpRequestMessage(HttpMethod.Post, "/session")
        {
            Content = Form(
                ("email_address", "new@37signals.com"),
                ("password", "secret123456")),
        };
        login.Headers.TryAddWithoutValidation("User-Agent", "LoginAgent/1.0");
        login.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");

        using var response = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.AbsolutePath);

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookie));
        var cookieHeader = string.Join("\n", setCookie!);
        Assert.Contains("httponly", cookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookieHeader, StringComparison.OrdinalIgnoreCase);
        var signed = SessionCookie.ValueFromSetCookieHeader(cookieHeader);
        Assert.False(string.IsNullOrEmpty(signed));
        var token = RailsMessages.ReadSessionToken(signed, SecretKey.Default);
        Assert.False(string.IsNullOrEmpty(token));

        using (var connection = host.Open())
        {
            Assert.Equal(2L, Count(connection, "sessions"));
            Assert.Equal("203.0.113.10", Scalar(connection, "SELECT ip_address FROM sessions WHERE token = $token", ("$token", token!)));
            Assert.Equal("LoginAgent/1.0", Scalar(connection, "SELECT user_agent FROM sessions WHERE token = $token", ("$token", token!)));
            Assert.Equal("new@37signals.com", Scalar(connection, """
                SELECT users.email_address
                FROM sessions
                JOIN users ON users.id = sessions.user_id
                WHERE sessions.token = $token
                """, ("$token", token!)));

            var stale = DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            Execute(connection, "UPDATE sessions SET last_active_at = $active WHERE token = $token",
                ("$active", stale),
                ("$token", token!));
        }

        using var follow = new HttpRequestMessage(HttpMethod.Get, "/session/new");
        follow.Headers.TryAddWithoutValidation("Cookie", $"session_token={signed}");
        follow.Headers.TryAddWithoutValidation("User-Agent", "SubsequentAgent/1.0");
        follow.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.11");
        using var authed = await client.SendAsync(follow);
        Assert.Equal(HttpStatusCode.OK, authed.StatusCode);

        using (var connection = host.Open())
        {
            Assert.Equal("SubsequentAgent/1.0", Scalar(connection, "SELECT user_agent FROM sessions WHERE token = $token", ("$token", token!)));
            Assert.Equal("203.0.113.11", Scalar(connection, "SELECT ip_address FROM sessions WHERE token = $token", ("$token", token!)));
            var active = (string)Scalar(connection, "SELECT last_active_at FROM sessions WHERE token = $token", ("$token", token!))!;
            Assert.True(DateTime.Parse(active, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) > DateTime.UtcNow.AddMinutes(-5));
        }
    }

    [Fact]
    public async Task BadPassword_IsRejected()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();
        await CreateAdmin(client, "new@37signals.com");

        using var response = await client.PostAsync("/session", Form(
            ("email_address", "new@37signals.com"),
            ("password", "wrong")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Too many requests or unauthorized.", body, StringComparison.Ordinal);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var setCookie) && string.Join("\n", setCookie!).Contains("session_token=", StringComparison.Ordinal));

        using var connection = host.Open();
        Assert.Equal(1L, Count(connection, "sessions"));
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();
        await CreateAdmin(client, "admin@example.com");

        using var anonymous = await client.DeleteAsync("/session");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/session/new", anonymous.Headers.Location?.AbsolutePath);

        var signed = await Login(client, "admin@example.com", "secret123456");
        string token;
        using (var connection = host.Open())
        {
            token = RailsMessages.ReadSessionToken(signed, SecretKey.Default)!;
            Assert.NotNull(Scalar(connection, "SELECT id FROM sessions WHERE token = $token", ("$token", token)));
        }

        using var logout = new HttpRequestMessage(HttpMethod.Delete, "/session");
        logout.Headers.TryAddWithoutValidation("Cookie", $"session_token={signed}");
        using var response = await client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.AbsolutePath);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cleared));
        Assert.Contains("session_token=;", string.Join("\n", cleared!), StringComparison.Ordinal);

        using (var connection = host.Open())
        {
            Assert.Null(Scalar(connection, "SELECT id FROM sessions WHERE token = $token", ("$token", token)));
        }

        using var again = new HttpRequestMessage(HttpMethod.Delete, "/session");
        again.Headers.TryAddWithoutValidation("Cookie", $"session_token={signed}");
        using var rejected = await client.SendAsync(again);
        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        Assert.Equal("/session/new", rejected.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task TransferShow_RendersWhenNotSignedIn()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/session/transfers/some-token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TransferUpdate_EstablishesSessionWhenCodeIsValid()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();
        await CreateAdmin(client, "david@37signals.com");

        long userId;
        using (var connection = host.Open())
        {
            userId = (long)Scalar(connection, "SELECT id FROM users")!;
        }

        var transferId = RailsMessages.SignTransferId(userId, SecretKey.Default, DateTimeOffset.UtcNow.AddHours(4));
        using var response = await client.PutAsync($"/session/transfers/{Uri.EscapeDataString(transferId)}", new StringContent(""));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.AbsolutePath);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookie));
        var signed = SessionCookie.ValueFromSetCookieHeader(string.Join("\n", setCookie!));
        var token = RailsMessages.ReadSessionToken(signed, SecretKey.Default);
        Assert.False(string.IsNullOrEmpty(token));

        using var connection2 = host.Open();
        Assert.Equal(userId, Scalar(connection2, "SELECT user_id FROM sessions WHERE token = $token", ("$token", token!)));

        using var invalid = await client.PutAsync("/session/transfers/not-a-code", new StringContent(""));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task BannedIp_IsBlocked()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();
        await CreateAdmin(client, "admin@example.com");

        using (var connection = host.Open())
        {
            var userId = (long)Scalar(connection, "SELECT id FROM users")!;
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            Execute(connection, """
                INSERT INTO bans (created_at, ip_address, updated_at, user_id)
                VALUES ($created, $ip, $updated, $user)
                """,
                ("$created", now),
                ("$ip", "203.0.113.1"),
                ("$updated", now),
                ("$user", userId));
        }

        using var blocked = new HttpRequestMessage(HttpMethod.Post, "/session")
        {
            Content = Form(("email_address", "admin@example.com"), ("password", "secret123456")),
        };
        blocked.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.1");
        using var banned = await client.SendAsync(blocked);
        Assert.Equal(HttpStatusCode.TooManyRequests, banned.StatusCode);

        using var allowedGet = new HttpRequestMessage(HttpMethod.Get, "/session/new");
        allowedGet.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.1");
        using var get = await client.SendAsync(allowedGet);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var other = new HttpRequestMessage(HttpMethod.Post, "/session")
        {
            Content = Form(("email_address", "admin@example.com"), ("password", "wrong")),
        };
        other.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
        using var open = await client.SendAsync(other);
        Assert.Equal(HttpStatusCode.Unauthorized, open.StatusCode);
    }

    [Fact]
    public async Task SignInPage_IncludesAdminEmailAfterFirstRun()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();

        using var before = await client.GetAsync("/session/new");
        Assert.Equal(HttpStatusCode.Redirect, before.StatusCode);
        Assert.Equal("/first_run", before.Headers.Location?.AbsolutePath);

        await CreateAdmin(client, "owner@37signals.com");
        using var response = await client.GetAsync("/session/new");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", response.Headers.GetValues("X-Version").Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("owner@37signals.com", body, StringComparison.Ordinal);
        Assert.Contains("Campfire", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossSitePost_IsRejectedWith422()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/session")
        {
            Content = Form(("email_address", "a@b.c"), ("password", "secret123456")),
        };
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var https = new DefaultHttpContext();
        https.Request.Scheme = "https";
        https.Request.Method = HttpMethods.Post;
        https.Request.Host = new HostString("example.test");
        Assert.False(RequestGuards.ForgeryAllows(https.Request));
    }

    [Fact]
    public void SignedSessionCookie_MatchesRailsMessageShape()
    {
        var expires = DateTimeOffset.Parse("2030-01-02T03:04:05.678Z", CultureInfo.InvariantCulture);
        var signed = RailsMessages.SignSessionToken("AbCdEfGhIjKlMnOpQrStUvWx", "test-secret", expires);
        Assert.Equal("AbCdEfGhIjKlMnOpQrStUvWx", RailsMessages.ReadSessionToken(signed, "test-secret"));
        Assert.Equal(40, signed[(signed.Length - 40)..].Length);
        Assert.Equal('-', signed[^42]);
        var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(signed[..^42]));
        Assert.Contains("\"pur\":\"cookie.session_token\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"exp\":\"2030-01-02T03:04:05.678Z\"", payload, StringComparison.Ordinal);

        var transfer = RailsMessages.SignTransferId(42, "test-secret", expires);
        Assert.Equal(42, RailsMessages.ReadTransferId(transfer, "test-secret"));
        Assert.Equal(64, transfer[(transfer.Length - 64)..].Length);
    }

    [Fact]
    public async Task UnimplementedSessionActions_ReturnNotFound()
    {
        await using var host = new SessionHost();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/session")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/session/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsync("/session", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/session", new StringContent(""))).StatusCode);
    }

    private static async Task CreateAdmin(HttpClient client, string email)
    {
        using var response = await client.PostAsync("/first_run", Form(
            ("user[name]", "Admin"),
            ("user[email_address]", email),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> Login(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsync("/session", Form(
            ("email_address", email),
            ("password", password)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var setCookie));
        var signed = SessionCookie.ValueFromSetCookieHeader(string.Join("\n", setCookie!));
        Assert.False(string.IsNullOrEmpty(signed));
        return signed!;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
    }

    private static long Count(SqliteConnection connection, string table)
    {
        return (long)Scalar(connection, $"SELECT COUNT(*) FROM \"{table}\"")!;
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private sealed class SessionHost : IAsyncDisposable
    {
        private readonly string _path;
        private readonly WebApplicationFactory<Program> _factory;

        public SessionHost()
        {
            _path = Path.Combine(Path.GetTempPath(), $"campfire-session-{Guid.NewGuid():N}.sqlite");
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
                HandleCookies = false,
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
    }
}
