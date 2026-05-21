namespace Ryn.Core;

public interface IRynWebView
{
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default);
    public ValueTask NavigateToStringAsync(string html, CancellationToken cancellationToken = default);
    public ValueTask<string> EvaluateJavaScriptAsync(string script, CancellationToken cancellationToken = default);
    public ValueTask InjectScriptAsync(string script, CancellationToken cancellationToken = default);
    public void RegisterCustomScheme(string scheme, Func<RynSchemeRequest, ValueTask<RynSchemeResponse>> handler);
}
