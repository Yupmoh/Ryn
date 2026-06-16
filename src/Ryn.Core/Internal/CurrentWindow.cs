namespace Ryn.Core.Internal;

/// <summary>
/// Ambient "which window is this IPC call from" slot, backed by <see cref="AsyncLocal{T}"/>. Set in
/// <see cref="RynWebView.ExecuteCommandAsync"/> before the command dispatch hops to a worker thread, so it
/// flows into the dispatched <c>[RynCommand]</c> via the captured <see cref="System.Threading.ExecutionContext"/>.
/// Each command call has its own logical async slot, so concurrent calls from different windows never collide.
/// Read through <see cref="CurrentWindowAccessor"/>, which falls back to the main window when no ambient is set.
/// </summary>
internal static class CurrentWindow
{
    private static readonly AsyncLocal<IRynWindow?> Slot = new();

    internal static IRynWindow? Value
    {
        get => Slot.Value;
        set => Slot.Value = value;
    }
}
