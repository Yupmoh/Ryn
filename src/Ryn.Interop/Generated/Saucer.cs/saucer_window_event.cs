namespace Ryn.Interop
{
    [NativeTypeName("unsigned int")]
    public enum saucer_window_event : uint
    {
        SAUCER_WINDOW_EVENT_DECORATED,
        SAUCER_WINDOW_EVENT_MAXIMIZE,
        SAUCER_WINDOW_EVENT_MINIMIZE,
        SAUCER_WINDOW_EVENT_CLOSED,
        SAUCER_WINDOW_EVENT_RESIZE,
        SAUCER_WINDOW_EVENT_FOCUS,
        SAUCER_WINDOW_EVENT_CLOSE,
    }
}
