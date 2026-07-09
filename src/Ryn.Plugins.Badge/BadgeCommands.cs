using Ryn.Ipc;

namespace Ryn.Plugins.Badge;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class BadgeCommands
#pragma warning restore CA1812
{
    private readonly BadgeService _service;

    public BadgeCommands(BadgeService service) => _service = service;

    [RynCommand("badge.set")]
    public void Set(string label) => _service.Set(label);

    [RynCommand("badge.setCount")]
    public void SetCount(int count) => _service.SetCount(count);

    [RynCommand("badge.clear")]
    public void Clear() => _service.Clear();
}
