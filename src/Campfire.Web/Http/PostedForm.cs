using Campfire.Web.Identity;

namespace Campfire.Web.Http;

public static class PostedForm
{
    public static async Task<IFormCollection?> Read(HttpRequest request)
    {
        if (!request.HasFormContentType)
        {
            return null;
        }

        return await request.ReadFormAsync();
    }

    public static bool HasPrefix(IFormCollection? form, IQueryCollection query, string prefix)
    {
        if (form is not null)
        {
            if (form.Keys.Any(key => key == prefix || key.StartsWith(prefix + "[", StringComparison.Ordinal)))
            {
                return true;
            }

            if (form.Files.Any(file => file.Name == prefix || file.Name.StartsWith(prefix + "[", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return query.Keys.Any(key => key == prefix || key.StartsWith(prefix + "[", StringComparison.Ordinal));
    }

    public static bool Has(IFormCollection? form, IQueryCollection query, string key)
    {
        if (form is not null && form.ContainsKey(key))
        {
            return true;
        }

        return query.ContainsKey(key);
    }

    public static string? Optional(IFormCollection? form, IQueryCollection query, string key)
    {
        if (!Has(form, query, key))
        {
            return null;
        }

        if (form is not null && form.ContainsKey(key))
        {
            return form[key].ToString();
        }

        return query[key].ToString();
    }

    public static async Task<BlobPayload?> File(IFormCollection? form, string fieldName)
    {
        if (form is null)
        {
            return null;
        }

        var file = form.Files.GetFile(fieldName);
        if (file is null || file.Length <= 0)
        {
            return null;
        }

        using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        var type = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var filename = string.IsNullOrWhiteSpace(file.FileName) ? "blob" : Path.GetFileName(file.FileName);
        return new BlobPayload(filename, type, memory.ToArray());
    }
}
