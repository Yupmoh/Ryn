using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Regression coverage for the IPC argument-binding and error-surfacing fixes (finding TST-07).
/// Drives the real generated router through <see cref="RynCommandDispatcher"/> so the exact exception
/// surfaced for malformed JSON, a type mismatch, a missing required argument, and a JSON null for a
/// non-nullable parameter is locked in. Also pins the observer/logger contract: a handler that throws
/// hands the FULL exception detail to <see cref="IIpcObserver.OnCommandFailed"/> (server-side logging)
/// while the dispatcher re-throws — the seam the webview layer relies on to show a generic message to
/// the page in Release yet keep full detail in the host log.
/// </summary>
public sealed class IpcErrorPathTests
{
    // ── Malformed JSON args → a JsonException (not a leaked internal/opaque type) ──

    [Fact]
    public async Task MalformedJson_ThrowsJsonException()
    {
        var dispatcher = BuildDispatcher();

        var act = () => dispatcher.DispatchAsync("add", Args("{not json")).AsTask();

        // JsonDocument.Parse rejects the body; the concrete type is JsonReaderException, a JsonException.
        await act.Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    // ── Type mismatch (string supplied for an int param) → STJ InvalidOperationException ──
    // The generated reader calls __prop.GetInt32() directly; a JSON string surfaces STJ's own
    // InvalidOperationException. This pins the CURRENT contract: only missing/null are reshaped into a
    // RynIpcArgumentException, a type mismatch is not (see CONCERNS in the task report).

    [Fact]
    public async Task TypeMismatch_ThrowsInvalidOperationException()
    {
        var dispatcher = BuildDispatcher();

        var act = () => dispatcher.DispatchAsync("add", Args("{\"a\":\"x\",\"b\":3}")).AsTask();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Missing required argument → descriptive RynIpcArgumentException naming command + param ──

    [Fact]
    public async Task MissingRequiredArg_ThrowsDescriptiveArgumentException()
    {
        var dispatcher = BuildDispatcher();

        var act = () => dispatcher.DispatchAsync("add", Args("{\"a\":5}")).AsTask();

        (await act.Should().ThrowAsync<RynIpcArgumentException>()
            .WithMessage("*add*b*missing*"))
            .Which.Should().Match<RynIpcArgumentException>(e => e.Command == "add" && e.Parameter == "b");
    }

    // ── JSON null for a non-nullable reference parameter → descriptive RynIpcArgumentException ──

    [Fact]
    public async Task NullForNonNullableParam_ThrowsDescriptiveArgumentException()
    {
        var dispatcher = BuildDispatcher();

        var act = () => dispatcher.DispatchAsync("echoStrict", Args("{\"value\":null}")).AsTask();

        (await act.Should().ThrowAsync<RynIpcArgumentException>()
            .WithMessage("*echoStrict*value*must not be null*"))
            .Which.Should().Match<RynIpcArgumentException>(
                e => e.Command == "echoStrict" && e.Parameter == "value");
    }

    // ── A descriptive argument exception leaks NO internal stack/path detail ──
    // The RynIpcArgumentException message is built from command + parameter + a fixed reason only; it
    // must not contain a file path, a type's full namespace, or a stack frame.

    [Fact]
    public async Task ArgumentException_MessageLeaksNoInternalDetail()
    {
        var dispatcher = BuildDispatcher();

        var ex = (await dispatcher.Invoking(d => d.DispatchAsync("add", Args("{\"a\":5}")).AsTask())
            .Should().ThrowAsync<RynIpcArgumentException>()).Which;

        ex.Message.Should().NotContainAny(
            ".cs", "src/", "src\\", "Ryn.Ipc.", "   at ", "StackTrace", AppContext.BaseDirectory);
    }

    // ── A throwing handler: dispatcher re-throws AND hands full detail to the observer (TST-07) ──
    // The "generic message to the page in Release / full detail in the host log" split lives in the
    // webview layer (Ryn.Core). What Ryn.Ipc guarantees — and what we lock in here — is that the
    // dispatcher surfaces the COMPLETE exception (type + message) to IIpcObserver.OnCommandFailed so a
    // logger downstream can record it, while still re-throwing for the caller.

    [Fact]
    public async Task ThrowingHandler_ReThrows_AndObserverReceivesFullDetail()
    {
        var observer = new RecordingObserver();
        var dispatcher = BuildDispatcher(observer);

        var act = () => dispatcher.DispatchAsync("boom", ReadOnlyMemory<byte>.Empty).AsTask();

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("detailed handler failure*")).Which;

        // The same exception (full type + message) reached the observer for server-side logging.
        observer.Failures.Should().ContainSingle();
        var (command, failure) = observer.Failures[0];
        command.Should().Be("boom");
        failure.Should().BeSameAs(thrown);
        failure.Message.Should().Be("detailed handler failure (secret internal context)");
    }

    [Fact]
    public async Task ThrowingHandler_ObserverNotNotifiedCompleted()
    {
        var observer = new RecordingObserver();
        var dispatcher = BuildDispatcher(observer);

        await dispatcher.Invoking(d => d.DispatchAsync("boom", ReadOnlyMemory<byte>.Empty).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        observer.Completions.Should().BeEmpty();
        observer.Failures.Should().ContainSingle();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static RynCommandDispatcher BuildDispatcher(IIpcObserver? observer = null)
    {
        var services = new ServiceCollection()
            .AddE2ETestCommands()
            .AddE2EMathCommands()
            .AddErrorPathCommands()
            .BuildServiceProvider();

        var routers = services.GetServices<ICommandRouter>().ToArray();
        return new RynCommandDispatcher(routers, services, RynCapabilities.AllowAll(), observer);
    }

    private static ReadOnlyMemory<byte> Args(string json) => Encoding.UTF8.GetBytes(json);

    private sealed class RecordingObserver : IIpcObserver
    {
        public List<(string Command, Exception Exception)> Failures { get; } = [];
        public List<string> Completions { get; } = [];

        public void OnCommandStarted(string command) { }
        public void OnCommandCompleted(string command, long elapsedMs) => Completions.Add(command);
        public void OnCommandFailed(string command, long elapsedMs, Exception exception) =>
            Failures.Add((command, exception));
        public void OnCommandDenied(string command) { }
    }
}

// ── Throwing-handler fixture (the generator emits a router for this) ──────
#pragma warning disable CA1515 // public so the generated router can reference it
public sealed class ErrorPathCommands
{
    // A handler whose exception carries internal context that must NOT leak to the page in Release but
    // MUST reach the observer/logger verbatim.
    [RynCommand("boom")]
    public static string Boom() =>
        throw new InvalidOperationException("detailed handler failure (secret internal context)");
}
#pragma warning restore CA1515
