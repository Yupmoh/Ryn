using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ryn.Ipc.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class RynCommandGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Ryn.Ipc.RynCommandAttribute";
    private const string JsonContextAttributeFullName = "Ryn.Ipc.RynJsonContextAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methods = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => ExtractCommandInfo(ctx, ct))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!.Value);

        var grouped = methods.Collect()
            .SelectMany(static (items, _) =>
            {
                var groups = new Dictionary<string, List<CommandInfo>>();
                foreach (var item in items)
                {
                    var key = item.ContainingTypeFullName;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<CommandInfo>();
                        groups[key] = list;
                    }
                    list.Add(item);
                }

                var result = ImmutableArray.CreateBuilder<CommandGroup>();
                foreach (var kvp in groups)
                {
                    result.Add(new CommandGroup(
                        kvp.Key,
                        kvp.Value.First().ContainingTypeName,
                        kvp.Value.First().Namespace,
                        kvp.Value.First().JsonContextTypeFullName,
                        kvp.Value.ToImmutableArray()));
                }
                return result.ToImmutable();
            });

        context.RegisterSourceOutput(grouped, static (ctx, group) =>
        {
            var source = Emitter.Emit(group, ctx);
            if (source is not null)
            {
                // Qualify the hint name with the full (namespace-prefixed) type name. Using only the simple
                // type name collides when two command classes share a name across namespaces — Roslyn
                // requires hint names to be unique per generator, so the second AddSource would throw.
                ctx.AddSource($"{HintName(group.TypeFullName)}Router.g.cs", source);
            }
        });

        // RYN006: detect the same command name declared across DIFFERENT classes. RYN004 only catches
        // duplicates within one class; across classes the first-registered router silently shadows the rest.
        var allCommands = methods.Collect();
        context.RegisterSourceOutput(allCommands, static (ctx, commands) =>
        {
            var byName = new Dictionary<string, List<CommandInfo>>(StringComparer.Ordinal);
            foreach (var cmd in commands)
            {
                if (!byName.TryGetValue(cmd.CommandName, out var list))
                {
                    list = new List<CommandInfo>();
                    byName[cmd.CommandName] = list;
                }
                list.Add(cmd);
            }

            foreach (var kvp in byName)
            {
                var distinctTypes = kvp.Value
                    .Select(c => c.ContainingTypeFullName)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (distinctTypes.Count <= 1)
                    continue;

                foreach (var cmd in kvp.Value)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.DuplicateCommandNameAcrossClasses,
                        cmd.Location,
                        kvp.Key,
                        string.Join(", ", distinctTypes)));
                }
            }
        });
    }

    private static CommandInfo? ExtractCommandInfo(
        GeneratorAttributeSyntaxContext ctx,
        System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
            return null;

        ct.ThrowIfCancellationRequested();

        var attr = ctx.Attributes.FirstOrDefault();
        if (attr is null)
            return null;

        string? explicitName = null;
        if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string name)
        {
            explicitName = name;
        }

        var commandName = explicitName ?? ToCamelCase(method.Name);

        var containingType = method.ContainingType;
        var ns = containingType.ContainingNamespace.IsGlobalNamespace
            ? null
            : containingType.ContainingNamespace.ToDisplayString();

        var parameters = ImmutableArray.CreateBuilder<ParameterInfo>();
        foreach (var p in method.Parameters)
        {
            var isJsonElement = p.Type.ToDisplayString() == "System.Text.Json.JsonElement";
            var isArray = false;
            var arrayElementSpecialType = SpecialType.None;
            var isNullable = false;
            var nullableUnderlyingSpecialType = SpecialType.None;

            if (p.Type is IArrayTypeSymbol arrayType)
            {
                isArray = true;
                arrayElementSpecialType = arrayType.ElementType.SpecialType;
            }
            else if (p.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                     && p.Type is INamedTypeSymbol namedNullable)
            {
                isNullable = true;
                nullableUnderlyingSpecialType = namedNullable.TypeArguments[0].SpecialType;
            }

            parameters.Add(new ParameterInfo(
                p.Name,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                p.Type.SpecialType,
                IsCancellationToken(p.Type),
                isJsonElement,
                isArray,
                arrayElementSpecialType,
                isNullable,
                nullableUnderlyingSpecialType,
                IsInjectedServiceType(p.Type)));
        }

        var returnTypeDisplay = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var isAsync = false;
        var innerReturnType = returnTypeDisplay;
        var innerSpecialType = method.ReturnType.SpecialType;
        ITypeSymbol innerReturnTypeSymbol = method.ReturnType;

        if (method.ReturnType is INamedTypeSymbol namedReturn)
        {
            var genericDef = namedReturn.OriginalDefinition.ToDisplayString();
            if (genericDef is "System.Threading.Tasks.ValueTask<TResult>" or "System.Threading.Tasks.Task<TResult>")
            {
                isAsync = true;
                innerReturnTypeSymbol = namedReturn.TypeArguments[0];
                innerReturnType = innerReturnTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                innerSpecialType = innerReturnTypeSymbol.SpecialType;
            }
            else
            {
                var nonGeneric = namedReturn.ToDisplayString();
                if (nonGeneric is "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.Task")
                {
                    isAsync = true;
                    innerReturnType = "void";
                    innerSpecialType = SpecialType.System_Void;
                }
            }
        }

        var isReturnArray = false;
        var returnArrayElementSpecialType = SpecialType.None;
        var isReturnNullable = false;
        var returnNullableUnderlyingSpecialType = SpecialType.None;

        if (innerReturnTypeSymbol is IArrayTypeSymbol returnArrayType)
        {
            isReturnArray = true;
            returnArrayElementSpecialType = returnArrayType.ElementType.SpecialType;
        }
        else if (innerReturnTypeSymbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                 && innerReturnTypeSymbol is INamedTypeSymbol namedNullableReturn)
        {
            isReturnNullable = true;
            returnNullableUnderlyingSpecialType = namedNullableReturn.TypeArguments[0].SpecialType;
        }

        var location = ctx.TargetNode.GetLocation();

        // Check for [RynJsonContext(typeof(...))] on the containing class
        string? jsonContextTypeFullName = null;
        foreach (var classAttr in containingType.GetAttributes())
        {
            if (classAttr.AttributeClass?.ToDisplayString() == JsonContextAttributeFullName
                && classAttr.ConstructorArguments.Length > 0
                && classAttr.ConstructorArguments[0].Value is INamedTypeSymbol contextTypeSymbol)
            {
                jsonContextTypeFullName = contextTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                break;
            }
        }

        return new CommandInfo(
            CommandName: commandName,
            MethodName: method.Name,
            ContainingTypeName: containingType.Name,
            ContainingTypeFullName: containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace: ns,
            IsStatic: method.IsStatic,
            IsAccessible: method.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal,
            Parameters: parameters.ToImmutable(),
            ReturnTypeDisplay: returnTypeDisplay,
            InnerReturnType: innerReturnType,
            InnerReturnSpecialType: innerSpecialType,
            IsAsync: isAsync,
            IsVoidReturn: innerSpecialType == SpecialType.System_Void,
            IsReturnArray: isReturnArray,
            ReturnArrayElementSpecialType: returnArrayElementSpecialType,
            IsReturnNullable: isReturnNullable,
            ReturnNullableUnderlyingSpecialType: returnNullableUnderlyingSpecialType,
            JsonContextTypeFullName: jsonContextTypeFullName,
            Location: new LocationInfo(location));
    }

    /// <summary>
    /// Turns a fully-qualified type name (e.g. <c>global::App.Foo.Commands</c>) into a unique, file-name-safe
    /// hint-name stem (<c>App.Foo.Commands</c>). Strips the <c>global::</c> prefix and replaces any character
    /// that is not a letter, digit, dot, or underscore.
    /// </summary>
    private static string HintName(string typeFullName)
    {
        var name = typeFullName.StartsWith("global::", StringComparison.Ordinal)
            ? typeFullName.Substring("global::".Length)
            : typeFullName;

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' ? c : '_');
        return sb.ToString();
    }

    private static bool IsCancellationToken(ITypeSymbol type) =>
        type.ToDisplayString() == "System.Threading.CancellationToken";

    /// <summary>
    /// Framework types that are injected from the DI container into a command rather than deserialized
    /// from the JSON args — Tauri-style ambient context (the window, the webview, the service provider).
    /// </summary>
    private static bool IsInjectedServiceType(ITypeSymbol type) =>
        type.ToDisplayString() is
            "Ryn.Core.IRynWindow" or
            "Ryn.Core.IRynWebView" or
            "System.IServiceProvider";

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Strip "Async" suffix for command names
        if (name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5)
            name = name.Substring(0, name.Length - 5);

        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}

internal readonly record struct CommandInfo(
    string CommandName,
    string MethodName,
    string ContainingTypeName,
    string ContainingTypeFullName,
    string? Namespace,
    bool IsStatic,
    bool IsAccessible,
    ImmutableArray<ParameterInfo> Parameters,
    string ReturnTypeDisplay,
    string InnerReturnType,
    SpecialType InnerReturnSpecialType,
    bool IsAsync,
    bool IsVoidReturn,
    bool IsReturnArray,
    SpecialType ReturnArrayElementSpecialType,
    bool IsReturnNullable,
    SpecialType ReturnNullableUnderlyingSpecialType,
    string? JsonContextTypeFullName,
    LocationInfo Location);

/// <summary>
/// Wraps a <see cref="Microsoft.CodeAnalysis.Location"/> but is excluded from value equality, so a
/// trivial edit that only shifts line numbers does not invalidate the incremental generator's cache and
/// force a re-emit. Implicitly converts back to <see cref="Microsoft.CodeAnalysis.Location"/> for diagnostics.
/// </summary>
internal readonly struct LocationInfo : IEquatable<LocationInfo>
{
    public LocationInfo(Location location) => Location = location;

    public Location Location { get; }

    public bool Equals(LocationInfo other) => true;            // ignored for caching
    public override bool Equals(object? obj) => obj is LocationInfo;
    public override int GetHashCode() => 0;

    public static implicit operator Location(LocationInfo info) => info.Location;
}

internal readonly record struct ParameterInfo(
    string Name,
    string TypeDisplay,
    SpecialType SpecialType,
    bool IsCancellationToken,
    bool IsJsonElement,
    bool IsArray,
    SpecialType ArrayElementSpecialType,
    bool IsNullable,
    SpecialType NullableUnderlyingSpecialType,
    bool IsInjected);

internal readonly record struct CommandGroup(
    string TypeFullName,
    string TypeName,
    string? Namespace,
    string? JsonContextTypeFullName,
    ImmutableArray<CommandInfo> Commands);
