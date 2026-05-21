namespace Ryn.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        return args[0] switch
        {
            "new" => HandleNew(args.AsSpan(1)),
            "dev" => HandleDev(args.AsSpan(1)),
            "build" => HandleBuild(args.AsSpan(1)),
            "bundle" => HandleBundle(args.AsSpan(1)),
            "--version" or "-v" => HandleVersion(),
            "--help" or "-h" => HandleHelp(),
            _ => HandleUnknown(args[0]),
        };
    }

    private static int HandleNew(ReadOnlySpan<string> args)
    {
        Console.Error.WriteLine("ryn new: not yet implemented");
        return 1;
    }

    private static int HandleDev(ReadOnlySpan<string> args)
    {
        Console.Error.WriteLine("ryn dev: not yet implemented");
        return 1;
    }

    private static int HandleBuild(ReadOnlySpan<string> args)
    {
        Console.Error.WriteLine("ryn build: not yet implemented");
        return 1;
    }

    private static int HandleBundle(ReadOnlySpan<string> args)
    {
        Console.Error.WriteLine("ryn bundle: not yet implemented");
        return 1;
    }

    private static int HandleVersion()
    {
        Console.WriteLine(FormattableString.Invariant($"ryn {typeof(Program).Assembly.GetName().Version}"));
        return 0;
    }

    private static int HandleHelp()
    {
        PrintUsage();
        return 0;
    }

    private static int HandleUnknown(string command)
    {
        Console.Error.WriteLine(FormattableString.Invariant($"Unknown command: {command}"));
        Console.Error.WriteLine("Run 'ryn --help' for usage.");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Ryn CLI — Rich Yet Native

            Usage: ryn <command> [options]

            Commands:
              new <name>    Create a new Ryn project
              dev           Run in development mode with hot reload
              build         Build for production
              bundle        Package into platform installer

            Options:
              -h, --help       Show help
              -v, --version    Show version
            """);
    }
}
