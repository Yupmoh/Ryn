using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ryn.Callbacks.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class RynCallbackGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Ryn.Callbacks.RynCallbackAttribute";
    private const string NavigatingContextFullName = "Ryn.Core.WebViewNavigatingContext";
    private const string NavigatedContextFullName = "Ryn.Core.WebViewNavigatedContext";
    private const string NavigationDecisionFullName = "Ryn.Core.NavigationDecision";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var callbacks = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ExtractCallback(ctx, cancellationToken))
            .Where(static callback => callback is not null)
            .Select(static (callback, _) => callback!.Value);

        var groups = callbacks
            .Collect()
            .SelectMany(static (items, _) => GroupCallbacks(items));

        context.RegisterSourceOutput(groups, static (productionContext, group) =>
        {
            var source = Emitter.Emit(group, productionContext);
            if (source is not null)
            {
                productionContext.AddSource(
                    $"{HintName(group.TypeFullName)}Router.g.cs",
                    source);
            }
        });
    }

    private static CallbackInfo? ExtractCallback(
        GeneratorAttributeSyntaxContext context,
        System.Threading.CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not IMethodSymbol method)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        var attribute = context.Attributes.FirstOrDefault();
        if (attribute is null)
            return null;

        var containingType = method.ContainingType;
        var callbackKind = GetCallbackKind(attribute, out var callbackKindDisplay);
        var problem = GetContainingTypeProblem(containingType);

        if (problem == CallbackProblem.None && !IsMethodAccessible(method))
            problem = CallbackProblem.InaccessibleMethod;
        if (problem == CallbackProblem.None && method.IsGenericMethod)
            problem = CallbackProblem.GenericMethod;
        if (problem == CallbackProblem.None && method.IsAsync)
            problem = CallbackProblem.AsyncMethod;
        if (problem == CallbackProblem.None && callbackKind == CallbackKind.Unknown)
            problem = CallbackProblem.UnsupportedKind;

        var expectedParameterType = callbackKind switch
        {
            CallbackKind.WebViewNavigating => context.SemanticModel.Compilation.GetTypeByMetadataName(NavigatingContextFullName),
            CallbackKind.WebViewNavigated => context.SemanticModel.Compilation.GetTypeByMetadataName(NavigatedContextFullName),
            _ => null,
        };

        var hasValidParameter = method.Parameters.Length == 1
            && method.Parameters[0].RefKind == RefKind.None
            && expectedParameterType is not null
            && SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, expectedParameterType);

        if (problem == CallbackProblem.None && !hasValidParameter)
            problem = CallbackProblem.InvalidParameter;

        var hasValidReturn = callbackKind switch
        {
            CallbackKind.WebViewNavigating => IsExactType(
                method.ReturnType,
                context.SemanticModel.Compilation.GetTypeByMetadataName(NavigationDecisionFullName)),
            CallbackKind.WebViewNavigated => method.ReturnsVoid,
            _ => false,
        };

        if (problem == CallbackProblem.None && !hasValidReturn)
            problem = CallbackProblem.InvalidReturn;

        var typeNamespace = containingType.ContainingNamespace.IsGlobalNamespace
            ? null
            : containingType.ContainingNamespace.ToDisplayString();
        var location = context.TargetNode is MethodDeclarationSyntax declaration
            ? declaration.Identifier.GetLocation()
            : method.Locations.FirstOrDefault() ?? Location.None;

        return new CallbackInfo(
            callbackKind,
            callbackKindDisplay,
            method.Name,
            containingType.Name,
            containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            typeNamespace,
            containingType.IsStatic,
            method.IsStatic,
            problem,
            GetContainingTypeProblemReason(containingType),
            LocationInfo.From(location));
    }

    private static ImmutableArray<CallbackGroup> GroupCallbacks(ImmutableArray<CallbackInfo> callbacks)
    {
        if (callbacks.IsDefaultOrEmpty)
            return ImmutableArray<CallbackGroup>.Empty;

        var ordered = callbacks
            .OrderBy(static callback => callback.ContainingTypeFullName, StringComparer.Ordinal)
            .ThenBy(static callback => callback.Location.FilePath, StringComparer.Ordinal)
            .ThenBy(static callback => callback.Location.SpanStart)
            .ToArray();

        var result = ImmutableArray.CreateBuilder<CallbackGroup>();
        var start = 0;
        while (start < ordered.Length)
        {
            var first = ordered[start];
            var end = start + 1;
            while (end < ordered.Length
                   && string.Equals(
                       ordered[end].ContainingTypeFullName,
                       first.ContainingTypeFullName,
                       StringComparison.Ordinal))
            {
                end++;
            }

            var groupCallbacks = ImmutableArray.CreateBuilder<CallbackInfo>(end - start);
            for (var i = start; i < end; i++)
                groupCallbacks.Add(ordered[i]);

            result.Add(new CallbackGroup(
                first.ContainingTypeFullName,
                first.ContainingTypeName,
                first.Namespace,
                first.IsStaticContainingType,
                groupCallbacks.ToImmutable()));
            start = end;
        }

        return result.ToImmutable();
    }

    private static CallbackKind GetCallbackKind(AttributeData attribute, out string display)
    {
        if (attribute.ConstructorArguments.Length == 0)
        {
            display = "<missing>";
            return CallbackKind.Unknown;
        }

        var argument = attribute.ConstructorArguments[0];
        display = argument.Value?.ToString() ?? "<invalid>";
        if (argument.Type is not INamedTypeSymbol enumType)
            return CallbackKind.Unknown;

        foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
        {
            if (!member.HasConstantValue || !Equals(member.ConstantValue, argument.Value))
                continue;

            display = member.Name;
            return member.Name switch
            {
                "WebViewNavigating" => CallbackKind.WebViewNavigating,
                "WebViewNavigated" => CallbackKind.WebViewNavigated,
                _ => CallbackKind.Unknown,
            };
        }

        return CallbackKind.Unknown;
    }

    private static CallbackProblem GetContainingTypeProblem(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
            return CallbackProblem.InvalidContainingType;
        if (IsAnyContainingTypeGeneric(type))
            return CallbackProblem.InvalidContainingType;
        if (!IsContainingTypeChainAccessible(type))
            return CallbackProblem.InvalidContainingType;
        if (type.IsAbstract && !type.IsStatic)
            return CallbackProblem.InvalidContainingType;
        return CallbackProblem.None;
    }

    private static string GetContainingTypeProblemReason(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
            return "callback methods must be declared in a class";
        if (IsAnyContainingTypeGeneric(type))
            return "generic containing types are not supported";
        if (!IsContainingTypeChainAccessible(type))
            return "the type and every enclosing type must be public or internal";
        if (type.IsAbstract && !type.IsStatic)
            return "an abstract instance type cannot be registered with dependency injection";
        return string.Empty;
    }

    private static bool IsAnyContainingTypeGeneric(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
                return true;
        }

        return false;
    }

    private static bool IsContainingTypeChainAccessible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return false;
        }

        return true;
    }

    private static bool IsMethodAccessible(IMethodSymbol method) =>
        method.DeclaredAccessibility is
            Accessibility.Public or
            Accessibility.Internal or
            Accessibility.ProtectedOrInternal;

    private static bool IsExactType(ITypeSymbol actual, INamedTypeSymbol? expected) =>
        expected is not null && SymbolEqualityComparer.Default.Equals(actual, expected);

    private static string HintName(string typeFullName)
    {
        var name = typeFullName.StartsWith("global::", StringComparison.Ordinal)
            ? typeFullName.Substring("global::".Length)
            : typeFullName;
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '_' ? character : '_');
        return builder.ToString();
    }
}

internal enum CallbackKind
{
    Unknown = 0,
    WebViewNavigating,
    WebViewNavigated,
}

internal enum CallbackProblem
{
    None = 0,
    InvalidContainingType,
    InaccessibleMethod,
    GenericMethod,
    AsyncMethod,
    InvalidParameter,
    InvalidReturn,
    UnsupportedKind,
}

internal readonly record struct CallbackInfo(
    CallbackKind Kind,
    string KindDisplay,
    string MethodName,
    string ContainingTypeName,
    string ContainingTypeFullName,
    string? Namespace,
    bool IsStaticContainingType,
    bool IsStaticMethod,
    CallbackProblem Problem,
    string ContainingTypeProblemReason,
    LocationInfo Location);

internal readonly record struct CallbackGroup(
    string TypeFullName,
    string TypeName,
    string? Namespace,
    bool IsStaticContainingType,
    EquatableArray<CallbackInfo> Callbacks);

internal readonly record struct LocationInfo(
    string? FilePath,
    int SpanStart,
    int SpanLength,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter)
{
    public static LocationInfo From(Location location)
    {
        if (location == Location.None)
            return new LocationInfo(null, 0, 0, 0, 0, 0, 0);

        var span = location.SourceSpan;
        var lineSpan = location.GetLineSpan();
        return new LocationInfo(
            location.SourceTree?.FilePath ?? lineSpan.Path,
            span.Start,
            span.Length,
            lineSpan.StartLinePosition.Line,
            lineSpan.StartLinePosition.Character,
            lineSpan.EndLinePosition.Line,
            lineSpan.EndLinePosition.Character);
    }

    public Location ToLocation()
    {
        if (FilePath is null)
            return Location.None;

        return Location.Create(
            FilePath,
            new Microsoft.CodeAnalysis.Text.TextSpan(SpanStart, SpanLength),
            new Microsoft.CodeAnalysis.Text.LinePositionSpan(
                new Microsoft.CodeAnalysis.Text.LinePosition(StartLine, StartCharacter),
                new Microsoft.CodeAnalysis.Text.LinePosition(EndLine, EndCharacter)));
    }
}

internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items) => _items = items;

    public int Count => _items.IsDefault ? 0 : _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() =>
        ((IEnumerable<T>)(_items.IsDefault ? ImmutableArray<T>.Empty : _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(EquatableArray<T> other)
    {
        if (Count != other.Count)
            return false;

        for (var i = 0; i < Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(this[i], other[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        for (var i = 0; i < Count; i++)
            hash = (hash * 31) + this[i].GetHashCode();
        return hash;
    }

    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);
}
