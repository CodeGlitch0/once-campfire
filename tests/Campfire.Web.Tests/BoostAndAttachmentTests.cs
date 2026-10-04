using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Campfire.Web.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Campfire.Web.Tests;

public sealed class BoostAndAttachmentTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04];

    [Fact]
    public async Task Rendered_boost_form_has_maxlength_16()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var messageId = await PostMessage(client, admin, await OpenRoom(client, admin));

        using var form = await Send(client, HttpMethod.Get, "/messages/" + messageId + "/boosts/new", admin);
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        var html = await form.Content.ReadAsStringAsync();
        Assert.Contains("name=\"boost[content]\"", html, StringComparison.Ordinal);
        Assert.Contains("maxlength=\"16\"", html, StringComparison.Ordinal);
        Assert.Contains("type=\"submit\"", html, StringComparison.Ordinal);
        Assert.Contains("method=\"post\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_and_destroy_a_boost()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var member = await Join(client, await JoinCode(host), "Member", "member@example.com");
        var room = await OpenRoom(client, admin);
        var messageId = await PostMessage(client, admin, room);

        using var created = await Send(
            client,
            HttpMethod.Post,
            "/messages/" + messageId + "/boosts",
            admin,
            Form(("boost[content]", "Morning!")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal("/messages/" + messageId + "/boosts", created.Headers.Location?.AbsolutePath);

        long boostId;
        using (var connection = host.Open())
        {
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM boosts WHERE message_id = " + messageId));
            Assert.Equal("Morning!", Scalar(connection, "SELECT content FROM boosts WHERE message_id = " + messageId));
            boostId = (long)Scalar(connection, "SELECT id FROM boosts WHERE message_id = " + messageId)!;
        }

        const string seventeen = "12345678901234567";
        Assert.Equal(17, seventeen.Length);
        using (var longer = await Send(
            client,
            HttpMethod.Post,
            "/messages/" + messageId + "/boosts",
            member,
            Form(("boost[content]", seventeen))))
        {
            Assert.Equal(HttpStatusCode.Redirect, longer.StatusCode);
            Assert.NotEqual(HttpStatusCode.BadRequest, longer.StatusCode);
            Assert.NotEqual((HttpStatusCode)422, longer.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal(seventeen, Scalar(connection, "SELECT content FROM boosts WHERE booster_id != (SELECT booster_id FROM boosts WHERE id = " + boostId + ")"));
        }

        using (var index = await Send(client, HttpMethod.Get, "/messages/" + messageId + "/boosts", admin))
        {
            var html = await index.Content.ReadAsStringAsync();
            Assert.Contains("Morning!", html, StringComparison.Ordinal);
            Assert.Contains(
                "<form action=\"/messages/" + messageId + "/boosts/" + boostId + "\" method=\"post\">",
                html,
                StringComparison.Ordinal);
            Assert.Contains("name=\"_method\" value=\"delete\"", html, StringComparison.Ordinal);
            Assert.Contains("type=\"submit\"", html, StringComparison.Ordinal);
        }

        using (var stolen = await Send(
            client,
            HttpMethod.Post,
            "/messages/" + messageId + "/boosts/" + boostId,
            member,
            Form(("_method", "delete"))))
        {
            Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
        }

        using (var connection = host.Open())
        {
            Assert.Equal("Morning!", Scalar(connection, "SELECT content FROM boosts WHERE id = " + boostId));
        }

        using var removed = await Send(
            client,
            HttpMethod.Post,
            "/messages/" + messageId + "/boosts/" + boostId,
            admin,
            Form(("_method", "delete")));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using (var connection = host.Open())
        {
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM boosts WHERE id = " + boostId));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM boosts WHERE content = '" + seventeen + "'"));
        }
    }

    [Fact]
    public async Task Non_member_cannot_boost()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var outsider = await Join(client, await JoinCode(host), "Outsider", "out@example.com");
        long adminId;
        using (var connection = host.Open())
        {
            adminId = (long)Scalar(connection, "SELECT id FROM users WHERE email_address = 'new@37signals.com'")!;
        }

        using var room = await Send(
            client,
            HttpMethod.Post,
            "/rooms/closeds",
            admin,
            Form(("room[name]", "Secret"), ("user_ids[]", adminId.ToString(CultureInfo.InvariantCulture))));
        Assert.Equal(HttpStatusCode.Redirect, room.StatusCode);
        var roomPath = room.Headers.Location!.AbsolutePath;
        var messageId = await PostMessage(client, admin, roomPath);

        using (var denied = await Send(
            client,
            HttpMethod.Post,
            "/messages/" + messageId + "/boosts",
            outsider,
            Form(("boost[content]", "Nope"))))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }

        using (var form = await Send(client, HttpMethod.Get, "/messages/" + messageId + "/boosts/new", outsider))
        {
            Assert.Equal(HttpStatusCode.NotFound, form.StatusCode);
        }

        using var connectionAfter = host.Open();
        Assert.Equal(0L, Scalar(connectionAfter, "SELECT COUNT(*) FROM boosts"));
    }

    [Fact]
    public async Task Upload_image_on_message_create_and_read_the_bytes_back()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        var admin = await FirstRun(client);
        var room = await OpenRoom(client, admin);
        var roomId = room.Split('/')[^1];

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("With photo"), "message[body]");
        var file = new ByteArrayContent(PngBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        content.Add(file, "message[attachment]", "photo.png");

        using var created = await Send(client, HttpMethod.Post, room + "/messages", admin, content);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("text/html", created.Content.Headers.ContentType?.MediaType);
        var page = await created.Content.ReadAsStringAsync();
        var messageId = Regex.Match(page, "data-message-id=\"(\\d+)\"").Groups[1].Value;
        Assert.Contains("/rooms/" + roomId + "/messages/" + messageId + "/attachment", page, StringComparison.Ordinal);
        Assert.Contains("photo.png", page, StringComparison.Ordinal);

        using (var connection = host.Open())
        {
            Assert.Equal("image/png", Scalar(connection, "SELECT content_type FROM active_storage_blobs"));
            Assert.NotEqual("text/html", Scalar(connection, "SELECT content_type FROM active_storage_blobs"));
        }

        using var shown = await Send(client, HttpMethod.Get, "/rooms/" + roomId + "/messages/" + messageId + "/attachment", admin);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("image/png", shown.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("text/html", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", shown.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(PngBytes, await shown.Content.ReadAsByteArrayAsync());

        using var svgContent = new MultipartFormDataContent();
        svgContent.Add(new StringContent("svg"), "message[body]");
        var svg = new ByteArrayContent("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>stored-svg</script></svg>"u8.ToArray());
        svg.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        svgContent.Add(svg, "message[attachment]", "evil.svg");
        using var svgCreated = await Send(client, HttpMethod.Post, room + "/messages", admin, svgContent);
        var svgPage = await svgCreated.Content.ReadAsStringAsync();
        var svgId = Regex.Match(svgPage, "data-message-id=\"(\\d+)\"").Groups[1].Value;
        using var svgShown = await Send(client, HttpMethod.Get, "/rooms/" + roomId + "/messages/" + svgId + "/attachment", admin);
        Assert.Equal(HttpStatusCode.OK, svgShown.StatusCode);
        Assert.NotEqual("image/svg+xml", svgShown.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("text/html", svgShown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", svgShown.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("application/octet-stream", svgShown.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<long> PostMessage(HttpClient client, string cookie, string roomPath)
    {
        using var created = await Send(
            client,
            HttpMethod.Post,
            roomPath + "/messages",
            cookie,
            Form(("message[body]", "Hello"), ("message[client_message_id]", "client-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var html = await created.Content.ReadAsStringAsync();
        return long.Parse(Regex.Match(html, "data-message-id=\"(\\d+)\"").Groups[1].Value, CultureInfo.InvariantCulture);
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
            _path = Path.Combine(Path.GetTempPath(), $"campfire-boosts-{Guid.NewGuid():N}.sqlite");
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
            try
            {
                if (Directory.Exists(_path + ".files"))
                {
                    Directory.Delete(_path + ".files", recursive: true);
                }
            }
            catch (IOException)
            {
            }
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
