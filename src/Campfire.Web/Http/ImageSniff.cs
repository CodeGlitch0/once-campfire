using System.Diagnostics;

namespace Campfire.Web.Http;

/// <summary>
/// Image type from magic bytes. Client and stored Content-Type are not trusted.
/// </summary>
public static class ImageSniff
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";

    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes))
        {
            return Png;
        }

        if (IsJpeg(bytes))
        {
            return Jpeg;
        }

        if (IsGif(bytes))
        {
            return Gif;
        }

        if (IsWebp(bytes))
        {
            return Webp;
        }

        return null;
    }

    public static byte[]? TryTranscode(byte[] bytes, string format)
    {
        if (bytes.Length < 16 || Detect(bytes) is null)
        {
            return null;
        }

        var expected = format == "webp" ? Webp : format == "png" ? Png : null;
        if (expected is null)
        {
            return null;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-loglevel");
            start.ArgumentList.Add("error");
            start.ArgumentList.Add("-i");
            start.ArgumentList.Add("pipe:0");
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(format);
            start.ArgumentList.Add("pipe:1");

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            process.StandardInput.BaseStream.Write(bytes);
            process.StandardInput.Close();
            if (!copy.Wait(TimeSpan.FromSeconds(5)) || !process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            if (process.ExitCode != 0 || output.Length == 0)
            {
                return null;
            }

            var transcoded = output.ToArray();
            return Detect(transcoded) == expected ? transcoded : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[4] == 0x0D
            && bytes[5] == 0x0A
            && bytes[6] == 0x1A
            && bytes[7] == 0x0A;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
    }

    private static bool IsGif(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 6
            && bytes[0] == (byte)'G'
            && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'8'
            && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9')
            && bytes[5] == (byte)'a';
    }

    private static bool IsWebp(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 12
            && bytes[0] == (byte)'R'
            && bytes[1] == (byte)'I'
            && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W'
            && bytes[9] == (byte)'E'
            && bytes[10] == (byte)'B'
            && bytes[11] == (byte)'P';
    }
}
