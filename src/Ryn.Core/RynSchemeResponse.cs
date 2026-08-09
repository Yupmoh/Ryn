using System.IO;

namespace Ryn.Core;

/// <summary>Response returned by a custom URL-scheme handler.</summary>
public readonly record struct RynSchemeResponse(
    int StatusCode,
    string ContentType,
    ReadOnlyMemory<byte> Body,
    Stream? Content = null,
    long? ContentLength = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>Creates a response using the original three-argument API.</summary>
    public RynSchemeResponse(int statusCode, string contentType, ReadOnlyMemory<byte> body)
        : this(statusCode, contentType, body, null, null, null) { }

    /// <summary>Deconstructs the response fields exposed by the original API.</summary>
    public void Deconstruct(out int statusCode, out string contentType, out ReadOnlyMemory<byte> body)
    {
        statusCode = StatusCode;
        contentType = ContentType;
        body = Body;
    }
    /// <summary>Creates an in-memory successful response.</summary>
    public static RynSchemeResponse Ok(ReadOnlyMemory<byte> body, string contentType = "application/octet-stream") =>
        new(200, contentType, body);

    /// <summary>Creates a JSON response from UTF-8 bytes.</summary>
    public static RynSchemeResponse Json(ReadOnlyMemory<byte> body) =>
        new(200, "application/json", body);

    /// <summary>Creates a streamed successful response. The stream is consumed and disposed by the serving path.</summary>
    /// <remarks>
    /// Saucer currently accepts a contiguous stash, so the serving path materializes this stream before handing it
    /// to native code. The stream is still preferable for compatibility and ownership safety, but it is not zero-copy.
    /// </remarks>
    public static RynSchemeResponse Stream(Stream content, long contentLength, string contentType = "application/octet-stream")
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        return new(200, contentType, ReadOnlyMemory<byte>.Empty, content, contentLength);
    }

    /// <summary>Creates a response backed by a file. The serving path materializes the requested file bytes into Saucer's contiguous stash.</summary>
    /// <remarks>This API does not provide zero-copy file serving; the native Saucer stash contract requires contiguous content.</remarks>
    public static RynSchemeResponse File(string path, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
#pragma warning disable CA2000 // Ownership transfers to the streamed response and serving path disposes it.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            return Stream(stream, stream.Length, contentType ?? "application/octet-stream") with
            {
                Headers = new Dictionary<string, string> { ["Accept-Ranges"] = "bytes" }
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Creates a file response for a single HTTP byte range.</summary>
    /// <remarks>The returned response is 206 with only the requested bytes, or 416 for malformed/unsatisfiable ranges.</remarks>
    public static RynSchemeResponse FileRange(string path, string range, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(range);
        var info = new FileInfo(path);
        if (!info.Exists)
            return new(404, "text/plain", "Not Found"u8.ToArray());
        if (!TryParseRange(range, info.Length, out var start, out var length))
            return new(416, "text/plain", ReadOnlyMemory<byte>.Empty, null, 0,
                new Dictionary<string, string> { ["Content-Range"] = $"bytes */{info.Length}" });
#pragma warning disable CA2000
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            stream.Position = start;
            return new(206, contentType ?? "application/octet-stream", ReadOnlyMemory<byte>.Empty,
                new BoundedReadStream(stream, length), length,
                new Dictionary<string, string> { ["Accept-Ranges"] = "bytes", ["Content-Range"] = $"bytes {start}-{start + length - 1}/{info.Length}" });
        }
        catch { stream.Dispose(); throw; }
    }

    private sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => remaining;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            remaining -= read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => remaining <= 0 ? ValueTask.FromResult(0) : ReadBoundedAsync(buffer, cancellationToken);
        private async ValueTask<int> ReadBoundedAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], ct).ConfigureAwait(false);
            remaining -= read;
            return read;
        }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    internal static bool TryParseRange(string value, long length, out long start, out long count)
    {
        start = count = 0;
        if (length <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var spec = value[6..].Trim();
        if (spec.Length == 0 || spec.Contains(',', StringComparison.Ordinal)) return false;
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || spec.IndexOf('-', dash + 1) >= 0) return false;
        if (dash == 0)
        {
            if (!long.TryParse(spec[1..], out var suffix) || suffix <= 0) return false;
            count = Math.Min(suffix, length);
            start = length - count;
            return true;
        }
        if (!long.TryParse(spec[..dash], out var parsedStart) || parsedStart < 0 || parsedStart >= length) return false;
        if (dash == spec.Length - 1)
        {
            start = parsedStart;
            count = length - parsedStart;
            return true;
        }
        if (!long.TryParse(spec[(dash + 1)..], out var end) || end < parsedStart) return false;
        end = Math.Min(end, length - 1);
        start = parsedStart;
        count = end - parsedStart + 1;
        return true;
    }

    /// <summary>Creates a not-found response.</summary>
    public static RynSchemeResponse NotFound() =>
        new(404, "text/plain", "Not Found"u8.ToArray());
}
