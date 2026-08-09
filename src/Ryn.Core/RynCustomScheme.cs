namespace Ryn.Core;

/// <summary>Associates a custom URL scheme with its request handler.</summary>
public sealed record RynCustomScheme(
    string Scheme,
    Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> Handler);
