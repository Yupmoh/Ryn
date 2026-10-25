namespace Ryn.iOS.Internal;

internal static class MimeTypeHelper
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "application/javascript",
        [".mjs"] = "application/javascript",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".wav"] = "audio/wav",
        [".txt"] = "text/plain",
        [".wasm"] = "application/wasm",
    };

    public static string GetMimeType(string extension) =>
        MimeTypes.TryGetValue(extension, out var mime) ? mime : "application/octet-stream";
}
