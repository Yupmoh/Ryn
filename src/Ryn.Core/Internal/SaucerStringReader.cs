using System.Buffers;
using System.Text;
using Ryn.Interop;

namespace Ryn.Core.Internal;

internal static unsafe class SaucerStringReader
{
    private const int StackAllocThreshold = 512;

    internal static string ReadUrlPath(saucer_url* url)
    {
        nuint size = 0;
        Saucer.saucer_url_path(url, null, &size);
        return ReadWithSize((sbyte* buf, nuint* sz) => Saucer.saucer_url_path(url, buf, sz), size);
    }

    internal static string ReadUrlString(saucer_url* url)
    {
        nuint size = 0;
        Saucer.saucer_url_string(url, null, &size);
        return ReadWithSize((sbyte* buf, nuint* sz) => Saucer.saucer_url_string(url, buf, sz), size);
    }

    internal static string ReadUrlScheme(saucer_url* url)
    {
        nuint size = 0;
        Saucer.saucer_url_scheme(url, null, &size);
        return ReadWithSize((sbyte* buf, nuint* sz) => Saucer.saucer_url_scheme(url, buf, sz), size);
    }

    internal static string ReadWindowTitle(saucer_window* window)
    {
        nuint size = 0;
        Saucer.saucer_window_title(window, null, &size);
        return ReadWithSize((sbyte* buf, nuint* sz) => Saucer.saucer_window_title(window, buf, sz), size);
    }

    internal static string ReadRequestMethod(saucer_scheme_request* request)
    {
        nuint size = 0;
        Saucer.saucer_scheme_request_method(request, null, &size);
        return ReadWithSize((sbyte* buf, nuint* sz) => Saucer.saucer_scheme_request_method(request, buf, sz), size);
    }

    internal static string ReadWebViewPageTitle(saucer_webview* webview)
    {
        nuint size = 0;
        Saucer.saucer_webview_page_title(webview, null, &size);
        return ReadWithSize((sbyte* buf, nuint* sz) => Saucer.saucer_webview_page_title(webview, buf, sz), size);
    }

    private static string ReadWithSize(SaucerStringGetter getter, nuint size)
    {
        if (size == 0) return string.Empty;

        if ((int)size <= StackAllocThreshold)
        {
            var buffer = stackalloc byte[(int)size];
            getter((sbyte*)buffer, &size);
            return Encoding.UTF8.GetString(buffer, (int)size);
        }

        var pooled = ArrayPool<byte>.Shared.Rent((int)size);
        try
        {
            fixed (byte* ptr = pooled)
            {
                getter((sbyte*)ptr, &size);
                return Encoding.UTF8.GetString(ptr, (int)size);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    private unsafe delegate void SaucerStringGetter(sbyte* buffer, nuint* size);
}
