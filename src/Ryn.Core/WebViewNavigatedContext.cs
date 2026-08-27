namespace Ryn.Core;

/// <summary>Describes a webview navigation after the native engine commits it.</summary>
public readonly record struct WebViewNavigatedContext(Uri Url);
