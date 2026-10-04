namespace Campfire.Web.Http;

/// <summary>
/// Avatar and logo responses. Rails serves a webp variant for avatars and a png variant for logos.
/// Non-images (including stored HTML and SVG) are not returned. If a raster image cannot be
/// transcoded to that type, it is downloaded rather than inlined as the client type.
/// </summary>
public static class ImageResponses
{
    public static IResult? Avatar(byte[] bytes)
    {
        var sniffed = ImageSniff.Detect(bytes);
        if (sniffed is null)
        {
            return null;
        }

        if (sniffed == ImageSniff.Webp)
        {
            return Responses.InlineBytes(bytes, ImageSniff.Webp);
        }

        var webp = ImageSniff.TryTranscode(bytes, "webp");
        return webp is null
            ? Responses.Attachment(bytes, sniffed)
            : Responses.InlineBytes(webp, ImageSniff.Webp);
    }

    public static IResult? Logo(byte[] bytes)
    {
        var sniffed = ImageSniff.Detect(bytes);
        if (sniffed is null)
        {
            return null;
        }

        if (sniffed == ImageSniff.Png)
        {
            return Responses.InlineBytes(bytes, ImageSniff.Png);
        }

        var png = ImageSniff.TryTranscode(bytes, "png");
        return png is null
            ? Responses.Attachment(bytes, sniffed)
            : Responses.InlineBytes(png, ImageSniff.Png);
    }
}
