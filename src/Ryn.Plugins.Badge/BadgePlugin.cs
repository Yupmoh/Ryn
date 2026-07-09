using Ryn.Core;

namespace Ryn.Plugins.Badge;

#pragma warning disable CA1812 // Instantiated by DI
internal sealed class BadgePlugin : IRynPlugin
#pragma warning restore CA1812
{
    public string Name => "badge";

    public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
