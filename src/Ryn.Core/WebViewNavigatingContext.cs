namespace Ryn.Core;

/// <summary>Describes a webview navigation before the native engine commits it.</summary>
public readonly record struct WebViewNavigatingContext(
    Uri Url,
    bool IsNewWindow,
    bool IsRedirect,
    bool IsUserInitiated);
