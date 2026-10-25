using Foundation;
using Ryn.iOS.Internal;
using WebKit;

namespace Ryn.iOS;

public class RynUrlSchemeHandler : NSObject, IWKUrlSchemeHandler
{
    private readonly string _bundleRoot;

    public RynUrlSchemeHandler()
    {
        _bundleRoot = NSBundle.MainBundle.BundlePath;
    }

    public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        ArgumentNullException.ThrowIfNull(urlSchemeTask);
        var url = urlSchemeTask.Request.Url;
        if (url is null)
        {
            urlSchemeTask.DidFailWithError(new NSError(new NSString("RynError"), 400));
            return;
        }

        var path = url.Path?.TrimStart('/') ?? "";
        if (string.IsNullOrEmpty(path))
        {
            path = "index.html";
        }

        // 1. Try wwwroot/path
        var filePath = Path.Combine(_bundleRoot, "wwwroot", path);
        if (!File.Exists(filePath))
        {
            // 2. Try root bundle path
            filePath = Path.Combine(_bundleRoot, path);
        }

        if (File.Exists(filePath))
        {
            try
            {
                var bytes = File.ReadAllBytes(filePath);
                var mime = MimeTypeHelper.GetMimeType(Path.GetExtension(filePath));
                using var response = new NSUrlResponse(url, mime, (nint)bytes.Length, "utf-8");
                urlSchemeTask.DidReceiveResponse(response);
                urlSchemeTask.DidReceiveData(NSData.FromArray(bytes));
                urlSchemeTask.DidFinish();
            }
            catch (Exception)
            {
                urlSchemeTask.DidFailWithError(new NSError(new NSString("RynError"), 500));
            }
        }
        else
        {
            urlSchemeTask.DidFailWithError(new NSError(new NSString("RynError"), 404));
        }
    }

    public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
    }
}
