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

    /// <summary>Fires when files are dropped onto the webview (names only, not full paths).</summary>
    public event EventHandler<FileDropEventArgs>? FileDrop;
}
