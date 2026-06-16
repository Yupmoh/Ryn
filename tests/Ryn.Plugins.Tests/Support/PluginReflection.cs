using System.Reflection;

namespace Ryn.Plugins.Tests.Support;

/// <summary>
/// Small reflection helpers for reaching plugin seams whose access modifier (private/internal nested
/// types, private static helpers) keeps them out of the public API but which carry security-relevant
/// logic the regression tests must pin against the *real* implementation rather than a re-implementation.
/// Reflection is used deliberately: the alternative would be loosening source visibility purely for tests,
/// and these helpers are pure/deterministic functions where invoking the genuine code is what gives the
/// test its value (a regression in the real method fails the test).
/// </summary>
internal static class PluginReflection
{
    internal static MethodInfo PrivateStatic(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
            throw new MissingMethodException($"{type.FullName}.{name} (non-public static) was not found.");
        return method;
    }

    internal static MethodInfo PrivateInstance(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null)
            throw new MissingMethodException($"{type.FullName}.{name} (non-public instance) was not found.");
        return method;
    }

    internal static MethodInfo PrivateStatic(Type type, string name, params Type[] parameterTypes)
    {
        foreach (var candidate in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (candidate.Name != name)
                continue;
            var parameters = candidate.GetParameters();
            if (parameters.Length != parameterTypes.Length)
                continue;
            var match = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != parameterTypes[i])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return candidate;
        }
        throw new MissingMethodException(
            $"{type.FullName}.{name}({string.Join(", ", parameterTypes.Select(t => t.Name))}) was not found.");
    }

    internal static T Invoke<T>(MethodInfo method, object? instance, params object?[] args)
    {
        try
        {
            return (T)method.Invoke(instance, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Surface the real exception (e.g. SecurityException) so [Fact]s can assert on it directly.
            throw ex.InnerException;
        }
    }
}
