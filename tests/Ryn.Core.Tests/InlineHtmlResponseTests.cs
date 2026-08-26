using System.Text;
using FluentAssertions;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class InlineHtmlResponseTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/index.html")]
    public void InlineHtml_IsServedForDocumentPaths(string path)
    {
        const string html = "<h1>Ryn inline HTML</h1>";

        var response = RynWebView.TryGetInlineHtmlResponse(html, path);

        response.Should().NotBeNull();
        response!.MimeType.Should().Be("text/html");
        Encoding.UTF8.GetString(response.Body).Should().Be(html);
    }

    [Theory]
    [InlineData("/app.js")]
    [InlineData("/index.html/child")]
    public void InlineHtml_DoesNotShadowAssetPaths(string path)
    {
        RynWebView.TryGetInlineHtmlResponse("<h1>Ryn inline HTML</h1>", path).Should().BeNull();
    }

    [Fact]
    public void MissingInlineHtml_ReturnsNoResponse()
    {
        RynWebView.TryGetInlineHtmlResponse(null, "/index.html").Should().BeNull();
    }
}
