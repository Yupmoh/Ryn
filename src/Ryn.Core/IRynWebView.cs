namespace Ryn.Core;

/// <summary>Provides access to the embedded webview for navigation, script execution, and IPC.</summary>
public interface IRynWebView
{
    /// <summary>Navigates the webview to the specified URL.</summary>
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>Loads raw HTML content into the webview.</summary>
    public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default);

    /// <summary>Evaluates a JavaScript expression and returns the result as a string.</summary>
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default);

    /// <summary>Injects a JavaScript script that runs on every page load.</summary>
    public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default);

    /// <summary>Registers a handler for a custom URL scheme (e.g. ryn://).</summary>
    public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler);

    /// <summary>
    /// Emits a named event to the JavaScript side via <c>window.__ryn</c>. <paramref name="jsonData"/> must
    /// be a valid JSON value; it is validated and canonicalized to prevent script injection. Prefer the
    /// strongly-typed <see cref="EmitEvent{T}"/> overload.
    /// </summary>
    public void EmitEvent(string eventName, string jsonData);

    /// <summary>Emits a strongly-typed event payload serialized via a source-generated JsonTypeInfo (AOT- and injection-safe).</summary>
    public void EmitEvent<T>(string eventName, T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo);

    /// <summary>
    /// Creates a shared-memory buffer that can be posted to the page and read there as an
    /// <c>ArrayBuffer</c> without serialization. Only supported on Windows (WebView2); other platforms throw
    /// <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public ValueTask<RynSharedBuffer> CreateSharedBufferAsync(ulong size, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts <paramref name="buffer"/> to the page; script receives it through <c>chrome.webview</c>'s
    /// <c>sharedbufferreceived</c> event. Only supported on Windows (WebView2). If
    /// <paramref name="additionalDataAsJson"/> is non-empty it must be valid JSON and is surfaced to script
    /// as <c>event.additionalData</c> (a convenient place for per-frame metadata such as row counts).
    /// </summary>
    public ValueTask PostSharedBufferToScriptAsync(RynSharedBuffer buffer, RynSharedBufferAccess access, string? additionalDataAsJson = null, CancellationToken cancellationToken = default);

    /// <summary>Fires when files are dropped onto the webview (names only, not full paths).</summary>
    public event EventHandler<FileDropEventArgs>? FileDrop;
}
