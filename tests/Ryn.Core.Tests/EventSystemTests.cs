using FluentAssertions;
using NSubstitute;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class EventSystemTests
{
    // --- EscapeForJs tests (internal, visible via InternalsVisibleTo) ---

    [Fact]
    public void EscapeForJs_PlainString_Unchanged()
    {
        RynWebView.EscapeForJs("hello world").Should().Be("hello world");
    }

    [Fact]
    public void EscapeForJs_Backslash_Escaped()
    {
        RynWebView.EscapeForJs(@"path\to\file").Should().Be(@"path\\to\\file");
    }

    [Fact]
    public void EscapeForJs_SingleQuote_Escaped()
    {
        RynWebView.EscapeForJs("it's").Should().Be("it\\'s");
    }

    [Fact]
    public void EscapeForJs_Newline_Escaped()
    {
        RynWebView.EscapeForJs("line1\nline2").Should().Be("line1\\nline2");
    }

    [Fact]
    public void EscapeForJs_CarriageReturn_Escaped()
    {
        RynWebView.EscapeForJs("line1\rline2").Should().Be("line1\\rline2");
    }

    [Fact]
    public void EscapeForJs_CrLf_Escaped()
    {
        RynWebView.EscapeForJs("line1\r\nline2").Should().Be("line1\\r\\nline2");
    }

    [Fact]
    public void EscapeForJs_NullByte_Escaped()
    {
        RynWebView.EscapeForJs("before\0after").Should().Be("before\\0after");
    }

    [Fact]
    public void EscapeForJs_LineSeparatorU2028_Escaped()
    {
        // U+2028 LINE SEPARATOR is a JS line terminator; must be escaped
        RynWebView.EscapeForJs("before\u2028after").Should().Be("before\\u2028after");
    }

    [Fact]
    public void EscapeForJs_ParagraphSeparatorU2029_Escaped()
    {
        // U+2029 PARAGRAPH SEPARATOR is a JS line terminator; must be escaped
        RynWebView.EscapeForJs("before\u2029after").Should().Be("before\\u2029after");
    }

    [Fact]
    public void EscapeForJs_EmptyString_ReturnsEmpty()
    {
        RynWebView.EscapeForJs("").Should().BeEmpty();
    }

    [Fact]
    public void EscapeForJs_BackslashBeforeQuote_EscapesBoth()
    {
        // Backslash must be escaped first to avoid double-escape issues
        // Input: \'  -> Should become \\\'  (escaped backslash + escaped quote)
        RynWebView.EscapeForJs("\\'").Should().Be("\\\\\\'");
    }

    [Fact]
    public void EscapeForJs_AllSpecialChars_AllEscaped()
    {
        var input = "a\\b'c\nd\re\0f\u2028g\u2029h";
        var expected = "a\\\\b\\'c\\nd\\re\\0f\\u2028g\\u2029h";
        RynWebView.EscapeForJs(input).Should().Be(expected);
    }

    // --- EmitEvent interface contract tests ---

    [Fact]
    public void EmitEvent_Interface_AcceptsValidCall()
    {
        // Verify IRynWebView.EmitEvent can be called with valid arguments
        var mock = Substitute.For<IRynWebView>();

        mock.EmitEvent("test-event", "{\"key\":\"value\"}");

        mock.Received(1).EmitEvent("test-event", "{\"key\":\"value\"}");
    }

    [Fact]
    public void EmitEvent_Interface_HasCorrectSignature()
    {
        // Verify the interface declares the string EmitEvent overload with the expected parameters.
        // (There is also a generic EmitEvent<T> overload, so select by parameter types.)
        var method = typeof(IRynWebView).GetMethod("EmitEvent", [typeof(string), typeof(string)]);

        method.Should().NotBeNull();
        var parameters = method!.GetParameters();
        parameters.Should().HaveCount(2);
        parameters[0].Name.Should().Be("eventName");
        parameters[0].ParameterType.Should().Be<string>();
        parameters[1].Name.Should().Be("jsonData");
        parameters[1].ParameterType.Should().Be<string>();
    }

    // --- JS bridge script structure tests ---

    [Fact]
    public void BridgeScript_ContainsEventOnFunction()
    {
        var script = RynWebView.GetBridgeScriptText();

        script.Should().Contain("ryn.on = function(event, callback)");
    }

    [Fact]
    public void BridgeScript_ContainsEventOffFunction()
    {
        var script = RynWebView.GetBridgeScriptText();

        script.Should().Contain("ryn.off = function(event, callback)");
    }

    [Fact]
    public void BridgeScript_ContainsEmitFunction()
    {
        var script = RynWebView.GetBridgeScriptText();

        script.Should().Contain("ryn._emit = function(event, data)");
    }

    [Fact]
    public void BridgeScript_OnRegistersListenerInArray()
    {
        var script = RynWebView.GetBridgeScriptText();

        // on() should push callback into the listeners array for that event
        script.Should().Contain("listeners[event].push(callback)");
    }

    [Fact]
    public void BridgeScript_OffRemovesListenerBySplice()
    {
        var script = RynWebView.GetBridgeScriptText();

        // off() should find the callback via indexOf and splice it out
        script.Should().Contain("list.indexOf(callback)");
        script.Should().Contain("list.splice(idx, 1)");
    }

    [Fact]
    public void BridgeScript_OffGuardsAgainstMissingEvent()
    {
        var script = RynWebView.GetBridgeScriptText();

        // off() should bail out if listeners[event] is undefined
        script.Should().Contain("if (!list) return");
    }

    [Fact]
    public void BridgeScript_OffGuardsAgainstMissingCallback()
    {
        var script = RynWebView.GetBridgeScriptText();

        // off() should only splice if indexOf returned >= 0
        script.Should().Contain("if (idx >= 0)");
    }

    [Fact]
    public void BridgeScript_EmitGuardsAgainstNoListeners()
    {
        var script = RynWebView.GetBridgeScriptText();

        // _emit should return early if no listeners registered for event
        // The pattern appears twice (off and _emit both have it)
        var occurrences = CountOccurrences(script, "if (!list) return");
        occurrences.Should().BeGreaterThanOrEqualTo(2,
            "both off() and _emit() should guard against missing listener list");
    }

    [Fact]
    public void BridgeScript_EmitCatchesCallbackErrors()
    {
        var script = RynWebView.GetBridgeScriptText();

        // _emit should wrap callback invocations in try/catch
        script.Should().Contain("try { list[i](data); }");
        script.Should().Contain("catch(e) { console.error('Ryn event error:', e); }");
    }

    [Fact]
    public void BridgeScript_ListenersIsolatedPerEvent()
    {
        var script = RynWebView.GetBridgeScriptText();

        // on() creates a new array per event key if one doesn't exist
        script.Should().Contain("if (!listeners[event]) listeners[event] = [];");
    }

    [Fact]
    public void BridgeScript_IsWrappedInIIFE()
    {
        var script = RynWebView.GetBridgeScriptText();

        // Script should be an immediately-invoked function expression
        script.Should().StartWith("(function(){");
        script.TrimEnd().Should().EndWith("})();");
    }

    [Fact]
    public void BridgeScript_InvokeFunction_Exists()
    {
        var script = RynWebView.GetBridgeScriptText();

        // Sanity check: the IPC invoke function should also be present
        script.Should().Contain("ryn.invoke = function(command, args)");
    }

    [Fact]
    public void BridgeScript_ListenersDeclaredAsObject()
    {
        var script = RynWebView.GetBridgeScriptText();

        // listeners should be a plain object (not an array)
        script.Should().Contain("var listeners = {};");
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
