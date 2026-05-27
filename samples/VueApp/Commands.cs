using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Ryn.Ipc;

namespace VueApp;

public record SystemInfo(
    string MachineName,
    string Os,
    string Runtime,
    string Framework,
    int CpuCount,
    long MemoryMb);

public record TodoItem(int Id, string Text, bool Done);
public record TodoList(TodoItem[] Items, int DoneCount);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(TodoList))]
internal partial class AppJsonContext : JsonSerializerContext { }

[RynJsonContext(typeof(AppJsonContext))]
public static class AppCommands
{
    private static readonly List<TodoItem> Todos = [
        new(1, "Try Ryn + Vue", true),
        new(2, "Build something cool", false),
        new(3, "Ship it", false),
    ];
    private static int _nextId = 4;

    [RynCommand("app.systemInfo")]
    public static SystemInfo GetSystemInfo() => new(
        Environment.MachineName,
        RuntimeInformation.OSDescription,
        RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.FrameworkDescription,
        Environment.ProcessorCount,
        Environment.WorkingSet / 1024 / 1024);

    [RynCommand("app.greet")]
    public static string Greet(string name) =>
        $"Hello from C#, {name}! It's {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}.";

    private static TodoList MakeTodoList() =>
        new(Todos.ToArray(), Todos.Count(t => t.Done));

    [RynCommand("app.todos")]
    public static TodoList GetTodos() => MakeTodoList();

    [RynCommand("app.addTodo")]
    public static TodoList AddTodo(string text)
    {
        Todos.Add(new(_nextId++, text, false));
        return MakeTodoList();
    }

    [RynCommand("app.toggleTodo")]
    public static TodoList ToggleTodo(int id)
    {
        var idx = Todos.FindIndex(t => t.Id == id);
        if (idx >= 0)
            Todos[idx] = Todos[idx] with { Done = !Todos[idx].Done };
        return MakeTodoList();
    }

    [RynCommand("app.removeTodo")]
    public static TodoList RemoveTodo(int id)
    {
        Todos.RemoveAll(t => t.Id == id);
        return MakeTodoList();
    }

    [RynCommand("app.fibonacci")]
    public static long Fibonacci(int n)
    {
        if (n <= 1) return n;
        long a = 0, b = 1;
        for (var i = 2; i <= n; i++)
            (a, b) = (b, a + b);
        return b;
    }
}
