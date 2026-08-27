namespace Ryn.Callbacks;

/// <summary>Marks a method for source-generated callback routing.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RynCallbackAttribute(RynCallbackKind kind) : Attribute
{
    /// <summary>The callback invoked by the annotated method.</summary>
    public RynCallbackKind Kind { get; } = kind;
}
