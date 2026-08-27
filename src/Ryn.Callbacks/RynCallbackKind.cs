namespace Ryn.Callbacks;

/// <summary>Callbacks that can be routed to source-generated handlers.</summary>
public enum RynCallbackKind
{
    /// <summary>Runs before a webview navigation and can block it.</summary>
    WebViewNavigating,

    /// <summary>Runs after a webview navigation completes.</summary>
    WebViewNavigated,
}
