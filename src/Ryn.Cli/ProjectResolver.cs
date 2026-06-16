namespace Ryn.Cli;

/// <summary>
/// Resolves the single Ryn project the <c>dev</c>/<c>build</c>/<c>bundle</c> commands act on. One shared
/// helper so the three commands report multi-project directories identically and all honor the same
/// <c>--project</c>/<c>-p</c> selector (mirroring <c>dotnet --project</c>) — they previously each carried a
/// private copy of a <c>FindCsproj</c> that returned null (and reported "No .csproj found") whenever more
/// than one project was present, which was actively misleading (CLI-13).
/// </summary>
internal static class ProjectResolver
{
    /// <summary>The flag (and short alias) that selects a project explicitly.</summary>
    internal const string ProjectFlag = "--project";
    internal const string ProjectFlagAlias = "-p";

    /// <summary>
    /// Resolves the project to operate on. Honors an explicit <c>--project</c>/<c>-p &lt;path&gt;</c>
    /// (a path to a <c>.csproj</c> or to a directory containing exactly one); otherwise discovers a single
    /// <c>.csproj</c> in <paramref name="dir"/>. When several are present it tries to pick the one Ryn app
    /// project deterministically, and only fails (with a message naming all candidates) when the choice is
    /// genuinely ambiguous. Exactly one of <c>path</c>/<c>error</c> is non-null on return.
    /// </summary>
    internal static (string? Path, string? Error) Resolve(string dir, string? explicitProject)
    {
        if (explicitProject is not null)
            return ResolveExplicit(explicitProject);

        var files = Directory.GetFiles(dir, "*.csproj");
        switch (files.Length)
        {
            case 1:
                return (files[0], null);
            case 0:
                return (null, "No .csproj file found in the current directory.");
            default:
                // Several projects in one directory (e.g. an app plus a test project). Prefer the
                // single Ryn app project if exactly one is identifiable; otherwise ask the user to pick.
                var rynApps = Array.FindAll(files, IsRynAppProject);
                if (rynApps.Length == 1)
                    return (rynApps[0], null);

                return (null, $"Multiple .csproj files found in the current directory ({files.Length}). "
                    + $"Pass --project <file> to choose one:{FormatList(files)}");
        }
    }

    /// <summary>
    /// Reads the <c>--project</c>/<c>-p &lt;value&gt;</c> selector from the command's args, or null when
    /// absent. Kept here so the three commands share one parsing rule alongside the resolution logic.
    /// </summary>
    internal static string? ReadExplicitProject(ReadOnlySpan<string> args)
        => GetArgValue(args, ProjectFlag) ?? GetArgValue(args, ProjectFlagAlias);

    private static (string? Path, string? Error) ResolveExplicit(string explicitProject)
    {
        var resolved = Path.GetFullPath(explicitProject);

        // Allow pointing at a directory: resolve to the single .csproj inside it.
        if (Directory.Exists(resolved))
        {
            var inDir = Directory.GetFiles(resolved, "*.csproj");
            if (inDir.Length == 1)
                return (inDir[0], null);
            if (inDir.Length == 0)
                return (null, $"No .csproj file found in '{resolved}'.");
            return (null, "Multiple .csproj files found in "
                + $"'{resolved}'. Pass --project <file> to choose one:{FormatList(inDir)}");
        }

        if (File.Exists(resolved) && resolved.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return (resolved, null);

        return (null, $"Project not found: '{explicitProject}'. Pass a path to a .csproj file or its directory.");
    }

    // A Ryn application project references the Ryn metapackage or Ryn.Core (the in-repo project
    // reference used by the samples). This is best-effort text matching, deliberately lenient: it is
    // only used to disambiguate between multiple projects, and a no-match simply falls back to asking
    // the user to choose explicitly.
    private static bool IsRynAppProject(string csprojPath)
    {
        try
        {
            var text = File.ReadAllText(csprojPath);
            return text.Contains("Ryn.Core", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Include=\"Ryn\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Include='Ryn'", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FormatList(string[] files)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in files)
            sb.Append(Environment.NewLine).Append("  ").Append(Path.GetFileName(f));
        return sb.ToString();
    }

    private static string? GetArgValue(ReadOnlySpan<string> args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
                return args[i + 1];
        }
        return null;
    }
}
