namespace Ryn.Core;

/// <summary>Policy returned by a cancellable webview navigation callback.</summary>
public enum NavigationDecision
{
    /// <summary>Allows the navigation to continue.</summary>
    Allow,

    /// <summary>Blocks the navigation.</summary>
    Block,
}
