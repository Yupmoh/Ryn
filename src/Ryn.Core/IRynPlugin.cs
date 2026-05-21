namespace Ryn.Core;

public interface IRynPlugin
{
    public string Name { get; }
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
