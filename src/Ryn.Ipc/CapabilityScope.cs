namespace Ryn.Ipc;

public sealed class CapabilityScope
{
    public static CapabilityScope Empty { get; } = new([], []);

    public IReadOnlyList<string> AllowedPaths { get; }
    public IReadOnlyList<string> AllowedCommands { get; }

    public CapabilityScope(IReadOnlyList<string> allowedPaths, IReadOnlyList<string> allowedCommands)
    {
        AllowedPaths = allowedPaths;
        AllowedCommands = allowedCommands;
    }

    public bool HasPathRestrictions => AllowedPaths.Count > 0;
    public bool HasCommandRestrictions => AllowedCommands.Count > 0;
}
