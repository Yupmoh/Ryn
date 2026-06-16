using FluentAssertions;
using Ryn.Interop;
using Xunit;

namespace Ryn.Interop.Tests;

/// <summary>
/// Locks the ABI contract of the generated saucer enums: their underlying integer type and the
/// ordinal value of each member must match the C headers, because they are passed by value across the
/// native boundary. A regeneration that changed an underlying type (e.g. uint to int) or reordered
/// members would silently mis-marshal events/policies with no compile error. These are pure-managed
/// reflection/value assertions and require no native library.
/// </summary>
public sealed class SaucerEnumAbiTests
{
    [Fact]
    public void Policy_IsByteBacked_WithExpectedValues()
    {
        Enum.GetUnderlyingType(typeof(saucer_policy)).Should().Be<byte>();
        ((byte)saucer_policy.SAUCER_POLICY_ALLOW).Should().Be(0);
        ((byte)saucer_policy.SAUCER_POLICY_BLOCK).Should().Be(1);
    }

    [Fact]
    public void WindowEdge_IsByteBacked_WithBitFlagValues()
    {
        Enum.GetUnderlyingType(typeof(saucer_window_edge)).Should().Be<byte>();

        ((byte)saucer_window_edge.SAUCER_WINDOW_EDGE_TOP).Should().Be(1);
        ((byte)saucer_window_edge.SAUCER_WINDOW_EDGE_BOTTOM).Should().Be(2);
        ((byte)saucer_window_edge.SAUCER_WINDOW_EDGE_LEFT).Should().Be(4);
        ((byte)saucer_window_edge.SAUCER_WINDOW_EDGE_RIGHT).Should().Be(8);

        // Composite corners are bitwise ORs of the edges.
        saucer_window_edge.SAUCER_WINDOW_EDGE_BOTTOM_LEFT.Should()
            .Be(saucer_window_edge.SAUCER_WINDOW_EDGE_BOTTOM | saucer_window_edge.SAUCER_WINDOW_EDGE_LEFT);
        saucer_window_edge.SAUCER_WINDOW_EDGE_TOP_RIGHT.Should()
            .Be(saucer_window_edge.SAUCER_WINDOW_EDGE_TOP | saucer_window_edge.SAUCER_WINDOW_EDGE_RIGHT);
    }

    [Theory]
    [InlineData(typeof(saucer_status))]
    [InlineData(typeof(saucer_state))]
    [InlineData(typeof(saucer_window_event))]
    [InlineData(typeof(saucer_webview_event))]
    public void EventAndStateEnums_AreUIntBacked(Type enumType)
    {
        // These cross the boundary as `unsigned int`; the managed underlying type must stay uint.
        Enum.GetUnderlyingType(enumType).Should().Be<uint>();
    }

    [Fact]
    public void WindowEvent_MembersKeepTheirDeclaredOrdinals()
    {
        // saucer dispatches event callbacks keyed on these ordinals; reordering would route the wrong handler.
        ((uint)saucer_window_event.SAUCER_WINDOW_EVENT_DECORATED).Should().Be(0);
        ((uint)saucer_window_event.SAUCER_WINDOW_EVENT_MAXIMIZE).Should().Be(1);
        ((uint)saucer_window_event.SAUCER_WINDOW_EVENT_MINIMIZE).Should().Be(2);
        ((uint)saucer_window_event.SAUCER_WINDOW_EVENT_CLOSED).Should().Be(3);
        ((uint)saucer_window_event.SAUCER_WINDOW_EVENT_RESIZE).Should().Be(4);
        ((uint)saucer_window_event.SAUCER_WINDOW_EVENT_FOCUS).Should().Be(5);
        ((uint)saucer_window_event.SAUCER_WINDOW_EVENT_CLOSE).Should().Be(6);
    }

    [Fact]
    public void WebViewEvent_MembersKeepTheirDeclaredOrdinals()
    {
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_PERMISSION).Should().Be(0);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_FULLSCREEN).Should().Be(1);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_DOM_READY).Should().Be(2);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_NAVIGATED).Should().Be(3);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_NAVIGATE).Should().Be(4);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_MESSAGE).Should().Be(5);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_REQUEST).Should().Be(6);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_FAVICON).Should().Be(7);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_TITLE).Should().Be(8);
        ((uint)saucer_webview_event.SAUCER_WEBVIEW_EVENT_LOAD).Should().Be(9);
    }
}
