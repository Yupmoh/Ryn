namespace Ryn.Plugins.Tray;

public sealed class TrayMenuItem
{
    public required string Id { get; init; }
    public required string Label { get; init; }

    private readonly bool? _enabled;

    /// <summary>
    /// Serialization shim for <c>enabled</c>, nullable on purpose — see <c>MenuBarItem.EnabledRaw</c>: the STJ
    /// source generator sets an absent non-nullable <c>init</c> property to <c>false</c> rather than running a
    /// <c>= true</c> initializer, which would disable the item. Prefer <see cref="Enabled"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool? EnabledRaw { get => _enabled; init => _enabled = value; }

    /// <summary>Whether the item is enabled. Defaults to <c>true</c> when unspecified.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Enabled { get => _enabled ?? true; init => _enabled = value; }

    public bool Separator { get; init; }
}
