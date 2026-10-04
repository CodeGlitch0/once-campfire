namespace Campfire.Web.Http;

/// <summary>
/// Rails <c>ActionDispatch::MethodOverride</c> for the verbs these views submit.
/// On POST only, <c>_method</c> of PUT, PATCH, or DELETE (query, else form) is rewritten
/// before endpoint matching. Other verbs and other override values are left alone.
/// </summary>
public static class MethodOverride
{
    public static async Task ApplyAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return;
        }

        var method = Last(request.Query["_method"]);
        if (string.IsNullOrEmpty(method) && request.HasFormContentType)
        {
            try
            {
                var form = await request.ReadFormAsync();
                method = Last(form["_method"]);
            }
            catch (BadHttpRequestException)
            {
                return;
            }
            catch (InvalidDataException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
        }

        if (string.IsNullOrEmpty(method))
        {
            return;
        }

        var normalized = method.Trim().ToUpperInvariant();
        if (normalized is "PUT" or "PATCH" or "DELETE")
        {
            request.Method = normalized;
        }
    }

    private static string? Last(Microsoft.Extensions.Primitives.StringValues values)
    {
        return values.Count == 0 ? null : values[values.Count - 1];
    }
}
