using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

/// <summary>
/// Regression tests for <see cref="WindowStatePersistence"/> (ARC-05): a Save→Load round-trip preserves
/// X/Y/Width/Height/IsMaximized, and a missing or corrupt state file degrades to "no saved state" (null)
/// rather than throwing into window startup/shutdown.
/// Each test uses a unique application id so its on-disk file is isolated, and cleans up after itself.
/// </summary>
public sealed class WindowStatePersistenceTests : IDisposable
{
    private readonly string _appId = $"ryn-test-{Guid.NewGuid():N}";
    private readonly string _stateDir;

    public WindowStatePersistenceTests()
    {
        _stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ryn", _appId);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stateDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var persistence = new WindowStatePersistence(_appId);
        var saved = new WindowStateData { X = 120, Y = 240, Width = 1024, Height = 768, IsMaximized = true };

        persistence.Save(saved);
        var loaded = persistence.Load();

        loaded.Should().NotBeNull();
        loaded!.X.Should().Be(120);
        loaded.Y.Should().Be(240);
        loaded.Width.Should().Be(1024);
        loaded.Height.Should().Be(768);
        loaded.IsMaximized.Should().BeTrue();
    }

    [Fact]
    public void SaveThenLoad_PreservesIsMaximizedFalse()
    {
        var persistence = new WindowStatePersistence(_appId);
        persistence.Save(new WindowStateData { X = 1, Y = 2, Width = 800, Height = 600, IsMaximized = false });

        var loaded = persistence.Load();

        loaded.Should().NotBeNull();
        loaded!.IsMaximized.Should().BeFalse();
    }

    [Fact]
    public void Save_OverwritesPreviousState()
    {
        var persistence = new WindowStatePersistence(_appId);
        persistence.Save(new WindowStateData { X = 1, Y = 1, Width = 100, Height = 100 });
        persistence.Save(new WindowStateData { X = 9, Y = 9, Width = 900, Height = 900, IsMaximized = true });

        var loaded = persistence.Load();

        loaded.Should().NotBeNull();
        loaded!.X.Should().Be(9);
        loaded.Width.Should().Be(900);
        loaded.IsMaximized.Should().BeTrue();
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsNull_WithoutThrowing()
    {
        var persistence = new WindowStatePersistence(_appId);

        WindowStateData? loaded = null;
        var act = () => loaded = persistence.Load();

        act.Should().NotThrow();
        loaded.Should().BeNull();
    }

    [Fact]
    public void Load_WhenFileIsCorruptJson_ReturnsNull_WithoutThrowing()
    {
        // Write garbage to the exact path Load reads so the deserializer throws JsonException internally; the
        // persistence layer must swallow it and return null rather than crashing window startup.
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(Path.Combine(_stateDir, "window-state.json"), "{ this is not valid json ]]]");

        var persistence = new WindowStatePersistence(_appId);

        WindowStateData? loaded = null;
        var act = () => loaded = persistence.Load();

        act.Should().NotThrow();
        loaded.Should().BeNull();
    }

    [Fact]
    public void Load_WhenFileIsEmpty_ReturnsNull_WithoutThrowing()
    {
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(Path.Combine(_stateDir, "window-state.json"), "");

        var persistence = new WindowStatePersistence(_appId);

        WindowStateData? loaded = null;
        var act = () => loaded = persistence.Load();

        act.Should().NotThrow();
        loaded.Should().BeNull();
    }
}
