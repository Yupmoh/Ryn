using FluentAssertions;
using Ryn.Interop;
using Xunit;

namespace Ryn.Interop.Tests;

/// <summary>
/// Exercises the pure-managed surface of <see cref="NativeLibraryResolver"/>. Registering a resolver
/// only installs a callback; it does not load any native library (resolution fires lazily on the first
/// P/Invoke, which these tests never trigger), so every case here is deterministic and CI-safe.
/// Actual library loading is covered by Ryn.Integration.Tests.
/// </summary>
public sealed class NativeLibraryResolverTests
{
    [Fact]
    public void RegisterForAssembly_NullAssembly_ThrowsArgumentNullException()
    {
        var act = () => NativeLibraryResolver.RegisterForAssembly(null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("assembly");
    }

    [Fact]
    public void RegisterForAssembly_SameAssemblyTwice_IsIdempotent()
    {
        // Use this test assembly as the registration target: it is a real runtime assembly (required by
        // NativeLibrary.SetDllImportResolver) and nothing else in the suite registers a resolver on it.
        // The first call installs the resolver; the second must be swallowed by the resolver's internal
        // dedup set. Installing a resolver here is harmless: it only ever claims "saucer-bindings"/"ryn-pty"
        // and returns zero for everything else, and no P/Invoke in this assembly triggers it.
        var assembly = typeof(NativeLibraryResolverTests).Assembly;

        var first = () => NativeLibraryResolver.RegisterForAssembly(assembly);
        var second = () => NativeLibraryResolver.RegisterForAssembly(assembly);

        first.Should().NotThrow();
        // The runtime throws InvalidOperationException if a resolver is set twice for one assembly; the
        // dedup must prevent the second SetDllImportResolver call entirely for this to pass.
        second.Should().NotThrow();
    }

    [Fact]
    public void Register_IsIdempotentAndDoesNotThrow()
    {
        // Register() targets the Ryn.Interop assembly. It may already be registered by another test or
        // by the host; either way repeated calls must be safe and must not load anything.
        var act = () =>
        {
            NativeLibraryResolver.Register();
            NativeLibraryResolver.Register();
        };

        act.Should().NotThrow();
    }
}
