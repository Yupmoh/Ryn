namespace Ryn.Plugins.Badge.Backends;

// Linux (and anything else): no portable badge surface. Unity's launcher badge API is desktop-specific,
// so it is deliberately out of scope.
internal sealed class StubBadgeBackend : IBadgeBackend
{
    public void SetLabel(string? label) { }
    public void Dispose() { }
}
