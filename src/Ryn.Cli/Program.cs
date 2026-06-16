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
            "new" => Dispatch(Specs.New, args.AsSpan(1), Commands.NewCommand.Execute),
            "dev" => Dispatch(Specs.Dev, args.AsSpan(1), Commands.DevCommand.Execute),
            "build" => Dispatch(Specs.Build, args.AsSpan(1), Commands.BuildCommand.Execute),
            "bundle" => Dispatch(Specs.Bundle, args.AsSpan(1), Commands.BundleCommand.Execute),
            "doctor" => Dispatch(Specs.Doctor, args.AsSpan(1), Commands.DoctorCommand.Execute),
            "updater" => DispatchUpdater(args.AsSpan(1)),
            "--version" or "-v" => HandleVersion(),
            "--help" or "-h" => HandleHelp(),
            _ => HandleUnknown(args[0]),
        };
    }

    private delegate int CommandHandler(ReadOnlySpan<string> args);

    /// <summary>
    /// Shared entry for the per-command dispatch: handles <c>-h</c>/<c>--help</c> (print usage and exit
    /// without running the command) and rejects unrecognized flags with a clear error and a nonzero exit,
    /// then delegates to the command's own <c>Execute</c>. The command classes keep their existing
    /// <c>Execute(ReadOnlySpan&lt;string&gt;)</c> signature; all flag knowledge lives in the <see cref="CommandSpec"/>
    /// here, so adding a flag is a one-line spec edit rather than a change to every command.
    /// </summary>
    private static int Dispatch(CommandSpec spec, ReadOnlySpan<string> args, CommandHandler handler)
    {
        if (WantsHelp(args))
        {
            spec.PrintHelp();
            return 0;
        }

        if (!ValidateFlags(spec, args, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine($"Run 'ryn {spec.Name} --help' for usage.");
            return 2;
        }

        return handler(args);
    }

    private static int DispatchUpdater(ReadOnlySpan<string> args)
    {
        // `updater` is a command group; its only subcommand today is `keygen`. `ryn updater` / `ryn updater
        // --help` print the group usage; an unknown subcommand is an error.
        if (args.Length == 0 || WantsHelp(args))
        {
            Specs.UpdaterKeygen.PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        return args[0] switch
        {
            "keygen" => Dispatch(Specs.UpdaterKeygen, args[1..], Commands.UpdaterKeygenCommand.Execute),
            _ => HandleUnknownUpdaterSubcommand(args[0]),
        };
    }

    private static int HandleUnknownUpdaterSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown 'updater' subcommand: {subcommand}");
        Console.Error.WriteLine("Run 'ryn updater --help' for usage.");
        return 1;
    }

    /// <summary>True when the args contain a bare <c>-h</c>/<c>--help</c> token.</summary>
    private static bool WantsHelp(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--help", StringComparison.Ordinal) ||
                string.Equals(arg, "-h", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Verifies every option token against the command's spec. Boolean flags stand alone; value flags
    /// consume the next token (which is then not itself treated as an option). Any other token that starts
    /// with <c>-</c> is an unrecognized flag and is rejected. Non-option tokens (e.g. a project name) are
    /// left for the command to interpret.
    /// </summary>
    private static bool ValidateFlags(CommandSpec spec, ReadOnlySpan<string> args, out string error)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // A lone "--" or a non-option positional argument is the command's to interpret.
            if (arg.Length == 0 || arg[0] != '-' || string.Equals(arg, "--", StringComparison.Ordinal))
                continue;

            if (spec.IsBooleanFlag(arg))
                continue;

            if (spec.IsValueFlag(arg))
            {
                // The value flag consumes the following token; skip it so a value like "-r" or one that
                // begins with '-' isn't mistaken for an unknown flag.
                if (i + 1 >= args.Length)
                {
                    error = $"Option '{arg}' requires a value.";
                    return false;
                }

                i++;
                continue;
            }

            error = $"Unknown option '{arg}' for 'ryn {spec.Name}'.";
            return false;
        }

        error = "";
        return true;
    }

    private static int HandleVersion()
    {
        Console.WriteLine(FormattableString.Invariant($"ryn {RynCliVersion.Current}"));
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
              doctor        Check development environment
              updater       Auto-updater tooling (keygen)

            Options:
              -h, --help       Show help
              -v, --version    Show version

            Run 'ryn <command> --help' for command-specific options.
            """);
    }

    /// <summary>
    /// Per-command flag metadata + usage text. Held here (in the dispatch layer) rather than on each command
    /// so the command classes stay free of arg-parsing concerns and keep their existing public signatures.
    /// </summary>
    private sealed record CommandSpec(
        string Name,
        string[] BooleanFlags,
        string[] ValueFlags,
        string Usage)
    {
        internal bool IsBooleanFlag(string arg) => Contains(BooleanFlags, arg);

        internal bool IsValueFlag(string arg) => Contains(ValueFlags, arg);

        internal void PrintHelp() => Console.WriteLine(Usage);

        private static bool Contains(string[] flags, string arg)
        {
            foreach (var flag in flags)
            {
                if (string.Equals(flag, arg, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    private static class Specs
    {
        internal static readonly CommandSpec New = new(
            "new",
            BooleanFlags: ["--vite"],
            ValueFlags: [],
            Usage: """
                Usage: ryn new <name> [--vite]

                Create a new Ryn project in a subdirectory named <name>.

                Options:
                  --vite        Scaffold a Vite (TypeScript) frontend
                  -h, --help    Show this help
                """);

        internal static readonly CommandSpec Dev = new(
            "dev",
            BooleanFlags: ["--vite"],
            ValueFlags: ["--project", "-p"],
            Usage: """
                Usage: ryn dev [--project <path>] [--vite]

                Build and run the project in the current directory with hot reload.

                Options:
                  -p, --project <path>  Project (.csproj) or directory to run; defaults to
                                        the single .csproj in the current directory
                  --vite                Point the webview at the Vite dev server (auto-detected
                                        when the project has a Vite frontend)
                  -h, --help            Show this help
                """);

        internal static readonly CommandSpec Build = new(
            "build",
            BooleanFlags: ["--aot", "--embed"],
            ValueFlags: ["--project", "-p"],
            Usage: """
                Usage: ryn build [--project <path>] [--aot] [--embed]

                Build the project in the current directory for release (dotnet publish).

                Options:
                  -p, --project <path>  Project (.csproj) or directory to build; defaults to
                                        the single .csproj in the current directory
                  --aot                 Publish with NativeAOT
                  --embed               Embed wwwroot/ content into the assembly
                  -h, --help            Show this help
                """);

        internal static readonly CommandSpec Bundle = new(
            "bundle",
            BooleanFlags: ["--aot", "--self-contained", "--notarize", "--dmg"],
            ValueFlags: ["--project", "-p", "--rid", "--version", "--icon", "--sign",
                "--entitlements", "--notary-profile", "--deep-link-scheme"],
            Usage: """
                Usage: ryn bundle [options]

                Build a release bundle for the host platform (.app on macOS, MSI staging
                on Windows, AppDir/AppImage on Linux). Cross-RID bundling is not supported.

                Options:
                  -p, --project <path>       Project (.csproj) or directory to bundle; defaults
                                             to the single .csproj in the current directory
                  --rid <rid>                Target runtime identifier (must match the host)
                  --version <ver>            Bundle version (default 1.0.0 or ryn.json)
                  --icon <path>              App icon (.png/.icns/.ico); defaults to the Ryn icon
                  --sign <identity>          macOS code-signing identity
                  --entitlements <path>      macOS entitlements plist for code signing
                  --notarize                 Submit the macOS bundle for notarization (needs --sign)
                  --notary-profile <name>    notarytool keychain profile to use (default 'notarize')
                  --dmg                      Also produce a .dmg disk image (macOS)
                  --deep-link-scheme <name>  Register a custom URL scheme (repeatable)
                  --aot                      Publish with NativeAOT
                  --self-contained           Publish self-contained
                  -h, --help                 Show this help
                """);

        internal static readonly CommandSpec Doctor = new(
            "doctor",
            BooleanFlags: ["--full"],
            ValueFlags: [],
            Usage: """
                Usage: ryn doctor [--full]

                Check that the development environment has everything Ryn needs.

                Options:
                  --full        Also run a full solution build
                  -h, --help    Show this help
                """);

        internal static readonly CommandSpec UpdaterKeygen = new(
            "updater keygen",
            BooleanFlags: [],
            ValueFlags: ["--out"],
            Usage: """
                Usage: ryn updater keygen [--out <path>]

                Generate an ECDSA P-256 signing keypair for the auto-updater. Prints the
                public key (put it in UpdaterOptions.PublicKey / ryn.json) and writes the
                secret private key to a file (default ./ryn-updater.key, owner-only perms).

                Options:
                  --out <path>  Where to write the private key (default ./ryn-updater.key)
                  -h, --help    Show this help
                """);
    }
}
