namespace Ryn.Interop
{
    [NativeTypeName("uint8_t")]
    public enum saucer_permission_type : byte
    {
        SAUCER_PERMISSION_TYPE_UNKNOWN = 0,
        SAUCER_PERMISSION_TYPE_AUDIO_MEDIA = 1 << 0,
        SAUCER_PERMISSION_TYPE_VIDEO_MEDIA = 1 << 1,
        SAUCER_PERMISSION_TYPE_DESKTOP_MEDIA = 1 << 2,
        SAUCER_PERMISSION_TYPE_MOUSE_LOCK = 1 << 3,
        SAUCER_PERMISSION_TYPE_DEVICE_INFO = 1 << 4,
        SAUCER_PERMISSION_TYPE_LOCATION = 1 << 5,
        SAUCER_PERMISSION_TYPE_CLIPBOARD = 1 << 6,
        SAUCER_PERMISSION_TYPE_NOTIFICATION = 1 << 7,
    }
}
