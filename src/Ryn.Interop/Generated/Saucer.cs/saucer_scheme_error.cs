namespace Ryn.Interop
{
    [NativeTypeName("int16_t")]
    public enum saucer_scheme_error : short
    {
        SAUCER_SCHEME_ERROR_NOT_FOUND = 404,
        SAUCER_SCHEME_ERROR_INVALID = 400,
        SAUCER_SCHEME_ERROR_DENIED = 401,
        SAUCER_SCHEME_ERROR_FAILED = -1,
    }
}
