using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ryn.Ipc.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class RynCommandGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Ryn.Ipc.RynCommandAttribute";

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
                        kvp.Value.ToImmutableArray()));
                }
                return result.ToImmutable();
            });

        context.RegisterSourceOutput(grouped, static (ctx, group) =>
        {
            var source = Emitter.Emit(group, ctx);
            if (source is not null)
            {
                ctx.AddSource($"{group.TypeName}Router.g.cs", source);
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
                nullableUnderlyingSpecialType));
        }

        var returnTypeDisplay = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var isAsync = false;
        var innerReturnType = returnTypeDisplay;
        var innerSpecialType = method.ReturnType.SpecialType;
        ITypeSymbol innerReturnTypeSymbol = method.ReturnType;

        if (method.ReturnType is INamedTypeSymbol namedReturn)
        {
            if (namedReturn.OriginalDefinition.ToDisplayString() == "System.Threading.Tasks.ValueTask<TResult>")
            {
                isAsync = true;
                innerReturnTypeSymbol = namedReturn.TypeArguments[0];
                innerReturnType = innerReturnTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                innerSpecialType = innerReturnTypeSymbol.SpecialType;
            }
            else if (namedReturn.ToDisplayString() == "System.Threading.Tasks.ValueTask")
            {
                isAsync = true;
                innerReturnType = "void";
                innerSpecialType = SpecialType.System_Void;
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
            Location: location);
    }

    private static bool IsCancellationToken(ITypeSymbol type) =>
        type.ToDisplayString() == "System.Threading.CancellationToken";

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
    Location Location);

internal readonly record struct ParameterInfo(
    string Name,
    string TypeDisplay,
    SpecialType SpecialType,
    bool IsCancellationToken,
    bool IsJsonElement,
    bool IsArray,
    SpecialType ArrayElementSpecialType,
    bool IsNullable,
    SpecialType NullableUnderlyingSpecialType);

internal readonly record struct CommandGroup(
    string TypeFullName,
    string TypeName,
    string? Namespace,
    ImmutableArray<CommandInfo> Commands);
