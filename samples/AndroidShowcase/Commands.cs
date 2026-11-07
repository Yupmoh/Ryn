using System.Runtime.InteropServices;
using Ryn.Ipc;

namespace AndroidShowcase;

public static class AppCommands
{
    [RynCommand("app.greet")]
    public static string Greet(string name) =>
        $"Hello, {name}! Ryn is running natively on Android with C# backend.";

    [RynCommand("app.add")]
    public static int Add(int a, int b) => a + b;

    [RynCommand("app.calculate")]
    public static double Calculate(double a, string op, double b) => op switch
    {
        "+" => a + b,
        "-" or "−" => a - b,
        "*" or "×" => a * b,
        "/" or "÷" when b != 0 => Math.Round(a / b, 4),
        _ => 0
    };

    [RynCommand("app.deviceInfo")]
    public static string GetDeviceInfo() =>
        $"OS: {RuntimeInformation.OSDescription}\nArch: {RuntimeInformation.OSArchitecture}\nRuntime: {RuntimeInformation.FrameworkDescription}\nCores: {Environment.ProcessorCount}";
}
