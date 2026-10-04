using System.Net;
using Campfire.Web.Identity;
using Campfire.Web.Rooms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests;

public sealed class RoomTests
{
    [Fact]
    public async Task Create_an_open_room_and_see_it()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var member = await Join(client, await JoinCode(host), "Member", "member@example.com");

        using (var form = await Send(client, HttpMethod.Get, "/rooms/opens/new", admin))
        {
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            var html = await form.Content.ReadAsStringAsync();
            Assert.Contains("action=\"/rooms/opens\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"room[name]\"", html, StringComparison.Ordinal);
            Assert.Contains("type=\"submit\"", html, StringComparison.Ordinal);
        }

        using var created = await Send(client, HttpMethod.Post, "/rooms/opens", admin, Form(("room[name]", "Design")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var path = created.Headers.Location?.AbsolutePath;
        Assert.StartsWith("/rooms/", path, StringComparison.Ordinal);
        Assert.False(path!.Contains("/opens", StringComparison.Ordinal));

        using var shown = await Send(client, HttpMethod.Get, path, admin);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        var page = await shown.Content.ReadAsStringAsync();
        Assert.Contains("Design", page, StringComparison.Ordinal);
        Assert.Contains("Rooms::Open", page, StringComparison.Ordinal);
        Assert.True(shown.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains("last_room=", string.Join("\n", cookies!), StringComparison.Ordinal);

        using var memberView = await Send(client, HttpMethod.Get, path, member);
        Assert.Equal(HttpStatusCode.OK, memberView.StatusCode);
        Assert.Contains("Design", await memberView.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var index = await Send(client, HttpMethod.Get, "/rooms", admin);
        Assert.Equal(HttpStatusCode.Redirect, index.StatusCode);
        Assert.Equal(path, index.Headers.Location?.AbsolutePath);

        using (var shadowed = await Send(client, HttpMethod.Get, "/rooms/opens", admin))
        {
            Assert.Equal(HttpStatusCode.Redirect, shadowed.StatusCode);
            Assert.Equal("/", shadowed.Headers.Location?.AbsolutePath);
        }

        using (var atMessage = await Send(client, HttpMethod.Get, path + "/@1", admin))
        {
            Assert.Equal(HttpStatusCode.OK, atMessage.StatusCode);
            Assert.Contains("Design", await atMessage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var connection = host.Open();
        Assert.Equal("Design", Scalar(connection, "SELECT name FROM rooms WHERE type = 'Rooms::Open' ORDER BY id DESC LIMIT 1"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM memberships WHERE room_id = (SELECT id FROM rooms WHERE name = 'Design')"));
    }

    [Fact]
    public async Task Closed_room_access_matches_Rails()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var member = await Join(client, await JoinCode(host), "Member", "member@example.com");
        var other = await Join(client, await JoinCode(host), "Other", "other@example.com");
        long adminId;
        long memberId;
        using (var connection = host.Open())
        {
            adminId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'new@37signals.com'")!;
            memberId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'member@example.com'")!;
        }

        using var created = await Send(
            client,
            HttpMethod.Post,
            "/rooms/closeds",
            admin,
            Form(("room[name]", "Designers"), ("user_ids[]", adminId.ToString()), ("user_ids[]", memberId.ToString())));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var path = created.Headers.Location!.AbsolutePath;

        using (var memberView = await Send(client, HttpMethod.Get, path, member))
        {
            Assert.Equal(HttpStatusCode.OK, memberView.StatusCode);
            Assert.Contains("Designers", await memberView.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var outsider = await Send(client, HttpMethod.Get, path, other))
        {
            Assert.Equal(HttpStatusCode.Redirect, outsider.StatusCode);
            Assert.Equal("/", outsider.Headers.Location?.AbsolutePath);
        }

        using (var forbidden = await Send(
            client,
            HttpMethod.Put,
            "/rooms/closeds/" + RoomId(path),
            member,
            Form(("room[name]", "Stolen"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal("Designers", Scalar(connection, "SELECT name FROM rooms WHERE id = " + RoomId(path)));
        }

        using var updated = await Send(
            client,
            HttpMethod.Put,
            "/rooms/closeds/" + RoomId(path),
            admin,
            Form(("room[name]", "Studio"), ("user_ids[]", adminId.ToString())));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal(path, updated.Headers.Location?.AbsolutePath);

        using (var connection = host.Open())
        {
            Assert.Equal("Studio", Scalar(connection, "SELECT name FROM rooms WHERE id = " + RoomId(path)));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM memberships WHERE room_id = " + RoomId(path)));
        }

        using (var removed = await Send(client, HttpMethod.Get, path, member))
        {
            Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
            Assert.Equal("/", removed.Headers.Location?.AbsolutePath);
        }

        using (var edit = await Send(client, HttpMethod.Get, "/rooms/closeds/" + RoomId(path) + "/edit", admin))
        {
            var html = await edit.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            Assert.Contains("name=\"_method\" value=\"patch\"", html, StringComparison.Ordinal);
            Assert.Contains("type=\"submit\"", html, StringComparison.Ordinal);
        }

        using (var connection = host.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE accounts SET settings = $settings";
            command.Parameters.AddWithValue("$settings", "{\"restrict_room_creation_to_administrators\":true}");
            command.ExecuteNonQuery();
        }

        using (var blocked = await Send(client, HttpMethod.Post, "/rooms/closeds", member, Form(("room[name]", "Nope"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        }

        using (var blockedOpen = await Send(client, HttpMethod.Post, "/rooms/opens", member, Form(("room[name]", "Nope"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, blockedOpen.StatusCode);
        }

        using var allowed = await Send(client, HttpMethod.Post, "/rooms/closeds", admin, Form(("room[name]", "Admin only"), ("user_ids[]", adminId.ToString())));
        Assert.Equal(HttpStatusCode.Redirect, allowed.StatusCode);
    }

    [Fact]
    public async Task Direct_room_is_only_visible_to_its_members()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var member = await Join(client, await JoinCode(host), "Member", "member@example.com");
        var other = await Join(client, await JoinCode(host), "Other", "other@example.com");
        long memberId;
        using (var connection = host.Open())
        {
            memberId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'member@example.com'")!;
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE accounts SET settings = $settings";
            command.Parameters.AddWithValue("$settings", "{\"restrict_room_creation_to_administrators\":\"true\"}");
            command.ExecuteNonQuery();
        }

        using var created = await Send(client, HttpMethod.Post, "/rooms/directs", admin, Form(("user_ids[]", memberId.ToString())));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var path = created.Headers.Location!.AbsolutePath;
        var roomId = RoomId(path);

        using var again = await Send(client, HttpMethod.Post, "/rooms/directs", member, Form(("user_ids[]", "1")));
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Equal(path, again.Headers.Location?.AbsolutePath);

        using (var connection = host.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM rooms WHERE type = 'Rooms::Direct'"));
            Assert.Equal("Rooms::Direct", Scalar(connection, "SELECT type FROM rooms WHERE id = " + roomId));
            Assert.Equal("everything", Scalar(connection, "SELECT involvement FROM memberships WHERE room_id = " + roomId + " LIMIT 1"));
        }

        using (var adminView = await Send(client, HttpMethod.Get, path, admin))
        {
            Assert.Equal(HttpStatusCode.OK, adminView.StatusCode);
            Assert.Contains("Member", await adminView.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var memberView = await Send(client, HttpMethod.Get, path, member))
        {
            Assert.Equal(HttpStatusCode.OK, memberView.StatusCode);
            Assert.Contains("New Person", await memberView.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var outsider = await Send(client, HttpMethod.Get, path, other))
        {
            Assert.Equal(HttpStatusCode.Redirect, outsider.StatusCode);
            Assert.Equal("/", outsider.Headers.Location?.AbsolutePath);
        }

        using (var typed = await Send(client, HttpMethod.Get, "/rooms/directs/" + roomId, other))
        {
            Assert.Equal(HttpStatusCode.Redirect, typed.StatusCode);
            Assert.Equal("/", typed.Headers.Location?.AbsolutePath);
        }

        using (var promote = await Send(client, HttpMethod.Put, "/rooms/opens/" + roomId, admin, Form(("room[name]", "Watercooler"))))
        {
            Assert.Equal(HttpStatusCode.Redirect, promote.StatusCode);
            Assert.Equal("/", promote.Headers.Location?.AbsolutePath);
        }

        using (var convert = await Send(
            client,
            HttpMethod.Put,
            "/rooms/closeds/" + roomId,
            admin,
            Form(("room[name]", "Watercooler"), ("user_ids[]", "1"), ("user_ids[]", memberId.ToString()))))
        {
            Assert.Equal(HttpStatusCode.Redirect, convert.StatusCode);
            Assert.Equal("/", convert.Headers.Location?.AbsolutePath);
        }

        using (var connection = host.Open())
        {
            Assert.Equal("Rooms::Direct", Scalar(connection, "SELECT type FROM rooms WHERE id = " + roomId));
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM memberships WHERE room_id = " + roomId));
        }

        using (var missed = await Send(client, HttpMethod.Delete, "/rooms/directs/" + roomId, other))
        {
            Assert.Equal(HttpStatusCode.Redirect, missed.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM rooms WHERE id = " + roomId));
        }

        using var removed = await Send(client, HttpMethod.Delete, "/rooms/directs/" + roomId, member);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        Assert.Equal("/", removed.Headers.Location?.AbsolutePath);
        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM rooms WHERE id = " + roomId));
        }
    }

    [Fact]
    public async Task Involvement_update_changes_the_membership()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var other = await Join(client, await JoinCode(host), "Other", "other@example.com");
        long roomId;
        long membershipId;
        using (var connection = host.Open())
        {
            roomId = (long)Scalar(connection, "SELECT id FROM rooms")!;
            membershipId = (long)Scalar(connection, "SELECT id FROM memberships")!;
            Assert.Equal("mentions", Scalar(connection, "SELECT involvement FROM memberships WHERE id = " + membershipId));
        }

        using (var show = await Send(client, HttpMethod.Get, "/rooms/" + roomId + "/involvement", admin))
        {
            Assert.Equal(HttpStatusCode.OK, show.StatusCode);
            var html = await show.Content.ReadAsStringAsync();
            Assert.Contains("mentions", html, StringComparison.Ordinal);
            Assert.Contains("name=\"_method\" value=\"put\"", html, StringComparison.Ordinal);
            Assert.Contains("type=\"submit\"", html, StringComparison.Ordinal);
        }

        using var updated = await Send(
            client,
            HttpMethod.Put,
            "/rooms/" + roomId + "/involvement",
            admin,
            Form(("involvement", "everything")));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal("/rooms/" + roomId + "/involvement", updated.Headers.Location?.AbsolutePath);

        using (var connection = host.Open())
        {
            Assert.Equal("everything", Scalar(connection, "SELECT involvement FROM memberships WHERE id = " + membershipId));
        }

        using (var missing = await Send(client, HttpMethod.Get, "/rooms/999/involvement", other))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var refresh = await Send(client, HttpMethod.Get, "/rooms/" + roomId + "/refresh?since=0", admin))
        {
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        }

        using (var settings = await Send(client, HttpMethod.Get, "/rooms/" + roomId + "/settings", admin))
        {
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
            Assert.Contains("All Talk", await settings.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var now = DateTime.UtcNow;
        using (var connection = host.Open())
        {
            MembershipStore.Connected(connection, membershipId, now);
            var row = MembershipStore.Find(connection, membershipId);
            Assert.NotNull(row);
            Assert.True(MembershipStore.IsConnected(row.Value, now));
            Assert.Equal(1, row.Value.Connections);

            MembershipStore.Connected(connection, membershipId, now);
            row = MembershipStore.Find(connection, membershipId);
            Assert.Equal(2, row!.Value.Connections);

            MembershipStore.Disconnected(connection, membershipId, now);
            row = MembershipStore.Find(connection, membershipId);
            Assert.True(MembershipStore.IsConnected(row!.Value, now));
            Assert.Equal(1, row.Value.Connections);

            MembershipStore.Disconnected(connection, membershipId, now);
            row = MembershipStore.Find(connection, membershipId);
            Assert.False(MembershipStore.IsConnected(row!.Value, now.AddSeconds(61)));
            Assert.Equal(0, row.Value.Connections);

            MembershipStore.Present(connection, membershipId, now.AddSeconds(120));
            row = MembershipStore.Find(connection, membershipId);
            Assert.True(MembershipStore.IsConnected(row!.Value, now.AddSeconds(120)));
            Assert.Equal(1, row.Value.Connections);

            MembershipStore.MarkUnreadExcept(connection, roomId, exceptUserId: 999, unreadAt: MembershipStore.Format(now), now.AddSeconds(200));
            row = MembershipStore.Find(connection, membershipId);
            Assert.False(string.IsNullOrEmpty(row!.Value.UnreadAt));
            Assert.True(MembershipStore.IsUnread(row.Value));

            MembershipStore.Read(connection, membershipId);
            row = MembershipStore.Find(connection, membershipId);
            Assert.False(MembershipStore.IsUnread(row!.Value));
        }
    }

    private static async Task<string> FirstRun(HttpClient client)
    {
        using var response = await client.PostAsync("/first_run", Form(
            ("user[name]", "New Person"),
            ("user[email_address]", "new@37signals.com"),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Cookie(response);
    }

    private static async Task<string> JoinCode(Host host)
    {
        using var connection = host.Open();
        return (string)Scalar(connection, "SELECT join_code FROM accounts")!;
    }

    private static async Task<string> Join(HttpClient client, string code, string name, string email)
    {
        using var response = await client.PostAsync("/join/" + code, Form(
            ("user[name]", name),
            ("user[email_address]", email),
            ("user[password]", "secret123456")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return Cookie(response);
    }

    private static async Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string path,
        string? cookie = null,
        HttpContent? form = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", "session_token=" + cookie);
        }

        if (form is not null)
        {
            request.Content = form;
        }

        return await client.SendAsync(request);
    }

    private static string Cookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var signed = SessionCookie.ValueFromSetCookieHeader(string.Join("\n", values!));
        Assert.False(string.IsNullOrEmpty(signed));
        return signed!;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields)
    {
        return new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
    }

    private static string RoomId(string path)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _path;
        private readonly WebApplicationFactory<Program> _factory;

        public Host()
        {
            _path = Path.Combine(Path.GetTempPath(), $"campfire-rooms-{Guid.NewGuid():N}.sqlite");
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
