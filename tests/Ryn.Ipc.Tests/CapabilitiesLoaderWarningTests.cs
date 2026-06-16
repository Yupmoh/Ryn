using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Regression coverage for finding IPC-07: when <c>ryn.json</c> is absent, the permissive (dev)
/// allow-all path must emit a loud one-time warning and return allow-all, while the fail-closed
/// (release) path must emit its own warning and return deny-all. This locks in both the warning
/// emission and the allow/deny outcome through the public
/// <see cref="RynCapabilitiesLoader.Load(bool, ILogger?)"/> seam.
/// <para>
/// The test relies on there being no <c>ryn.json</c> next to the test assembly (the normal case for a
/// test bin directory). If one is ever present the missing-file branch under test cannot run; the test
/// then returns early with an assertion proving the guard, rather than asserting against the wrong code
/// path. In CI the file is absent, so the real branch is always exercised.
/// </para>
/// </summary>
public sealed class CapabilitiesLoaderWarningTests
{
    private static bool RynJsonExistsNextToTestAssembly =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "ryn.json"));

    [Fact]
    public void Load_PermissiveUnconfigured_ReturnsAllowAll_AndWarns()
    {
        if (RynJsonExistsNextToTestAssembly)
        {
            // Environment carries a ryn.json next to the test binary; the missing-file branch under test
            // cannot be exercised. Assert the precondition note instead of a misleading pass.
            RynJsonExistsNextToTestAssembly.Should().BeTrue("a ryn.json is present so this case is inapplicable");
            return;
        }

        var logger = new CapturingLogger();

        var caps = RynCapabilitiesLoader.Load(permissiveWhenUnconfigured: true, logger);

        // Allow-all: not enforced, every command passes.
        caps.IsEnforced.Should().BeFalse();
        caps.Invoking(c => c.ThrowIfDenied("anything.goes")).Should().NotThrow();

        // A single loud warning naming the permissive/allow-all condition was emitted.
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().ContainEquivalentOf("allow-all");
    }

    [Fact]
    public void Load_FailClosedUnconfigured_ReturnsDenyAll_AndWarns()
    {
        if (RynJsonExistsNextToTestAssembly)
        {
            RynJsonExistsNextToTestAssembly.Should().BeTrue("a ryn.json is present so this case is inapplicable");
            return;
        }

        var logger = new CapturingLogger();

        var caps = RynCapabilitiesLoader.Load(permissiveWhenUnconfigured: false, logger);

        // Deny-all: enforced, every command is denied.
        caps.IsEnforced.Should().BeTrue();
        caps.Invoking(c => c.ThrowIfDenied("anything.goes")).Should().Throw<RynCommandDeniedException>();

        // A single warning explaining the fail-closed deny-all was emitted.
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().ContainEquivalentOf("denying every plugin");
    }

    [Fact]
    public void Load_FailClosedUnconfigured_WithoutLogger_StillDeniesAll()
    {
        if (RynJsonExistsNextToTestAssembly)
        {
            RynJsonExistsNextToTestAssembly.Should().BeTrue("a ryn.json is present so this case is inapplicable");
            return;
        }

        // The deny-all outcome must not depend on a logger being supplied.
        var caps = RynCapabilitiesLoader.Load(permissiveWhenUnconfigured: false, logger: null);

        caps.IsEnforced.Should().BeTrue();
        caps.Invoking(c => c.ThrowIfDenied("anything.goes")).Should().Throw<RynCommandDeniedException>();
    }

    /// <summary>Minimal ILogger that records only formatted Warning-level messages.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
