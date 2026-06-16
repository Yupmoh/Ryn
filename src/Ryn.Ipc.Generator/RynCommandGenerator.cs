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

        // RYN007/RYN008: report the unsupported signature shapes detected during extraction. These commands
        // are NOT grouped or emitted (see validMethods below), so the user gets a single pointed error
        // instead of a cascade of CS errors inside generated code.
        var problemMethods = methods
            .Where(static info => info.Problem != SignatureProblem.None)
            .Collect();
        context.RegisterSourceOutput(problemMethods, static (ctx, commands) =>
        {
            foreach (var cmd in commands)
                ReportSignatureProblem(ctx, cmd);
        });

        // Well-formed commands only: everything downstream (router emission and the RYN006 cross-class
        // duplicate check) operates on these so a broken signature never produces non-compiling code.
        var validMethods = methods.Where(static info => info.Problem == SignatureProblem.None);

        var grouped = validMethods.Collect()
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
        var allCommands = validMethods.Collect();
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
                        cmd.Location.ToLocation(),
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

        // GEN-07/GEN-08: detect signature shapes that today emit non-compiling code instead of an
        // actionable RYN diagnostic. The first problem found wins; it is reported (RYN007/RYN008) before
        // any source is generated, so the user sees a pointed error on their method rather than a cascade
        // of CS errors inside a .g.cs. None means the command is well-formed.
        var problem = SignatureProblem.None;
        var problemDetail = string.Empty;

        // async void [RynCommand]: classified as sync-void today, so the router fires-and-forgets it and
        // returns success immediately; a throw after the first await is unhandled and crashes the process.
        // This is the first check, so no `problem is None` guard is needed (it is always None here).
        if (method.IsAsync && method.ReturnsVoid)
        {
            problem = SignatureProblem.AsyncVoid;
            problemDetail = method.Name;
        }

        if (problem is SignatureProblem.None && method.IsGenericMethod)
        {
            problem = SignatureProblem.GenericMethod;
            problemDetail = method.Name;
        }

        if (problem is SignatureProblem.None && containingType.TypeKind is not TypeKind.Class and not TypeKind.Struct)
        {
            problem = SignatureProblem.NonClassContainingType;
            problemDetail = containingType.Name;
        }

        // Generic containing types (Commands<T>) leave T unbound in the emitted router/registration.
        if (problem is SignatureProblem.None && IsAnyContainingTypeGeneric(containingType))
        {
            problem = SignatureProblem.GenericContainingType;
            problemDetail = containingType.Name;
        }

        // The emitted router is a file-scoped top-level class, so it can only reference a containing type
        // that is public/internal at every nesting level. A private/protected nested command class compiles
        // to CS0122. (RYN005 only checks the method's own accessibility, not the type chain.)
        if (problem is SignatureProblem.None && !IsContainingTypeChainAccessible(containingType))
        {
            problem = SignatureProblem.InaccessibleContainingType;
            problemDetail = containingType.Name;
        }

        // Instance command on a struct: the emitted AddSingleton<T> has a `where T : class` constraint.
        if (problem is SignatureProblem.None && !method.IsStatic && containingType.TypeKind == TypeKind.Struct)
        {
            problem = SignatureProblem.InstanceCommandOnStruct;
            problemDetail = containingType.Name;
        }

        // Explicit command names are interpolated verbatim into case labels / a string literal. A quote or
        // backslash would break the generated source; reject any non-identifier-ish character defensively.
        if (problem is SignatureProblem.None && explicitName is not null && !IsValidCommandName(explicitName))
        {
            problem = SignatureProblem.InvalidCommandNameChar;
            problemDetail = explicitName;
        }

        var parameters = ImmutableArray.CreateBuilder<ParameterInfo>();
        foreach (var p in method.Parameters)
        {
            // ref/out/in parameters: RefKind is dropped, so the call is emitted without the modifier (CS1620)
            // and an out param is never assigned from JSON.
            if (problem is SignatureProblem.None && p.RefKind != RefKind.None)
            {
                problem = SignatureProblem.ByRefParameter;
                problemDetail = p.Name;
            }

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

            // GEN-03: a reference type annotated as nullable (`string?`, `MyDto?`) accepts a missing/null
            // JSON arg as null. Before P5 this worked; P5's missing+null throw is correct ONLY for the
            // non-nullable `string`. Value-type Nullable<T> is handled above (isNullable) and is reference
            // type-irrelevant, so this check is exclusive of it.
            var isNullableReference = p.Type.IsReferenceType
                && p.NullableAnnotation == NullableAnnotation.Annotated;

            // GEN-02: capture a C# default value so a MISSING arg binds the default instead of throwing.
            var hasDefault = p.HasExplicitDefaultValue;
            var defaultLiteral = hasDefault ? FormatDefaultLiteral(p) : string.Empty;

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
                IsInjectedServiceType(p.Type),
                isNullableReference,
                hasDefault,
                defaultLiteral));
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

        var location = LocationInfo.From(ctx.TargetNode.GetLocation());

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
            Problem: problem,
            ProblemDetail: problemDetail,
            Location: location);
    }

    private static bool IsAnyContainingTypeGeneric(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.IsGenericType)
                return true;
        }
        return false;
    }

    private static bool IsContainingTypeChainAccessible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Restricts explicit command names to characters that interpolate safely into the generated string
    /// literals and case labels. Dots are allowed for the plugin-prefix convention (e.g. "fs.readDir").
    /// </summary>
    private static bool IsValidCommandName(string name)
    {
        if (name.Length == 0)
            return false;
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == ':'))
                return false;
        }
        return true;
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

    /// <summary>
    /// GEN-02: renders a parameter's explicit C# default value as a self-contained C# literal that the
    /// emitter drops verbatim into the generated <c>!__has ? &lt;literal&gt; : &lt;read&gt;</c> binding.
    /// Caller must guard on <see cref="IParameterSymbol.HasExplicitDefaultValue"/>. Handles null, strings
    /// (quoted + escaped), chars, bools, numerics (with the correct CLR-keyword cast/suffix so the literal
    /// is the parameter's exact type), and enum members (rendered as a fully-qualified cast of the constant).
    /// </summary>
    private static string FormatDefaultLiteral(IParameterSymbol p)
    {
        var value = p.ExplicitDefaultValue;
        var type = p.Type;

        // `default`/null for any reference or nullable type — covers `string s = null`, `MyDto? d = null`,
        // and `int? n = null` alike. A non-null Nullable<T> default falls through to the underlying value.
        if (value is null)
            return "null";

        // Enums: the default is the boxed underlying constant. Emit `(global::Ns.MyEnum)<constant>` rather
        // than a member name so it compiles even for unnamed/combined flag values and is AOT-safe.
        var enumType = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                       && type is INamedTypeSymbol namedNullable
            ? namedNullable.TypeArguments[0]
            : type;
        if (enumType.TypeKind == TypeKind.Enum)
        {
            var enumName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"({enumName})({FormatPrimitiveLiteral(value)})";
        }

        return FormatPrimitiveLiteral(value);
    }

    /// <summary>
    /// Renders a boxed primitive default value as a C# literal. The numeric suffix/cast makes the literal
    /// the exact CLR type (e.g. <c>1L</c>, <c>(byte)2</c>, <c>3.5f</c>) so it binds without a narrowing error.
    /// </summary>
    private static string FormatPrimitiveLiteral(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => FormatStringLiteral(s),
        char c => $"'{EscapeChar(c)}'",
        // Floating point: round-trip ("R") and an InvariantCulture format so a comma-decimal locale on the
        // build machine cannot corrupt the literal. Suffix marks the type.
        float f => $"{f.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}f",
        double d => $"{d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}d",
        decimal m => $"{m.ToString(System.Globalization.CultureInfo.InvariantCulture)}m",
        long l => $"{l.ToString(System.Globalization.CultureInfo.InvariantCulture)}L",
        ulong ul => $"{ul.ToString(System.Globalization.CultureInfo.InvariantCulture)}UL",
        uint ui => $"{ui.ToString(System.Globalization.CultureInfo.InvariantCulture)}U",
        // byte/sbyte/short/ushort have no literal suffix; cast so the literal is the parameter's exact type.
        byte by => $"(byte){by.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        sbyte sb => $"(sbyte){sb.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        short sh => $"(short){sh.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        ushort us => $"(ushort){us.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "default",
    };

    private static string FormatStringLiteral(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
            sb.Append(EscapeChar(c));
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Escapes a single character for inclusion in a C# string or char literal.</summary>
    private static string EscapeChar(char c) => c switch
    {
        '\\' => "\\\\",
        '"' => "\\\"",
        '\'' => "\\'",
        '\0' => "\\0",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\b' => "\\b",
        '\f' => "\\f",
        // Remaining C0 control chars plus the JS-hostile line/paragraph separators (U+2028/U+2029).
        _ when c < ' ' || c == '\u2028' || c == '\u2029' => $"\\u{(int)c:X4}",
        _ => c.ToString(),
    };

    // RYN007 / RYN008: diagnostics for signature shapes that previously emitted non-compiling code (GEN-07,
    // GEN-08). Defined here (rather than in DiagnosticDescriptors) so the whole signature-validation feature
    // lives in one file. RYN007 is the async-void footgun; RYN008 is the general "unsupported shape" bucket
    // with a human-readable reason in {1}.
    private static readonly DiagnosticDescriptor AsyncVoidCommand = new(
        id: "RYN007",
        title: "async void command",
        messageFormat: "Command method '{0}' is 'async void'; this becomes fire-and-forget and an exception after the first await crashes the process — return Task or ValueTask instead",
        category: "Ryn.Ipc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedCommandShape = new(
        id: "RYN008",
        title: "Unsupported command signature shape",
        messageFormat: "Command cannot be generated: {1} ('{0}')",
        category: "Ryn.Ipc",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static void ReportSignatureProblem(SourceProductionContext ctx, CommandInfo cmd)
    {
        var location = cmd.Location.ToLocation();

        if (cmd.Problem == SignatureProblem.AsyncVoid)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(AsyncVoidCommand, location, cmd.ProblemDetail));
            return;
        }

        var reason = cmd.Problem switch
        {
            SignatureProblem.GenericMethod => "the method is generic; command methods cannot have type parameters",
            SignatureProblem.GenericContainingType => "the containing type is generic; command classes cannot have type parameters",
            SignatureProblem.NonClassContainingType => "the containing type is not a class or struct",
            SignatureProblem.InaccessibleContainingType => "the containing type (or one of its enclosing types) is not public or internal, so the generated router cannot reference it",
            SignatureProblem.ByRefParameter => "parameter uses a ref/out/in modifier, which IPC commands do not support",
            SignatureProblem.InstanceCommandOnStruct => "an instance command on a struct cannot be registered for DI (struct types are not reference types)",
            SignatureProblem.InvalidCommandNameChar => "the explicit command name contains characters that are not letters, digits, '.', '_', '-', or ':'",
            _ => "the signature is not supported",
        };

        ctx.ReportDiagnostic(Diagnostic.Create(UnsupportedCommandShape, location, cmd.ProblemDetail, reason));
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
    EquatableArray<ParameterInfo> Parameters,
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
    SignatureProblem Problem,
    string ProblemDetail,
    LocationInfo Location);

/// <summary>
/// Unsupported [RynCommand] signature shapes that currently emit non-compiling code instead of an
/// actionable diagnostic. Detected during extraction; reported as RYN007/RYN008 before any source is
/// produced. <see cref="None"/> means the command is well-formed.
/// </summary>
internal enum SignatureProblem
{
    None = 0,
    AsyncVoid,                  // RYN007
    GenericMethod,              // RYN008
    GenericContainingType,      // RYN008
    NonClassContainingType,     // RYN008
    InaccessibleContainingType, // RYN008
    ByRefParameter,             // RYN008
    InstanceCommandOnStruct,    // RYN008
    InvalidCommandNameChar,     // RYN008
}

/// <summary>
/// A cache-stable, lightweight stand-in for <see cref="Microsoft.CodeAnalysis.Location"/>. Storing a live
/// <c>Location</c> in the incremental pipeline is a known footgun: it roots the originating
/// <c>SyntaxTree</c>/<c>SourceText</c> in memory and reports stale coordinates after edits that shift line
/// numbers. Instead we capture just the file path plus the source/line spans (all value types) and
/// reconstruct a <c>Location</c> via <see cref="ToLocation"/> only at diagnostic-report time. Being a
/// proper record struct over equatable fields, it participates correctly in the generator's value-equality
/// caching.
/// </summary>
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
        var span = location.SourceSpan;
        var lineSpan = location.GetLineSpan();
        var start = lineSpan.StartLinePosition;
        var end = lineSpan.EndLinePosition;
        return new LocationInfo(
            location.SourceTree?.FilePath ?? lineSpan.Path,
            span.Start,
            span.Length,
            start.Line,
            start.Character,
            end.Line,
            end.Character);
    }

    /// <summary>Rebuilds a reportable <see cref="Location"/>; never returns null so diagnostics always have a position.</summary>
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

    /// <summary>
    /// Reconstructs the <see cref="Location"/> on demand at diagnostic-report time. Existing call sites
    /// (Emitter's RYN001-005 reports) pass a <c>LocationInfo</c> straight to <c>Diagnostic.Create</c>; this
    /// keeps them working while ensuring no live <c>Location</c> is ever stored in the pipeline model.
    /// </summary>
    public static implicit operator Location(LocationInfo info) => info.ToLocation();
}

/// <summary>
/// An <see cref="ImmutableArray{T}"/> wrapper that implements value (sequence) equality. Bare
/// <c>ImmutableArray&lt;T&gt;</c> fields compare by underlying-array reference, so a record holding one is
/// re-created on every transform run and never equals its cached predecessor — defeating the incremental
/// generator cache and re-emitting every router on any edit. Wrapping the collection restores structural
/// equality so unchanged inputs stay cached.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array;

    public EquatableArray(ImmutableArray<T> array) => _array = array;

    public ImmutableArray<T> AsImmutableArray() => _array.IsDefault ? ImmutableArray<T>.Empty : _array;

    // Both names so existing call sites that expect an array-shaped surface (.Length) and ones that
    // expect IReadOnlyList<T> (.Count, indexer, LINQ via IEnumerable<T>) keep working unchanged.
    public int Length => _array.IsDefault ? 0 : _array.Length;

    public int Count => Length;

    public T this[int index] => _array[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(EquatableArray<T> other)
    {
        var a = AsImmutableArray();
        var b = other.AsImmutableArray();
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i]))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in AsImmutableArray())
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        return hash;
    }

    public static implicit operator EquatableArray<T>(ImmutableArray<T> array) => new(array);
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
    bool IsInjected,
    // GEN-03: a reference-type parameter annotated as nullable (e.g. `string?`, `MyDto?`). Distinct from
    // IsNullable (which is reserved for value-type Nullable<T>) so the emitter can read the reference type
    // directly while still treating a missing/null JSON arg as null. A non-nullable `string` keeps the throw.
    bool IsNullableReference,
    // GEN-02: the parameter declares a C# default value. When the JSON arg is MISSING (only — an explicit
    // null still goes through the null rule), the emitter binds DefaultLiteral instead of throwing.
    bool HasDefault,
    string DefaultLiteral);

internal readonly record struct CommandGroup(
    string TypeFullName,
    string TypeName,
    string? Namespace,
    string? JsonContextTypeFullName,
    EquatableArray<CommandInfo> Commands);
