using System.Text.Json.Serialization;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Options for <c>webviewPane.open</c>. Bounds are top-left CSS pixels relative to the window content
/// area on every platform (the rect from <c>getBoundingClientRect()</c> places the pane at that visual
/// position; the macOS bottom-left flip happens internally). Bounds are not re-derived on window resize —
/// re-send them from JS on layout changes.
/// </summary>
public sealed class PaneOpenRequest
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 300;

    /// <summary>Initial URL. Omit to start on a blank page.</summary>
    // CA1056: pane URLs are URL-bar strings passed to the engine as-is (see WebViewPaneService remarks).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
        Justification = "Pane URLs are engine-bound URL-bar strings, not validated URIs.")]
    public string? Url { get; set; }

    /// <summary>
    /// Directory for this pane's cookies/storage, enabling persistent per-pane sessions. Panes sharing a
    /// path share a session; omit for the engine default. Created if missing.
    /// </summary>
    public string? StoragePath { get; set; }

    /// <summary>Enable the engine's DevTools for this pane.</summary>
    public bool DevTools { get; set; }

    /// <summary>
    /// Custom user agent for this pane, applied before the first navigation. Omit for the engine default.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>Initial zoom factor (1.0 = 100%). Clamped to 0.25–5.0.</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>
    /// Background color painted from creation until the page's first paint (and behind transparent pages):
    /// <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c> or <c>rgba()</c>. Omit for the engine
    /// default (white). Set it to your UI's surface color so a pane never flashes a white rectangle.
    /// </summary>
    public string? Background { get; set; }
}

internal sealed record PaneNavigatedEvent(int Id, string Url);

internal sealed record PaneTitleChangedEvent(int Id, string Title);

/// <summary>State is "started" or "finished".</summary>
internal sealed record PaneLoadStateEvent(int Id, string State);

internal sealed record PaneDomReadyEvent(int Id);

/// <summary>DataUrl is a base64 <c>data:</c> URL of the favicon image, ready for an <c>&lt;img src&gt;</c>.</summary>
internal sealed record PaneFaviconEvent(int Id, string DataUrl);

internal sealed record PaneClosedEvent(int Id);

/// <summary>
/// Kinds: microphone, camera, screenShare, mouseLock, deviceInfo, geolocation, clipboard,
/// notifications, unknown. A getUserMedia call for camera+mic carries both kinds in one request.
/// </summary>
internal sealed record PanePermissionEvent(int Id, long RequestId, string[] Kinds, string Url);

/// <summary>ActiveIndex is 0-based; -1 when there are no matches (or the session was stopped).</summary>
public sealed record PaneFindResult(int Matches, int ActiveIndex);

/// <summary>
/// Reason strings — macOS: webContentProcessTerminated; Windows: browserProcessExited,
/// renderProcessExited, renderProcessUnresponsive, and friends; Linux: crashed,
/// exceededMemoryLimit, terminatedByApi.
/// </summary>
internal sealed record PaneProcessTerminatedEvent(int Id, string Reason);

internal sealed record PaneDownloadRequestedEvent(int Id, long DownloadId, string Url, string SuggestedName);

/// <summary>TotalBytes is 0 when the engine does not report a content length. Windows only (real progress).</summary>
internal sealed record PaneDownloadProgressEvent(int Id, long DownloadId, long ReceivedBytes, long TotalBytes);

internal sealed record PaneDownloadCompletedEvent(int Id, long DownloadId, string Path);

internal sealed record PaneDownloadFailedEvent(int Id, long DownloadId, string Error);

/// <summary>ParamsJson is the CDP event's parameter object, verbatim. Windows only.</summary>
internal sealed record PaneCdpEvent(int Id, string Event, string ParamsJson);

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(PaneOpenRequest))]
[JsonSerializable(typeof(PaneNavigatedEvent))]
[JsonSerializable(typeof(PaneTitleChangedEvent))]
[JsonSerializable(typeof(PaneLoadStateEvent))]
[JsonSerializable(typeof(PaneDomReadyEvent))]
[JsonSerializable(typeof(PaneFaviconEvent))]
[JsonSerializable(typeof(PaneClosedEvent))]
[JsonSerializable(typeof(PanePermissionEvent))]
[JsonSerializable(typeof(PaneFindResult))]
[JsonSerializable(typeof(PaneProcessTerminatedEvent))]
[JsonSerializable(typeof(PaneDownloadRequestedEvent))]
[JsonSerializable(typeof(PaneDownloadProgressEvent))]
[JsonSerializable(typeof(PaneDownloadCompletedEvent))]
[JsonSerializable(typeof(PaneDownloadFailedEvent))]
[JsonSerializable(typeof(PaneCdpEvent))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WebViewPaneJsonContext : JsonSerializerContext { }
