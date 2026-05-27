using Microsoft.CodeAnalysis;

namespace Ryn.Ipc.Generator;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor UnsupportedParameterType = new(
        id: "RYN001",
        title: "Unsupported parameter type",
        messageFormat: "Parameter '{0}' has unsupported type '{1}' (expected a primitive, JsonElement, nullable primitive, or primitive array)",
        category: "Ryn.Ipc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        id: "RYN002",
        title: "Unsupported return type",
        messageFormat: "Return type '{0}' is not supported (expected void, a primitive, nullable primitive, or primitive array)",
        category: "Ryn.Ipc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CancellationTokenMustBeLast = new(
        id: "RYN003",
        title: "CancellationToken must be the last parameter",
        messageFormat: "CancellationToken parameter '{0}' must be the last parameter",
        category: "Ryn.Ipc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateCommandName = new(
        id: "RYN004",
        title: "Duplicate command name",
        messageFormat: "Command name '{0}' is already used by another method in this class",
        category: "Ryn.Ipc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MethodMustBeAccessible = new(
        id: "RYN005",
        title: "Method must be public or internal",
        messageFormat: "Method '{0}' must be public or internal to be used as a command",
        category: "Ryn.Ipc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
