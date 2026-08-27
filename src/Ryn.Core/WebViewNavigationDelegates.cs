namespace Ryn.Core;

/// <summary>
/// Handles a webview navigation before it is committed. Registered by Ryn.Callbacks via AddRynCallbacks().
/// </summary>
public delegate NavigationDecision WebViewNavigatingHandler(WebViewNavigatingContext context);

/// <summary>
/// Handles a webview navigation after it is committed. Registered by Ryn.Callbacks via AddRynCallbacks().
/// </summary>
public delegate void WebViewNavigatedHandler(WebViewNavigatedContext context);
