using System.Globalization;
using Ryn.Ipc;

namespace RynApp;

public static class AppCommands
{
    [RynCommand("app.greet")]
    public static string Greet(string name) => $"Hello, {name}!";

    [RynCommand("app.add")]
    public static int Add(int a, int b) => a + b;

    [RynCommand("app.getTime")]
    public static string GetTime() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
