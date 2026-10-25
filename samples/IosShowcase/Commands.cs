using System.Runtime.InteropServices;
using Ryn.Ipc;
using UIKit;

namespace IosShowcase;

public static class AppCommands
{
    [RynCommand("app.greet")]
    public static string Greet(string name) =>
        $"Hello, {name}! From Ryn iOS running on {UIDevice.CurrentDevice.SystemName} {UIDevice.CurrentDevice.SystemVersion}.";

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
        $"Device: {UIDevice.CurrentDevice.Model} ({UIDevice.CurrentDevice.Name})\nOS: {UIDevice.CurrentDevice.SystemName} {UIDevice.CurrentDevice.SystemVersion}\nRuntime: {RuntimeInformation.FrameworkDescription}\nArch: {RuntimeInformation.OSArchitecture}";
}
