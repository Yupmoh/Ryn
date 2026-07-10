using System.Text.Json;
using FluentAssertions;
using Ryn.Plugins.Tray;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class TrayMenuItemJsonTests
{
    [Fact]
    public void Enabled_DefaultsToTrue_WhenAbsentFromJson()
    {
        // Same latent bug as MenuBarItem: a `= true` initializer is dropped by some STJ source-generator
        // versions for members absent from the JSON, disabling every tray item. The default lives in the
        // getter now, so an omitted `enabled` stays true.
        var absent = JsonSerializer.Deserialize(
            """[{"id":"x","label":"X"}]""", TrayJsonContext.Default.TrayMenuItemArray)!;
        absent[0].Enabled.Should().BeTrue();

        var disabled = JsonSerializer.Deserialize(
            """[{"id":"x","label":"X","enabled":false}]""", TrayJsonContext.Default.TrayMenuItemArray)!;
        disabled[0].Enabled.Should().BeFalse();

        new TrayMenuItem { Id = "x", Label = "X", Enabled = false }.Enabled.Should().BeFalse();
        new TrayMenuItem { Id = "x", Label = "X" }.Enabled.Should().BeTrue();
    }
}
