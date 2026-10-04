using System.Net;

namespace Campfire.Web.Http;

public static class Responses
{
    public static IResult Redirect(HttpContext context, string pathAndQuery)
    {
        return Results.Redirect($"{context.Request.Scheme}://{context.Request.Host}{pathAndQuery}", permanent: false);
    }

    public static IResult Html(string body, int statusCode = StatusCodes.Status200OK)
    {
        return Results.Content(body, "text/html; charset=utf-8", statusCode: statusCode);
    }

    public static IResult Empty(int statusCode)
    {
        return Results.StatusCode(statusCode);
    }

    public static IResult InlineBytes(byte[] bytes, string contentType)
    {
        return new InlineFile(bytes, contentType);
    }

    public static string Encode(string? value)
    {
        return WebUtility.HtmlEncode(value ?? "");
    }

    private sealed class InlineFile(byte[] bytes, string contentType) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            httpContext.Response.ContentType = contentType;
            httpContext.Response.Headers.ContentDisposition = "inline";
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            await httpContext.Response.Body.WriteAsync(bytes);
        }
    }
}
