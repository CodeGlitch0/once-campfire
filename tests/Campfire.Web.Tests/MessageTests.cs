using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Campfire.Web.Identity;
using Campfire.Web.Rooms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests;

public sealed class MessageTests
{
    [Fact]
    public async Task Create_a_message_and_read_it_back_including_client_message_id()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var room = await OpenRoom(client, admin);

        using (var shown = await Send(client, HttpMethod.Get, room, admin))
        {
            var html = await shown.Content.ReadAsStringAsync();
            Assert.Contains("enctype=\"multipart/form-data\"", html, StringComparison.Ordinal);
            Assert.Contains("type=\"file\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"message[body]\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"message[client_message_id]\"", html, StringComparison.Ordinal);
            Assert.Contains("type=\"submit\"", html, StringComparison.Ordinal);
        }

        using var created = await Send(
            client,
            HttpMethod.Post,
            room + "/messages",
            admin,
            Form(("message[body]", "New one"), ("message[client_message_id]", "client-999")));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var page = await created.Content.ReadAsStringAsync();
        Assert.Contains("New one", page, StringComparison.Ordinal);
        Assert.Contains("data-client-message-id=\"client-999\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"message_client-999\"", page, StringComparison.Ordinal);

        long messageId;
        using (var connection = host.Open())
        {
            messageId = (long)Scalar(connection, "SELECT id FROM messages WHERE client_message_id = 'client-999'")!;
            Assert.Equal("New one", Scalar(connection, "SELECT body FROM action_text_rich_texts WHERE record_type = 'Message' AND name = 'body' AND record_id = " + messageId));
            Assert.Equal("New one", Scalar(connection, "SELECT body FROM message_search_index WHERE rowid = " + messageId));
        }

        using var read = await Send(client, HttpMethod.Get, room + "/messages/" + messageId, admin);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var again = await read.Content.ReadAsStringAsync();
        Assert.Contains("New one", again, StringComparison.Ordinal);
        Assert.Contains("data-client-message-id=\"client-999\"", again, StringComparison.Ordinal);

        using (var edit = await Send(client, HttpMethod.Get, room + "/messages/" + messageId + "/edit", admin))
        {
            var html = await edit.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            Assert.Contains("name=\"_method\" value=\"patch\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"_method\" value=\"delete\"", html, StringComparison.Ordinal);
            Assert.Contains("type=\"submit\"", html, StringComparison.Ordinal);
        }

        using var generated = await Send(
            client,
            HttpMethod.Post,
            room + "/messages",
            admin,
            Form(("message[body]", "Bare")));
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        using (var connection = host.Open())
        {
            var clientId = (string)Scalar(connection, "SELECT client_message_id FROM messages WHERE id != " + messageId)!;
            Assert.False(string.IsNullOrEmpty(clientId));
        }
    }

    [Fact]
    public async Task Update_and_destroy_match_Rails_authorization()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var member = await Join(client, await JoinCode(host), "Member", "member@example.com");
        var other = await Join(client, await JoinCode(host), "Other", "other@example.com");
        var room = await OpenRoom(client, admin);
        long outsiderRoom;
        using (var connection = host.Open())
        {
            outsiderRoom = (long)Scalar(connection, "SELECT id FROM rooms WHERE name = 'All Talk'")!;
        }

        using var created = await Send(
            client,
            HttpMethod.Post,
            room + "/messages",
            member,
            Form(("message[body]", "Mine"), ("message[client_message_id]", "mine-1")));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var messageId = MessageId(await created.Content.ReadAsStringAsync());

        using (var forbidden = await Send(
            client,
            HttpMethod.Post,
            room + "/messages/" + messageId,
            other,
            Form(("_method", "patch"), ("message[body]", "Stolen"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal("Mine", Scalar(connection, "SELECT body FROM action_text_rich_texts WHERE record_id = " + messageId));
        }

        using (var outsider = await Send(
            client,
            HttpMethod.Post,
            "/rooms/" + outsiderRoom + "/messages/" + messageId,
            other,
            Form(("_method", "patch"), ("message[body]", "Stolen"))))
        {
            Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        }

        using var updated = await Send(
            client,
            HttpMethod.Post,
            room + "/messages/" + messageId,
            member,
            Form(("_method", "patch"), ("message[body]", "Updated body")));
        Assert.Equal(HttpStatusCode.Redirect, updated.StatusCode);
        Assert.Equal(room + "/messages/" + messageId, updated.Headers.Location?.AbsolutePath);
        using (var connection = host.Open())
        {
            Assert.Equal("Updated body", Scalar(connection, "SELECT body FROM action_text_rich_texts WHERE record_id = " + messageId));
            Assert.Equal("mine-1", Scalar(connection, "SELECT client_message_id FROM messages WHERE id = " + messageId));
        }

        using var adminUpdated = await Send(
            client,
            HttpMethod.Put,
            room + "/messages/" + messageId,
            admin,
            Form(("message[body]", "Admin edit")));
        Assert.Equal(HttpStatusCode.Redirect, adminUpdated.StatusCode);
        using (var connection = host.Open())
        {
            Assert.Equal("Admin edit", Scalar(connection, "SELECT body FROM action_text_rich_texts WHERE record_id = " + messageId));
        }

        using (var forbiddenDelete = await Send(
            client,
            HttpMethod.Post,
            room + "/messages/" + messageId,
            other,
            Form(("_method", "delete"))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE id = " + messageId));
        }

        using var adminDeleted = await Send(
            client,
            HttpMethod.Post,
            room + "/messages/" + messageId,
            admin,
            Form(("_method", "delete")));
        Assert.Equal(HttpStatusCode.OK, adminDeleted.StatusCode);
        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE id = " + messageId));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM action_text_rich_texts WHERE record_id = " + messageId));
        }

        using var own = await Send(
            client,
            HttpMethod.Post,
            room + "/messages",
            member,
            Form(("message[body]", "Again"), ("message[client_message_id]", "mine-2")));
        var ownId = MessageId(await own.Content.ReadAsStringAsync());
        using var ownDeleted = await Send(
            client,
            HttpMethod.Delete,
            room + "/messages/" + ownId,
            member);
        Assert.Equal(HttpStatusCode.OK, ownDeleted.StatusCode);
        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM messages WHERE id = " + ownId));
        }
    }

    [Fact]
    public async Task Pagination_matches_the_Rails_page_of_forty()
    {
        Assert.Equal(40, MessageStore.PageSize);

        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var roomPath = await OpenRoom(client, admin);
        var roomId = long.Parse(roomPath.Split('/')[^1], CultureInfo.InvariantCulture);
        long userId;
        using (var connection = host.Open())
        {
            userId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'new@37signals.com'")!;
        }

        long third;
        long first;
        long fifth;
        using (var connection = host.Open())
        {
            var small = new long[5];
            for (var i = 0; i < small.Length; i++)
            {
                small[i] = MessageStore.Create(
                    connection,
                    roomId,
                    userId,
                    "small-" + i,
                    "small-" + i,
                    Stamp(i + 1)).Id;
            }

            first = small[0];
            third = small[2];
            fifth = small[4];
        }

        using (var index = await Send(client, HttpMethod.Get, roomPath + "/messages", admin))
        {
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            var html = await index.Content.ReadAsStringAsync();
            Assert.Equal(new[] { first, first + 1, third, first + 3, fifth }, Ids(html));
            Assert.Contains("small-4", html, StringComparison.Ordinal);
        }

        using (var before = await Send(client, HttpMethod.Get, roomPath + "/messages?before=" + third, admin))
        {
            var ids = Ids(await before.Content.ReadAsStringAsync());
            Assert.Equal(new[] { first, first + 1 }, ids);
            Assert.DoesNotContain(third, ids);
            Assert.DoesNotContain(fifth, ids);
        }

        using (var after = await Send(client, HttpMethod.Get, roomPath + "/messages?after=" + third, admin))
        {
            var ids = Ids(await after.Content.ReadAsStringAsync());
            Assert.Equal(new[] { third + 1, fifth }, ids);
            Assert.DoesNotContain(first, ids);
            Assert.DoesNotContain(third, ids);
        }

        var tied = new List<long>();
        using (var connection = host.Open())
        {
            for (var i = 0; i < MessageStore.PageSize + 1; i++)
            {
                tied.Add(MessageStore.Create(
                    connection,
                    roomId,
                    userId,
                    "tie-" + i,
                    "tie-" + i,
                    Stamp(10)).Id);
            }
        }

        using (var last = await Send(client, HttpMethod.Get, roomPath + "/messages", admin))
        {
            var ids = Ids(await last.Content.ReadAsStringAsync());
            Assert.Equal(MessageStore.PageSize, ids.Count);
            Assert.DoesNotContain(tied[0], ids);
            Assert.Equal(tied.Skip(1).ToArray(), ids);
        }

        using (var emptyRoom = await Send(client, HttpMethod.Get, "/rooms/" + EmptyRoom(host) + "/messages", admin))
        {
            Assert.Equal(HttpStatusCode.NoContent, emptyRoom.StatusCode);
        }
    }

    [Fact]
    public async Task Get_room_at_message_resolves_that_message()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var roomPath = await OpenRoom(client, admin);
        var roomId = long.Parse(roomPath.Split('/')[^1], CultureInfo.InvariantCulture);
        long first;
        long last;
        using (var connection = host.Open())
        {
            var userId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'new@37signals.com'")!;
            first = 0;
            last = 0;
            for (var i = 0; i < MessageStore.PageSize + 5; i++)
            {
                var id = MessageStore.Create(
                    connection,
                    roomId,
                    userId,
                    "around-" + i,
                    "around-" + i,
                    Stamp(i + 1)).Id;
                if (i == 0)
                {
                    first = id;
                }

                last = id;
            }
        }

        using (var permalink = await Send(client, HttpMethod.Get, roomPath + "/@" + first, admin))
        {
            Assert.Equal(HttpStatusCode.OK, permalink.StatusCode);
            var html = await permalink.Content.ReadAsStringAsync();
            var ids = Ids(html);
            Assert.Contains(first, ids);
            Assert.DoesNotContain(last, ids);
            Assert.Contains("around-0", html, StringComparison.Ordinal);
            Assert.Contains("Design", html, StringComparison.Ordinal);
        }

        using (var missing = await Send(client, HttpMethod.Get, roomPath + "/@999999", admin))
        {
            Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
            var html = await missing.Content.ReadAsStringAsync();
            var ids = Ids(html);
            Assert.Contains(last, ids);
            Assert.DoesNotContain(first, ids);
            Assert.Contains("Design", html, StringComparison.Ordinal);
        }

        using (var tail = await Send(client, HttpMethod.Get, roomPath, admin))
        {
            var ids = Ids(await tail.Content.ReadAsStringAsync());
            Assert.Contains(last, ids);
            Assert.DoesNotContain(first, ids);
        }
    }

    private static long EmptyRoom(Host host)
    {
        using var connection = host.Open();
        return (long)Scalar(connection, "SELECT id FROM rooms WHERE name = 'All Talk'")!;
    }

    private static string Stamp(int second)
    {
        return new DateTime(2020, 1, 1, 0, 0, second, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<long> Ids(string html)
    {
        return Regex.Matches(html, "data-message-id=\"(\\d+)\"")
            .Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static long MessageId(string html)
    {
        return Ids(html)[0];
    }

    private static async Task<string> OpenRoom(HttpClient client, string cookie)
    {
        using var created = await Send(client, HttpMethod.Post, "/rooms/opens", cookie, Form(("room[name]", "Design")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        return created.Headers.Location!.AbsolutePath;
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
            _path = Path.Combine(Path.GetTempPath(), $"campfire-messages-{Guid.NewGuid():N}.sqlite");
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
