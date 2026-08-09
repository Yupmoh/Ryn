namespace Ryn.Core;

/// <summary>Incoming request delivered to a custom URL-scheme handler.</summary>
public readonly record struct RynSchemeRequest(
    Uri Url,
    string Method,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>Creates a request without headers (legacy constructor).</summary>
    public RynSchemeRequest(Uri url, string method, ReadOnlyMemory<byte> body)
        : this(url, method, body, null) { }

    /// <summary>Deconstructs the request fields exposed by the original API.</summary>
    public void Deconstruct(out Uri url, out string method, out ReadOnlyMemory<byte> body)
    {
        url = Url;
        method = Method;
        body = Body;
    }

    /// <summary>Returns a request header value, or <see langword="null"/> when absent.</summary>
    public string? GetHeader(string name) => Headers is not null && Headers.TryGetValue(name, out var value) ? value : null;
}
