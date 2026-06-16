using System.Diagnostics;

namespace Ryn.Cli.Commands;

internal static class NewCommand
{
    /// <summary>
    /// Version of the <c>Ryn</c> metapackage that generated projects reference. Derived from the CLI's own
    /// MinVer version (<see cref="RynCliVersion.Current"/>): the CLI and the NuGet packages release in
    /// lock-step from the same git tags, so a released <c>ryn</c> always scaffolds against a matching,
    /// published <c>Ryn</c> version — no hand-maintained literal.
    /// </summary>
    private static string RynPackageVersion => RynCliVersion.Current;

    internal static int Execute(ReadOnlySpan<string> args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ryn new <name> [--vite]");
            return 1;
        }

        var name = args[0];
        var useVite = args.Contains("--vite");
        if (ValidateProjectName(name) is { } nameError)
        {
            Console.Error.WriteLine(nameError);
            return 1;
        }

        var targetDir = Path.Combine(Directory.GetCurrentDirectory(), name);
        if (Directory.Exists(targetDir))
        {
            Console.Error.WriteLine($"Directory already exists: {targetDir}");
            return 1;
        }

        Console.WriteLine($"Creating Ryn project '{name}'...");

        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(Path.Combine(targetDir, "wwwroot"));

        File.WriteAllText(Path.Combine(targetDir, $"{name}.csproj"), GetCsproj(name, targetDir));
        File.WriteAllText(Path.Combine(targetDir, "Commands.cs"), GetCommandsCs(name));
        File.WriteAllText(Path.Combine(targetDir, "appsettings.json"), GetAppSettings(name));
        File.WriteAllText(Path.Combine(targetDir, "ryn.json"), GetRynJson());

        if (useVite)
        {
            File.WriteAllText(Path.Combine(targetDir, "Program.cs"), GetViteProgramCs(name));
            ScaffoldViteFrontend(targetDir, name);
            Console.WriteLine("  Created Vite frontend");
        }
        else
        {
            File.WriteAllText(Path.Combine(targetDir, "Program.cs"), GetProgramCs(name));
            File.WriteAllText(Path.Combine(targetDir, "wwwroot", "index.html"), GetIndexHtml(name));
        }

        var sourceRoot = FindRynSourceRoot();
        if (sourceRoot is not null)
        {
            Console.WriteLine("  Using project references (Ryn source detected)");
        }
        else
        {
            Console.WriteLine($"  Using NuGet package references (Ryn {RynPackageVersion})");
        }
        Console.WriteLine("  Created project files");

        // Run dotnet restore
        var dotnet = DotnetResolver.ResolveOrReport();
        if (dotnet is null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Project files were created in {targetDir}, but packages could not be restored.");
            Console.Error.WriteLine("  Install the .NET SDK, then run 'dotnet restore' in that directory.");
            return 1;
        }

        Console.WriteLine("  Restoring packages...");
        var restore = Process.Start(new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = "restore",
            WorkingDirectory = targetDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        var restoreFailed = restore is null;
        if (restore is not null)
        {
            // Read both streams before waiting so a large restore log can't deadlock the pipe.
            var stdout = restore.StandardOutput.ReadToEndAsync();
            var stderr = restore.StandardError.ReadToEndAsync();
            restore.WaitForExit();
            restoreFailed = restore.ExitCode != 0;

            if (restoreFailed)
            {
                var log = $"{stdout.GetAwaiter().GetResult()}{stderr.GetAwaiter().GetResult()}".Trim();
                if (log.Length > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine(log);
                }
            }
        }

        if (restoreFailed)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Project '{name}' was created, but 'dotnet restore' failed.");
            Console.Error.WriteLine($"  Fix the error above, then run:  cd {name} && \"{dotnet}\" restore");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"  Project '{name}' created successfully!");
        Console.WriteLine();

        if (useVite)
        {
            Console.WriteLine($"  cd {name}/frontend");
            Console.WriteLine("  npm install");
            Console.WriteLine();
            Console.WriteLine($"  cd {name}");
            Console.WriteLine("  ryn dev            # auto-detects vite");
            Console.WriteLine("  # or: ryn dev --vite");
        }
        else
        {
            Console.WriteLine($"  cd {name}");
            Console.WriteLine("  ryn dev");
        }

        Console.WriteLine();

        return 0;
    }

    /// <summary>
    /// C# reserved keywords (and contextual keywords that are unsafe as a namespace/identifier). The
    /// project name is emitted verbatim as a <c>namespace</c>, a <c>using</c>, and a
    /// <c>&lt;RootNamespace&gt;</c>, so a keyword here produces code that does not compile. We carry an
    /// explicit set rather than depend on Roslyn's <c>SyntaxFacts</c>: the CLI is NativeAOT-published and
    /// must not pull in Microsoft.CodeAnalysis just to validate a name.
    /// </summary>
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>
    /// Windows reserved device names. The project name is also used as a directory and file name
    /// (<c>{name}.csproj</c>), and these names cannot be created as files/directories on Windows, so a
    /// scaffolded project would be unusable there. Rejected on every OS for portability.
    /// </summary>
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Validates a project name for use as a C# namespace/identifier, a directory name, and a file
    /// name. Returns <c>null</c> when the name is acceptable, or a clear, actionable error message
    /// explaining the specific rule that was violated.
    /// </summary>
    private static string? ValidateProjectName(string name)
    {
        if (name.Length == 0)
            return "Invalid project name: name is empty.";

        if (!(char.IsLetter(name[0]) || name[0] == '_'))
            return $"Invalid project name: '{name}'. A name must start with a letter or underscore.";

        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return $"Invalid project name: '{name}'. Use only letters, digits, and underscores.";
        }

        if (CSharpKeywords.Contains(name))
            return $"Invalid project name: '{name}' is a C# keyword and cannot be used as a namespace. Pick a different name (e.g. '{name}App').";

        if (WindowsReservedNames.Contains(name))
            return $"Invalid project name: '{name}' is a reserved device name and cannot be used as a folder. Pick a different name (e.g. '{name}App').";

        return null;
    }

    private static string? FindRynSourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Ryn.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string GetCsproj(string name, string targetDir)
    {
        var sourceRoot = FindRynSourceRoot();
        string references;

        if (sourceRoot is not null)
        {
            var src = Path.GetFullPath(Path.Combine(sourceRoot, "src"));
            references = $"""
                  <ItemGroup>
                    <ProjectReference Include="{src}/Ryn.Core/Ryn.Core.csproj" />
                    <ProjectReference Include="{src}/Ryn.Ipc/Ryn.Ipc.csproj" />
                    <ProjectReference Include="{src}/Ryn.Ipc.Generator/Ryn.Ipc.Generator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
                  </ItemGroup>
              """;
        }
        else
        {
            // Single Ryn metapackage: it pulls in Ryn.Core + Ryn.Interop (native libs) and, transitively,
            // Ryn.Ipc — which ships the [RynCommand] source generator as an analyzer. Referencing the
            // individual packages here would risk shipping the generator twice.
            references = $"""
                  <ItemGroup>
                    <PackageReference Include="Ryn" Version="{RynPackageVersion}" />
                  </ItemGroup>
              """;
        }

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <RootNamespace>{name}</RootNamespace>
              </PropertyGroup>

              <ItemGroup>
                <Content Include="wwwroot/**" CopyToOutputDirectory="PreserveNewest" />
                <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
                <Content Include="ryn.json" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>

            {references}
            </Project>
            """;
    }

    private static string GetProgramCs(string name) => $$"""
        using Ryn.Core;
        using Ryn.Ipc;
        using {{name}};

        public static class Program
        {
            [System.STAThread]
            public static void Main()
            {
                var app = RynApplication.CreateBuilder()
                    .ConfigureOptions(opts =>
                    {
                        opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                    })
                    .ConfigureServices(services =>
                    {
                        services.AddRynCommands();
                        services.AddAppCommands();
                    })
                    .Build();

                app.Run();
            }
        }
        """;

    private static string GetCommandsCs(string name) => $$"""
        using System.Globalization;
        using Ryn.Ipc;

        namespace {{name}};

        public static class AppCommands
        {
            [RynCommand("app.greet")]
            public static string Greet(string name) => $"Hello, {name}!";

            [RynCommand("app.add")]
            public static int Add(int a, int b) => a + b;

            [RynCommand("app.getTime")]
            public static string GetTime() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
        """;

    private static string GetIndexHtml(string name) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <title>{{name}}</title>
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body {
                    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif;
                    background: #0f0f0f; color: #e0e0e0;
                    display: flex; flex-direction: column; align-items: center;
                    justify-content: center; height: 100vh; gap: 24px;
                }
                h1 { font-size: 2.5em; color: #7c3aed; }
                p { color: #888; font-size: 1.1em; }
                .card {
                    background: #1a1a2e; border: 1px solid #2a2a4a; border-radius: 12px;
                    padding: 24px; width: 400px;
                }
                .card h2 { font-size: 1.1em; color: #a78bfa; margin-bottom: 12px; }
                input, button {
                    padding: 8px 16px; border-radius: 8px; border: 1px solid #3a3a5a;
                    background: #252540; color: #e0e0e0; font-size: 14px;
                }
                button {
                    background: #7c3aed; border: none; cursor: pointer; font-weight: 600;
                }
                button:hover { background: #6d28d9; }
                .row { display: flex; gap: 8px; margin-bottom: 12px; }
                .result {
                    background: #0d0d1a; border-radius: 8px; padding: 12px;
                    font-family: monospace; min-height: 40px; margin-top: 8px;
                    border: 1px solid #2a2a4a;
                }
            </style>
        </head>
        <body>
            <h1>{{name}}</h1>
            <p>Built with Ryn — Rich Yet Native</p>

            <div class="card">
                <h2>Try IPC</h2>
                <div class="row">
                    <input id="name" type="text" placeholder="Your name" value="World" />
                    <button onclick="doGreet()">Greet</button>
                </div>
                <div class="result" id="result">Click Greet to call C#</div>
            </div>

            <script>
                async function doGreet() {
                    var name = document.getElementById('name').value;
                    var result = await window.__ryn.invoke('app.greet', { name: name });
                    document.getElementById('result').textContent = result;
                }
            </script>
        </body>
        </html>
        """;

    private static string GetAppSettings(string name) => $$"""
        {
          "Ryn": {
            "Title": "{{name}}",
            "Width": 900,
            "Height": 700,
            "DevTools": true
          },
          "Logging": {
            "LogLevel": {
              "Default": "Information"
            }
          }
        }
        """;

    private static string GetRynJson() => """
        {
          "capabilities": {
            "app": true
          }
        }
        """;

    private static string GetViteProgramCs(string name) => $$"""
        using Ryn.Core;
        using Ryn.Ipc;
        using {{name}};

        public static class Program
        {
            [System.STAThread]
            public static void Main(string[] args)
            {
                var app = RynApplication.CreateBuilder()
                    .ConfigureOptions(opts =>
                    {
                        if (args.Contains("--vite"))
                            opts.Url = new Uri("http://localhost:5173");
                        else
                            opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                    })
                    .ConfigureServices(services =>
                    {
                        services.AddRynCommands();
                        services.AddAppCommands();
                    })
                    .Build();

                app.Run();
            }
        }
        """;

    private static void ScaffoldViteFrontend(string targetDir, string name)
    {
        var frontendDir = Path.Combine(targetDir, "frontend");
        var srcDir = Path.Combine(frontendDir, "src");

        Directory.CreateDirectory(frontendDir);
        Directory.CreateDirectory(srcDir);

        File.WriteAllText(Path.Combine(frontendDir, "package.json"), GetVitePackageJson(name));
        File.WriteAllText(Path.Combine(frontendDir, "vite.config.ts"), GetViteConfig());
        File.WriteAllText(Path.Combine(frontendDir, "tsconfig.json"), GetViteTsConfig());
        File.WriteAllText(Path.Combine(frontendDir, "index.html"), GetViteIndexHtml(name));
        File.WriteAllText(Path.Combine(srcDir, "main.ts"), GetViteMainTs(name));
        File.WriteAllText(Path.Combine(srcDir, "ryn.d.ts"), GetViteRynDts());
    }

#pragma warning disable CA1308 // npm package names require lowercase by convention
    private static string GetVitePackageJson(string name) => $$"""
        {
          "name": "{{name.ToLowerInvariant()}}",
          "private": true,
          "type": "module",
          "scripts": {
            "dev": "vite",
            "build": "tsc -b && vite build",
            "preview": "vite preview"
          },
          "devDependencies": {
            "typescript": "~5.8.0",
            "vite": "^6.3.5"
          }
        }
        """;

    private static string GetViteConfig() => """
        import { defineConfig } from 'vite'

        export default defineConfig({
          server: {
            port: 5173,
            strictPort: true,
          },
          build: {
            outDir: '../wwwroot',
            emptyOutDir: true,
          },
        })
        """;

    private static string GetViteTsConfig() => """
        {
          "compilerOptions": {
            "target": "ES2022",
            "module": "ESNext",
            "moduleResolution": "bundler",
            "strict": true,
            "resolveJsonModule": true,
            "isolatedModules": true,
            "esModuleInterop": true,
            "lib": ["ES2022", "DOM", "DOM.Iterable"],
            "skipLibCheck": true,
            "noEmit": true
          },
          "include": ["src/**/*.ts", "src/ryn.d.ts"]
        }
        """;

    private static string GetViteIndexHtml(string name) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="UTF-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>{{name}}</title>
        </head>
        <body>
          <div id="app"></div>
          <script type="module" src="/src/main.ts"></script>
        </body>
        </html>
        """;

    private static string GetViteMainTs(string name) => $$"""
        const app = document.getElementById('app')!;

        app.innerHTML = `
          <h1>{{name}}</h1>
          <p>Built with Ryn + Vite</p>
          <div class="card">
            <h2>Try IPC</h2>
            <div class="row">
              <input id="name" type="text" placeholder="Your name" value="World" />
              <button id="greet-btn">Greet</button>
            </div>
            <div class="result" id="result">Click Greet to call C#</div>
          </div>
        `;

        document.getElementById('greet-btn')!.addEventListener('click', async () => {
          const nameInput = document.getElementById('name') as HTMLInputElement;
          const result = await window.__ryn.invoke('app.greet', { name: nameInput.value });
          document.getElementById('result')!.textContent = result as string;
        });
        """;

    private static string GetViteRynDts() => """
        interface RynBridge {
          invoke(command: string, args?: Record<string, unknown>): Promise<unknown>
          on(event: string, callback: (data: unknown) => void): void
          off(event: string, callback: (data: unknown) => void): void
        }

        interface Window {
          __ryn: RynBridge
        }
        """;

}
