namespace Ryn.Plugins.Badge;

internal interface IBadgeBackend : IDisposable
{
    /// <summary>Shows <paramref name="label"/> on the app icon; <c>null</c> or empty clears the badge.</summary>
    public void SetLabel(string? label);
}
