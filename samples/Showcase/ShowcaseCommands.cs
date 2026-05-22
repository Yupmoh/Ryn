using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ryn.Ipc;

namespace Showcase;

public static class ShowcaseCommands
{
    [RynCommand("app.sysinfo")]
    public static string GetSystemInfo()
    {
        var info = new
        {
            os = RuntimeInformation.OSDescription,
            arch = RuntimeInformation.OSArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            processors = Environment.ProcessorCount,
            machineName = Environment.MachineName,
            userName = Environment.UserName,
            workingDir = Environment.CurrentDirectory,
        };
        return JsonSerializer.Serialize(info);
    }

    [RynCommand("app.time")]
    public static string GetTime() =>
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    [RynCommand("app.greet")]
    public static string Greet(string name) =>
        $"Hello, {name}! Welcome to Ryn.";

    [RynCommand("app.calculate")]
    public static string Calculate(string expression)
    {
        // Simple expression evaluator for demo
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return "Error: use format 'a op b' (e.g. '42 + 8')";

        if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) ||
            !double.TryParse(parts[2], CultureInfo.InvariantCulture, out var b))
            return "Error: invalid numbers";

        var result = parts[1] switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" when b != 0 => a / b,
            "/" => double.NaN,
            "%" when b != 0 => a % b,
            "^" => Math.Pow(a, b),
            _ => double.NaN,
        };

        return double.IsNaN(result)
            ? "Error: invalid operation"
            : result.ToString(CultureInfo.InvariantCulture);
    }

    [RynCommand("app.fibonacci")]
    public static string Fibonacci(int n)
    {
        if (n < 0) return "Error: n must be non-negative";
        if (n > 45) return "Error: n too large (max 45)";

        var results = new List<long>();
        long a = 0, b = 1;
        for (var i = 0; i < n; i++)
        {
            results.Add(a);
            (a, b) = (b, a + b);
        }

        return JsonSerializer.Serialize(results);
    }

    [RynCommand("app.env")]
    public static string GetEnvironmentVariables()
    {
        var vars = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                // Only include safe, non-sensitive vars
                if (!key.Contains("KEY", StringComparison.OrdinalIgnoreCase) &&
                    !key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) &&
                    !key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) &&
                    !key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase))
                {
                    vars[key] = value.Length > 100 ? value[..100] + "..." : value;
                }
            }
        }

        return JsonSerializer.Serialize(vars);
    }
}
