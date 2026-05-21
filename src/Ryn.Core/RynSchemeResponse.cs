namespace Ryn.Core;

public readonly record struct RynSchemeResponse(
    int StatusCode,
    string ContentType,
    ReadOnlyMemory<byte> Body)
{
    public static RynSchemeResponse Ok(ReadOnlyMemory<byte> body, string contentType = "application/octet-stream") =>
        new(200, contentType, body);

    public static RynSchemeResponse Json(ReadOnlyMemory<byte> body) =>
        new(200, "application/json", body);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Intentional instance factory method")]
    public static RynSchemeResponse NotFound() =>
        new(404, "text/plain", "Not Found"u8.ToArray());
}
