namespace Ryn.Core;

public readonly record struct RynSchemeRequest(
    Uri Url,
    string Method,
    ReadOnlyMemory<byte> Body);
