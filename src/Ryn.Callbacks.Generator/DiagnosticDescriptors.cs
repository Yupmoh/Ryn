using Microsoft.CodeAnalysis;

namespace Ryn.Callbacks.Generator;

internal static class DiagnosticDescriptors
{
    private const string Category = "Ryn.Callbacks";

    public static readonly DiagnosticDescriptor InvalidContainingType = new(
        id: "RYNCB001",
        title: "Invalid callback containing type",
        messageFormat: "Containing type '{0}' is not supported: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MethodMustBeAccessible = new(
        id: "RYNCB002",
        title: "Callback method must be accessible",
        messageFormat: "Callback method '{0}' must be public or internal",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericMethod = new(
        id: "RYNCB003",
        title: "Callback method cannot be generic",
        messageFormat: "Callback method '{0}' cannot declare type parameters",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AsyncMethod = new(
        id: "RYNCB004",
        title: "Callback method must be synchronous",
        messageFormat: "Callback method '{0}' cannot be async; navigation callbacks run synchronously",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidParameter = new(
        id: "RYNCB005",
        title: "Invalid callback parameter",
        messageFormat: "{0} callback method '{1}' must have exactly one non-ref parameter of type '{2}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidReturnType = new(
        id: "RYNCB006",
        title: "Invalid callback return type",
        messageFormat: "{0} callback method '{1}' must return '{2}' synchronously",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateCallbackKind = new(
        id: "RYNCB007",
        title: "Duplicate callback kind",
        messageFormat: "Containing type '{0}' declares more than one '{1}' callback",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedCallbackKind = new(
        id: "RYNCB008",
        title: "Unsupported callback kind",
        messageFormat: "Callback method '{0}' uses unsupported RynCallbackKind value '{1}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
