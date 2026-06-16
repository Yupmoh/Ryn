namespace Ryn.Core.Internal;

/// <summary>
/// Single source of truth for the IPC wire-protocol constants (PAP-19): the route prefixes, the per-launch
/// token header, and the built-in app origin. These literals were previously hand-duplicated across
/// <see cref="LocalWebServer"/> (HTTP routing + auth) and <see cref="RynWebView"/> (the <c>ryn://</c> scheme
/// handler), so a typo in one site could silently break the IPC contract while the other kept working.
/// </summary>
/// <remarks>
/// The JS bridge built in <see cref="RynWebView"/> hard-codes the same prefixes and header name inside its
/// script literal (an interpolated-string template cannot pull these constants in cleanly without breaking
/// the <c>{{ }}</c> escaping, and the bridge is the wire's other end). That copy is the only remaining
/// duplicate and is kept in sync by hand; if any value here changes, update the bridge in
/// <see cref="RynWebView"/> to match. The bridge carries a comment pointing back to this type.
/// </remarks>
internal static class IpcProtocol
{
    /// <summary>Common prefix for every IPC route. A request path under this prefix is privileged (token-gated).</summary>
    internal const string IpcPrefix = "/ipc/";

    /// <summary>Route prefix for an IPC command invocation: <c>/ipc/cmd/{id}/{command}</c>.</summary>
    internal const string IpcCommandPrefix = "/ipc/cmd/";

    /// <summary>Route prefix for a host-initiated-eval response: <c>/ipc/eval/{id}/{ok}</c> (the scheme handler appends <c>/{nonce}</c>).</summary>
    internal const string IpcEvalPrefix = "/ipc/eval/";

    /// <summary>Request header carrying the per-launch IPC token (constant-time matched on the host).</summary>
    internal const string TokenHeader = "X-Ryn-Token";

    /// <summary>The built-in app origin served over the <c>ryn://</c> scheme; the default allowed/same-origin value.</summary>
    internal const string AppOrigin = "ryn://app";
}
