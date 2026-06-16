using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace Ryn.Cli.Commands;

internal static class BundleCommand
{
    internal static int Execute(ReadOnlySpan<string> args)
    {
        var (csproj, error) = ProjectResolver.Resolve(
            Directory.GetCurrentDirectory(), ProjectResolver.ReadExplicitProject(args));
        if (csproj is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var projectDir = Path.GetDirectoryName(csproj)!;
        var projectName = Path.GetFileNameWithoutExtension(csproj);

        var targetRid = GetArgValue(args, "--rid");
        if (targetRid is not null && !string.Equals(targetRid, RuntimeInformation.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"  Cross-RID bundling is not supported. Cannot bundle for '{targetRid}' on {RuntimeInformation.RuntimeIdentifier}.");
            Console.Error.WriteLine("  Build the bundle on the target platform, or use CI to produce platform-specific bundles.");
            return 1;
        }

        var dotnet = DotnetResolver.ResolveOrReport();
        if (dotnet is null)
            return 1;

        // The exact same publish arguments are reused both for the build and for the deterministic
        // PublishDir query below, so the directory we bundle from is the one this publish produced.
        var publishArgs = BuildPublishArgs(args);

        Console.WriteLine($"Building {projectName} for release...");
        var buildProcess = Process.Start(new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = publishArgs,
            WorkingDirectory = projectDir,
            UseShellExecute = false,
        });
        buildProcess?.WaitForExit();

        if (buildProcess?.ExitCode != 0)
        {
            Console.Error.WriteLine("  Build failed.");
            return 1;
        }

        var publishDir = ResolvePublishDir(dotnet, projectDir, publishArgs);
        if (publishDir is null)
        {
            Console.Error.WriteLine("  Could not locate the publish output directory.");
            Console.Error.WriteLine("  The build reported success but no publish output was found — try `dotnet publish` manually to diagnose.");
            return 1;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return CreateMacAppBundle(projectDir, projectName, publishDir, args);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return CreateWindowsBundle(projectDir, projectName, publishDir, args);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return CreateLinuxBundle(projectDir, projectName, publishDir, args);

        Console.WriteLine();
        Console.WriteLine($"  Bundle ready: {publishDir}");
        return 0;
    }

    private static int CreateMacAppBundle(string projectDir, string projectName, string publishDir, ReadOnlySpan<string> args)
    {
        var bundleDir = Path.Combine(projectDir, "bin", "bundle", $"{projectName}.app");
        var contentsDir = Path.Combine(bundleDir, "Contents");
        var macosDir = Path.Combine(contentsDir, "MacOS");
        var resourcesDir = Path.Combine(contentsDir, "Resources");

        // Clean previous bundle
        if (Directory.Exists(bundleDir))
            Directory.Delete(bundleDir, recursive: true);

        Directory.CreateDirectory(macosDir);
        Directory.CreateDirectory(resourcesDir);

        // Read ryn.json + CLI args for bundle metadata
#pragma warning disable CA1308 // Bundle identifiers require lowercase by convention
        var bundleId = $"com.ryn.{projectName.ToLowerInvariant()}";
#pragma warning restore CA1308
        var bundleVersion = GetArgValue(args, "--version") ?? "1.0.0";
        var iconPath = GetArgValue(args, "--icon");
        var signIdentity = GetArgValue(args, "--sign");
        var entitlements = GetArgValue(args, "--entitlements");
        var notaryProfile = GetArgValue(args, "--notary-profile") ?? "notarize";
        var notarize = args.Contains("--notarize");
        var makeDmg = args.Contains("--dmg");
        var deepLinkSchemes = GetArgValues(args, "--deep-link-scheme");

        var rynJsonPath = Path.Combine(projectDir, "ryn.json");
        if (File.Exists(rynJsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rynJsonPath));
                if (doc.RootElement.TryGetProperty("bundle", out var bundle))
                {
                    if (bundle.TryGetProperty("identifier", out var id))
                        bundleId = id.GetString() ?? bundleId;
                    if (bundle.TryGetProperty("version", out var ver))
                        bundleVersion = ver.GetString() ?? bundleVersion;
                    if (iconPath is null && bundle.TryGetProperty("icon", out var ico))
                        iconPath = ico.GetString();
                    if (signIdentity is null && bundle.TryGetProperty("sign", out var sig))
                        signIdentity = sig.GetString();
                    if (deepLinkSchemes.Count == 0)
                        deepLinkSchemes = ReadDeepLinkSchemes(bundle);
                }
            }
            catch (JsonException) { /* use defaults */ }
            catch (IOException) { /* use defaults */ }
        }

        // Build Contents/Resources/AppIcon.icns from the app's icon, or the bundled Ryn default.
        var icnsName = ResolveMacIcon(projectDir, resourcesDir, iconPath);
        var iconEntry = icnsName is not null
            ? $"\n    <key>CFBundleIconFile</key>\n    <string>{XmlEscape(icnsName)}</string>"
            : "";
        var urlTypesEntry = BuildUrlTypesPlist(bundleId, deepLinkSchemes);
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleExecutable</key>
                <string>{XmlEscape(projectName)}</string>
                <key>CFBundleIdentifier</key>
                <string>{XmlEscape(bundleId)}</string>
                <key>CFBundleName</key>
                <string>{XmlEscape(projectName)}</string>
                <key>CFBundleDisplayName</key>
                <string>{XmlEscape(projectName)}</string>
                <key>CFBundlePackageType</key>
                <string>APPL</string>
                <key>CFBundleVersion</key>
                <string>{XmlEscape(bundleVersion)}</string>
                <key>CFBundleShortVersionString</key>
                <string>{XmlEscape(bundleVersion)}</string>
                <key>LSMinimumSystemVersion</key>
                <string>11.0</string>
                <key>NSHighResolutionCapable</key>
                <true/>{iconEntry}{urlTypesEntry}
            </dict>
            </plist>
            """;

        File.WriteAllText(Path.Combine(contentsDir, "Info.plist"), plist);

        // Copy published output to MacOS/. The runtime reads wwwroot from AppContext.BaseDirectory,
        // which is Contents/MacOS, so the publish output (which already contains wwwroot) is the only
        // copy the app needs — there is intentionally no separate Contents/Resources/wwwroot copy.
        foreach (var file in Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(publishDir, file);
            var destPath = Path.Combine(macosDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null) Directory.CreateDirectory(destDir);
            File.Copy(file, destPath, overwrite: true);
        }

        // Make the executable... executable
        var execPath = Path.Combine(macosDir, projectName);
        if (File.Exists(execPath))
        {
            Process.Start("chmod", $"+x \"{execPath}\"")?.WaitForExit();
        }

        if (notarize && signIdentity is null)
        {
            Console.Error.WriteLine("  Error: --notarize requires a signing identity. Pass --sign \"Developer ID Application: ...\".");
            return 1;
        }

        // Code sign if identity specified. Hardened runtime (--options runtime) and a secure timestamp are
        // mandatory for notarization, and nested Mach-O binaries must be signed inside-out (deepest first)
        // because the now-deprecated `codesign --deep` cannot apply per-file options/entitlements reliably.
        if (signIdentity is not null)
        {
            Console.WriteLine($"  Signing with identity: {signIdentity}");
            if (!CodesignBundle(bundleDir, signIdentity, entitlements))
                Console.Error.WriteLine("  Warning: code signing failed");
            else
                Console.WriteLine("  Code signing succeeded");
        }

        // Notarize if requested
        if (notarize && signIdentity is not null)
        {
            Console.WriteLine($"  Submitting for notarization (keychain profile: {notaryProfile})...");
            var zipPath = bundleDir + ".zip";
            try
            {
                if (!RunTool("ditto", "-c", "-k", "--keepParent", bundleDir, zipPath))
                {
                    Console.Error.WriteLine("  Warning: could not create the notarization archive");
                }
                else if (SubmitForNotarization(zipPath, notaryProfile))
                {
                    if (RunTool("xcrun", "stapler", "staple", bundleDir))
                        Console.WriteLine("  Notarization succeeded and the ticket was stapled");
                    else
                        Console.Error.WriteLine("  Warning: notarization succeeded but stapling failed");
                }
                else
                {
                    Console.Error.WriteLine("  Warning: notarization failed (see the notarytool log above)");
                }
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
        }

        if (makeDmg)
        {
            var dmgPath = Path.Combine(projectDir, "bin", "bundle", $"{projectName}-{bundleVersion}.dmg");
            if (File.Exists(dmgPath)) File.Delete(dmgPath);
            Console.WriteLine("  Creating .dmg...");
            if (RunTool("hdiutil", "create", "-volname", projectName, "-srcfolder", bundleDir, "-ov", "-format", "UDZO", dmgPath))
                Console.WriteLine($"  Disk image created: {dmgPath}");
            else
                Console.Error.WriteLine("  Warning: hdiutil failed to create the .dmg");
        }

        Console.WriteLine();
        Console.WriteLine($"  macOS app bundle created: {bundleDir}");
        Console.WriteLine($"  Run with: open \"{bundleDir}\"");
        return 0;
    }

    /// <summary>
    /// Signs every nested Mach-O binary (dylibs and executables) inside-out, then the bundle itself, with the
    /// hardened runtime and a secure timestamp. Returns true only if every signature succeeded.
    /// </summary>
    private static bool CodesignBundle(string bundleDir, string identity, string? entitlements)
    {
        // Sign nested binaries deepest-first so each enclosing seal covers already-signed contents.
        var nested = new List<string>();
        foreach (var file in Directory.GetFiles(bundleDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                IsMachOExecutable(file))
            {
                nested.Add(file);
            }
        }
        nested.Sort(static (a, b) => b.Length.CompareTo(a.Length)); // longer path == deeper == signed first

        var ok = true;
        foreach (var binary in nested)
            ok &= CodesignOne(binary, identity, entitlements);

        // Finally seal the bundle as a whole.
        ok &= CodesignOne(bundleDir, identity, entitlements);
        return ok;
    }

    private static bool CodesignOne(string target, string identity, string? entitlements)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "codesign",
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add("--options");
        psi.ArgumentList.Add("runtime");
        psi.ArgumentList.Add("--timestamp");
        if (entitlements is not null)
        {
            psi.ArgumentList.Add("--entitlements");
            psi.ArgumentList.Add(entitlements);
        }
        psi.ArgumentList.Add("--sign");
        psi.ArgumentList.Add(identity);
        psi.ArgumentList.Add(target);

        try
        {
            var process = Process.Start(psi);
            if (process is null) return false;
            var err = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                Console.Error.WriteLine($"    codesign: {err.Trim()}");
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Heuristically detects a Mach-O executable by its magic number (no extension on macOS binaries).</summary>
    private static bool IsMachOExecutable(string path)
    {
        if (Path.HasExtension(path)) return false; // dylibs handled separately; data files have extensions
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) < 4) return false;
            // Mach-O magic numbers (32/64-bit, both endiannesses) and fat/universal.
            var value = (uint)(magic[0] | (magic[1] << 8) | (magic[2] << 16) | (magic[3] << 24));
            return value is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Submits a zipped bundle to notarytool, streaming its log so failures are diagnosable.</summary>
    private static bool SubmitForNotarization(string zipPath, string keychainProfile)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "xcrun",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("notarytool");
        psi.ArgumentList.Add("submit");
        psi.ArgumentList.Add(zipPath);
        psi.ArgumentList.Add("--keychain-profile");
        psi.ArgumentList.Add(keychainProfile);
        psi.ArgumentList.Add("--wait");

        try
        {
            var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Builds the <c>CFBundleURLTypes</c> plist fragment, one URL-type dict per deep-link scheme.</summary>
    private static string BuildUrlTypesPlist(string bundleId, List<string> schemes)
    {
        if (schemes.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n    <key>CFBundleURLTypes</key>\n    <array>");
        foreach (var scheme in schemes)
        {
            sb.Append("\n        <dict>");
            sb.Append("\n            <key>CFBundleURLName</key>");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n            <string>{XmlEscape(bundleId)}.{XmlEscape(scheme)}</string>");
            sb.Append("\n            <key>CFBundleURLSchemes</key>");
            sb.Append("\n            <array>");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n                <string>{XmlEscape(scheme)}</string>");
            sb.Append("\n            </array>");
            sb.Append("\n        </dict>");
        }
        sb.Append("\n    </array>");
        return sb.ToString();
    }

    /// <summary>Reads <c>bundle.deepLinkSchemes</c> (array of strings) from a parsed ryn.json bundle element.</summary>
    private static List<string> ReadDeepLinkSchemes(JsonElement bundle)
    {
        var schemes = new List<string>();
        if (bundle.TryGetProperty("deepLinkSchemes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) schemes.Add(s);
            }
        }
        return schemes;
    }

    private static string XmlEscape(string value) => SecurityElement.Escape(value) ?? value;

    private static string? GetArgValue(ReadOnlySpan<string> args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>Collects every value of a repeatable flag (e.g. <c>--deep-link-scheme a --deep-link-scheme b</c>).</summary>
    private static List<string> GetArgValues(ReadOnlySpan<string> args, string flag)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag && !string.IsNullOrWhiteSpace(args[i + 1]))
                values.Add(args[i + 1]);
        }
        return values;
    }

    /// <summary>
    /// Produces <c>Contents/Resources/AppIcon.icns</c> from the app's own icon, or — when none is supplied —
    /// the bundled Ryn default. PNG sources are converted via the macOS <c>sips</c>/<c>iconutil</c> toolchain.
    /// Returns the <c>CFBundleIconFile</c> base name (<c>AppIcon</c>), or null if no icon could be produced.
    /// </summary>
    private static string? ResolveMacIcon(string projectDir, string resourcesDir, string? iconPath)
    {
        var source = ResolveIconSource(projectDir, iconPath);
        var usingDefault = false;
        if (source is null)
        {
            source = ExtractEmbeddedIcon("Ryn.Cli.ryn-icon.png", ".png");
            usingDefault = source is not null;
        }
        if (source is null)
            return null;

        var icnsDest = Path.Combine(resourcesDir, "AppIcon.icns");
        bool ok;
        if (source.EndsWith(".icns", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(source, icnsDest, overwrite: true); ok = true; }
            catch (IOException) { ok = false; }
        }
        else
        {
            ok = TryGenerateIcns(source, icnsDest);
        }

        if (!ok)
        {
            Console.Error.WriteLine("  Warning: could not generate the app icon (.icns); the bundle will use the system default.");
            return null;
        }

        Console.WriteLine(usingDefault ? "  Icon: bundled Ryn default" : $"  Icon: {Path.GetFileName(source)}");
        return "AppIcon";
    }

    /// <summary>Builds a multi-resolution .icns from a PNG via the macOS sips + iconutil tools.</summary>
    private static bool TryGenerateIcns(string sourcePng, string icnsPath)
    {
        var iconset = Path.Combine(Path.GetTempPath(), $"ryn_icon_{Guid.NewGuid():N}.iconset");
        try
        {
            Directory.CreateDirectory(iconset);

            (int Size, string Name)[] entries =
            {
                (16, "icon_16x16"), (32, "icon_16x16@2x"), (32, "icon_32x32"), (64, "icon_32x32@2x"),
                (128, "icon_128x128"), (256, "icon_128x128@2x"), (256, "icon_256x256"), (512, "icon_256x256@2x"),
                (512, "icon_512x512"), (1024, "icon_512x512@2x"),
            };

            foreach (var (size, name) in entries)
            {
                var dim = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!RunTool("sips", "-z", dim, dim, sourcePng, "--out", Path.Combine(iconset, name + ".png")))
                    return false;
            }

            return RunTool("iconutil", "-c", "icns", iconset, "-o", icnsPath) && File.Exists(icnsPath);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (Directory.Exists(iconset)) Directory.Delete(iconset, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    private static bool RunTool(string fileName, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in arguments) psi.ArgumentList.Add(arg);
            var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Resolves an app-supplied icon path (absolute or project-relative) to an existing file, or null.</summary>
    private static string? ResolveIconSource(string projectDir, string? iconPath)
    {
        if (string.IsNullOrEmpty(iconPath)) return null;
        var resolved = Path.IsPathRooted(iconPath) ? iconPath : Path.Combine(projectDir, iconPath);
        return File.Exists(resolved) ? resolved : null;
    }

    /// <summary>Writes an embedded default icon resource to a temp file and returns its path, or null.</summary>
    private static string? ExtractEmbeddedIcon(string resourceName, string extension)
    {
        using var stream = typeof(BundleCommand).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        var dest = Path.Combine(Path.GetTempPath(), $"ryn_default_icon_{Guid.NewGuid():N}{extension}");
        try
        {
            using var fs = File.Create(dest);
            stream.CopyTo(fs);
            return dest;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static int CreateWindowsBundle(string projectDir, string projectName, string publishDir, ReadOnlySpan<string> args)
    {
        var bundleDir = Path.Combine(projectDir, "bin", "bundle", projectName);

        if (Directory.Exists(bundleDir))
            Directory.Delete(bundleDir, recursive: true);

        Directory.CreateDirectory(bundleDir);

        // Read ryn.json + CLI args for bundle metadata
        var bundleVersion = GetArgValue(args, "--version") ?? "1.0.0";
        var iconPath = GetArgValue(args, "--icon");
        string? manufacturer = null;
#pragma warning disable CA1308 // Bundle identifiers are lowercase by convention
        var bundleId = $"com.ryn.{projectName.ToLowerInvariant()}";
#pragma warning restore CA1308

        var rynJsonPath = Path.Combine(projectDir, "ryn.json");
        if (File.Exists(rynJsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rynJsonPath));
                if (doc.RootElement.TryGetProperty("bundle", out var bundle))
                {
                    if (bundle.TryGetProperty("version", out var ver))
                        bundleVersion = ver.GetString() ?? bundleVersion;
                    if (iconPath is null && bundle.TryGetProperty("icon", out var ico))
                        iconPath = ico.GetString();
                    if (bundle.TryGetProperty("manufacturer", out var mfr))
                        manufacturer = mfr.GetString();
                    if (bundle.TryGetProperty("identifier", out var id))
                        bundleId = id.GetString() ?? bundleId;
                }
            }
            catch (JsonException) { /* use defaults */ }
            catch (IOException) { /* use defaults */ }
        }

        manufacturer ??= projectName;

        // Copy published output
        foreach (var file in Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(publishDir, file);
            var destPath = Path.Combine(bundleDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null) Directory.CreateDirectory(destDir);
            File.Copy(file, destPath, overwrite: true);
        }

        // Copy wwwroot if present
        var wwwrootDir = Path.Combine(projectDir, "wwwroot");
        if (Directory.Exists(wwwrootDir))
        {
            var destWwwroot = Path.Combine(bundleDir, "wwwroot");
            foreach (var file in Directory.GetFiles(wwwrootDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(wwwrootDir, file);
                var destPath = Path.Combine(destWwwroot, relativePath);
                var destDir = Path.GetDirectoryName(destPath);
                if (destDir is not null) Directory.CreateDirectory(destDir);
                File.Copy(file, destPath, overwrite: true);
            }
        }

        // Copy the app icon, or the bundled Ryn default (.ico), into the bundle for the installer/shortcut.
        var resolvedIcon = ResolveIconSource(projectDir, iconPath);
        var usingDefaultIcon = false;
        if (resolvedIcon is null)
        {
            resolvedIcon = ExtractEmbeddedIcon("Ryn.Cli.ryn-icon.ico", ".ico");
            usingDefaultIcon = resolvedIcon is not null;
        }
        string? bundledIconName = null;
        if (resolvedIcon is not null && File.Exists(resolvedIcon))
        {
            bundledIconName = usingDefaultIcon ? "AppIcon.ico" : Path.GetFileName(resolvedIcon);
            File.Copy(resolvedIcon, Path.Combine(bundleDir, bundledIconName), overwrite: true);
            Console.WriteLine(usingDefaultIcon ? "  Icon: bundled Ryn default" : $"  Icon: {bundledIconName}");
        }

        // Create run.bat launcher
        var batContent = $"""
            @echo off
            cd /d "%~dp0"
            start "" "{projectName}.exe" %*
            """;
        File.WriteAllText(Path.Combine(bundleDir, "run.bat"), batContent);

        // Generate WiX v5 .wxs file for MSI creation
        var wxsPath = Path.Combine(bundleDir, $"{projectName}.wxs");
        GenerateWixFile(wxsPath, bundleDir, projectName, bundleVersion, manufacturer, bundleId, bundledIconName);

        Console.WriteLine();
        Console.WriteLine($"  Windows bundle created: {bundleDir}");
        Console.WriteLine($"  Run with: {Path.Combine(bundleDir, "run.bat")}");
        Console.WriteLine();
        Console.WriteLine($"  To build MSI: install WiX v5 (`dotnet tool install --global wix`) then run `wix build {projectName}.wxs -o {projectName}.msi`");
        return 0;
    }

    private static void GenerateWixFile(string wxsPath, string bundleDir, string projectName, string bundleVersion, string manufacturer, string bundleId, string? iconPath)
    {
        // Seed the UpgradeCode with the stable bundle identifier so two apps that happen to share a project
        // name never collide on the same upgrade lineage.
        var upgradeCode = GenerateDeterministicGuid(bundleId, "upgrade");
        var shortcutGuid = GenerateDeterministicGuid(bundleId, "shortcut");

        // Build a nested Directory/Component tree mirroring the on-disk layout (wwwroot, runtimes, etc.) so a
        // built MSI installs the app with its real directory structure instead of flattening everything into
        // the install root. Each directory becomes its own Component (one KeyPath file per Component).
        var componentRefs = new System.Text.StringBuilder();
        var dirTree = BuildWixDirectoryTree(bundleDir, bundleId, componentRefs, indent: "                    ");

        // Icon reference for Add/Remove Programs
        var iconElements = "";
        if (iconPath is not null)
        {
            var iconFileName = XmlEscape(Path.GetFileName(iconPath));
            iconElements = $"""

                    <Icon Id="AppIcon" SourceFile="{iconFileName}" />
                    <Property Id="ARPPRODUCTICON" Value="AppIcon" />
            """;
        }

        var escName = XmlEscape(projectName);
        var escManufacturer = XmlEscape(manufacturer);
        var escVersion = XmlEscape(bundleVersion);

        var wxs = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
                <Package Name="{escName}"
                         Version="{escVersion}"
                         Manufacturer="{escManufacturer}"
                         UpgradeCode="{upgradeCode}"
                         Compressed="yes">

                    <MajorUpgrade DowngradeErrorMessage="A newer version of {escName} is already installed." />
                    <MediaTemplate EmbedCab="yes" />{iconElements}

                    <StandardDirectory Id="ProgramFiles6432Folder">
                        <Directory Id="INSTALLFOLDER" Name="{escName}">
            {dirTree}
                        </Directory>
                    </StandardDirectory>

                    <StandardDirectory Id="ProgramMenuFolder">
                        <Component Id="StartMenuShortcut" Guid="{shortcutGuid}">
                            <Shortcut Id="AppShortcut"
                                      Name="{escName}"
                                      Target="[INSTALLFOLDER]{escName}.exe"
                                      WorkingDirectory="INSTALLFOLDER" />
                            <RegistryValue Root="HKCU"
                                           Key="Software\{escManufacturer}\{escName}"
                                           Name="StartMenuShortcut"
                                           Type="integer"
                                           Value="1"
                                           KeyPath="yes" />
                        </Component>
                    </StandardDirectory>

                    <Feature Id="Main" Level="1">
            {componentRefs}            <ComponentRef Id="StartMenuShortcut" />
                    </Feature>
                </Package>
            </Wix>
            """;

        File.WriteAllText(wxsPath, wxs);
    }

    /// <summary>
    /// Recursively emits a nested <c>Directory</c>/<c>Component</c>/<c>File</c> tree for everything under
    /// <paramref name="dir"/> (skipping the generated .wxs). Each directory level gets one Component whose files
    /// install into that level, preserving wwwroot and other subfolders. Appends a ComponentRef for every emitted
    /// component to <paramref name="componentRefs"/>.
    /// </summary>
    private static string BuildWixDirectoryTree(string dir, string idSeed, System.Text.StringBuilder componentRefs, string indent)
    {
        var sb = new System.Text.StringBuilder();
        BuildWixLevel(dir, dir, idSeed, sb, componentRefs, indent, new IdAllocator());
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static void BuildWixLevel(string root, string dir, string idSeed, System.Text.StringBuilder sb, System.Text.StringBuilder componentRefs, string indent, IdAllocator ids)
    {
        // Files directly in this directory -> one Component.
        var files = Directory.GetFiles(dir)
            .Where(f => !Path.GetFileName(f).EndsWith(".wxs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        if (files.Length > 0)
        {
            var relDirForId = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '_').Replace("..", "root", StringComparison.Ordinal);
            var componentId = ids.Next("Cmp_" + Sanitize(relDirForId));
            var componentGuid = GenerateDeterministicGuid(idSeed, "component:" + Path.GetRelativePath(root, dir));
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}<Component Id=\"{componentId}\" Guid=\"{componentGuid}\">");
            foreach (var file in files)
            {
                var fileId = ids.Next("File_" + Sanitize(Path.GetFileName(file)));
                var source = XmlEscape(Path.GetRelativePath(root, file).Replace('/', '\\'));
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}    <File Id=\"{fileId}\" Source=\"{source}\" />");
            }
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}</Component>");
            componentRefs.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"            <ComponentRef Id=\"{componentId}\" />");
        }

        // Subdirectories -> nested Directory elements.
        foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = XmlEscape(Path.GetFileName(sub));
            var dirId = ids.Next("Dir_" + Sanitize(Path.GetRelativePath(root, sub).Replace(Path.DirectorySeparatorChar, '_')));
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}<Directory Id=\"{dirId}\" Name=\"{name}\">");
            BuildWixLevel(root, sub, idSeed, sb, componentRefs, indent + "    ", ids);
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}</Directory>");
        }
    }

    /// <summary>Reduces a string to characters legal in a WiX Id (letters, digits, '.', '_'), prefixing if needed.</summary>
    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_').ToArray();
        var s = new string(chars);
        return s.Length == 0 ? "x" : s;
    }

    /// <summary>Hands out unique, WiX-legal element Ids (a 72-char limit-safe truncation plus a counter).</summary>
    private sealed class IdAllocator
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private int _counter;

        public string Next(string baseId)
        {
            if (baseId.Length > 60) baseId = baseId[..60];
            if (baseId.Length == 0 || !char.IsLetter(baseId[0]) && baseId[0] != '_')
                baseId = "_" + baseId;
            var id = baseId;
            while (!_used.Add(id))
                id = $"{baseId}_{_counter++}";
            return id;
        }
    }

    /// <summary>
    /// Generates a deterministic GUID from a seed string using a simple hash-based approach.
    /// This ensures the same project always gets the same GUIDs across builds.
    /// </summary>
    private static string GenerateDeterministicGuid(string seed, string purpose)
    {
        var input = $"ryn:{seed}:{purpose}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        // Use first 16 bytes of SHA256 to form a GUID
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        return new Guid(guidBytes).ToString("D");
    }

    private static int CreateLinuxBundle(string projectDir, string projectName, string publishDir, ReadOnlySpan<string> args)
    {
        var bundleDir = Path.Combine(projectDir, "bin", "bundle", projectName + ".AppDir");

        if (Directory.Exists(bundleDir))
            Directory.Delete(bundleDir, recursive: true);

        var usrBin = Path.Combine(bundleDir, "usr", "bin");
        var usrShare = Path.Combine(bundleDir, "usr", "share", "applications");
        var usrShareIcons = Path.Combine(bundleDir, "usr", "share", "icons", "hicolor", "256x256", "apps");
        var usrLib = Path.Combine(bundleDir, "usr", "lib");

        Directory.CreateDirectory(usrBin);
        Directory.CreateDirectory(usrShare);
        Directory.CreateDirectory(usrShareIcons);
        Directory.CreateDirectory(usrLib);

        // Read ryn.json + CLI args for bundle metadata
        var bundleVersion = GetArgValue(args, "--version") ?? "1.0.0";
        var iconPath = GetArgValue(args, "--icon");
        var categories = "Utility;";
        string? comment = null;

        var rynJsonPath = Path.Combine(projectDir, "ryn.json");
        if (File.Exists(rynJsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(rynJsonPath));
                if (doc.RootElement.TryGetProperty("bundle", out var bundle))
                {
                    if (bundle.TryGetProperty("version", out var ver))
                        bundleVersion = ver.GetString() ?? bundleVersion;
                    if (iconPath is null && bundle.TryGetProperty("icon", out var ico))
                        iconPath = ico.GetString();
                    if (bundle.TryGetProperty("categories", out var cat))
                        categories = cat.GetString() ?? categories;
                    if (bundle.TryGetProperty("comment", out var cmt))
                        comment = cmt.GetString();
                }
            }
            catch (JsonException) { /* use defaults */ }
            catch (IOException) { /* use defaults */ }
        }

        // Linux freedesktop Categories must end with a trailing ';' per the menu spec.
        if (!string.IsNullOrEmpty(categories) && !categories.EndsWith(';'))
            categories += ";";

        // The AppImage filename and the appimagetool ARCH env var must match the host's `uname -m`, which is
        // derived from the target RID (we already reject cross-RID bundling above).
        var appImageArch = AppImageArch();

        // Copy published output
        foreach (var file in Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(publishDir, file);
            var destPath = Path.Combine(usrBin, relativePath);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir is not null) Directory.CreateDirectory(destDir);
            File.Copy(file, destPath, overwrite: true);
        }

        // Copy wwwroot if present
        var wwwrootDir = Path.Combine(projectDir, "wwwroot");
        if (Directory.Exists(wwwrootDir))
        {
            var destWwwroot = Path.Combine(usrBin, "wwwroot");
            foreach (var file in Directory.GetFiles(wwwrootDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(wwwrootDir, file);
                var destPath = Path.Combine(destWwwroot, relativePath);
                var destDir = Path.GetDirectoryName(destPath);
                if (destDir is not null) Directory.CreateDirectory(destDir);
                File.Copy(file, destPath, overwrite: true);
            }
        }

        // Copy icon if specified
#pragma warning disable CA1308 // Desktop entry and icon filenames use lowercase by convention
        var lowerName = projectName.ToLowerInvariant();
#pragma warning restore CA1308
        var iconLine = "";
        // Use the app's icon, or fall back to the bundled Ryn default (PNG, native for hicolor/AppImage).
        var resolvedIcon = ResolveIconSource(projectDir, iconPath);
        var usingDefaultIcon = false;
        if (resolvedIcon is null)
        {
            resolvedIcon = ExtractEmbeddedIcon("Ryn.Cli.ryn-icon.png", ".png");
            usingDefaultIcon = resolvedIcon is not null;
        }
        if (resolvedIcon is not null && File.Exists(resolvedIcon))
        {
            var ext = Path.GetExtension(resolvedIcon);
            // Copy to AppDir root (required by AppImage spec)
            File.Copy(resolvedIcon, Path.Combine(bundleDir, $"{lowerName}{ext}"), overwrite: true);
            // Also copy to hicolor icons for desktop integration
            File.Copy(resolvedIcon, Path.Combine(usrShareIcons, $"{lowerName}{ext}"), overwrite: true);

            iconLine = $"\nIcon={lowerName}";
            Console.WriteLine(usingDefaultIcon ? "  Icon: bundled Ryn default" : $"  Icon: {Path.GetFileName(resolvedIcon)}");
        }

        // Generate a spec-compliant .desktop entry. Exec is the bare executable name (AppRun prepends
        // usr/bin to PATH), and the Version key is the *Desktop Entry spec* version (1.0), not the app
        // version — the app version lives in CFBundleVersion-equivalent metadata, not here.
        var commentLine = comment is not null ? $"\nComment={comment}" : "";
        var desktopEntry = $"""
            [Desktop Entry]
            Type=Application
            Version=1.0
            Name={projectName}
            Exec={projectName}
            Categories={categories}{iconLine}{commentLine}
            Terminal=false
            """;
        File.WriteAllText(Path.Combine(bundleDir, $"{lowerName}.desktop"), desktopEntry);

        // Also place .desktop in usr/share/applications
        File.WriteAllText(Path.Combine(usrShare, $"{lowerName}.desktop"), desktopEntry);

        // Generate AppRun with proper environment setup
        var appRun = $$"""
            #!/bin/sh
            HERE="$(dirname "$(readlink -f "$0")")"
            export PATH="$HERE/usr/bin:$PATH"
            export LD_LIBRARY_PATH="$HERE/usr/lib:$LD_LIBRARY_PATH"
            export XDG_DATA_DIRS="$HERE/usr/share:$XDG_DATA_DIRS"
            export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${DOTNET_BUNDLE_EXTRACT_BASE_DIR:-$HOME/.cache/{{lowerName}}}"
            exec "$HERE/usr/bin/{{projectName}}" "$@"
            """;
        var appRunPath = Path.Combine(bundleDir, "AppRun");
        File.WriteAllText(appRunPath, appRun);

        SetExecutable(appRunPath);

        var execPath = Path.Combine(usrBin, projectName);
        if (File.Exists(execPath))
            SetExecutable(execPath);

        // Generate build-appimage.sh helper script
        var buildScript = $"""
            #!/bin/sh
            set -e
            SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
            APPDIR="$SCRIPT_DIR/{projectName}.AppDir"
            OUTPUT="$SCRIPT_DIR/{projectName}-{bundleVersion}-{appImageArch}.AppImage"

            if ! command -v appimagetool >/dev/null 2>&1; then
                echo "appimagetool not found in PATH. Downloading..."
                ARCH="$(uname -m)"
                TOOL_URL="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$ARCH.AppImage"
                TOOL_PATH="$SCRIPT_DIR/appimagetool"
                if command -v curl >/dev/null 2>&1; then
                    curl -fSL "$TOOL_URL" -o "$TOOL_PATH"
                elif command -v wget >/dev/null 2>&1; then
                    wget -q "$TOOL_URL" -O "$TOOL_PATH"
                else
                    echo "Error: curl or wget required to download appimagetool"
                    exit 1
                fi
                chmod +x "$TOOL_PATH"
                APPIMAGETOOL="$TOOL_PATH"
            else
                APPIMAGETOOL="appimagetool"
            fi

            echo "Building AppImage..."
            ARCH="$(uname -m)" "$APPIMAGETOOL" "$APPDIR" "$OUTPUT"
            echo ""
            echo "AppImage created: $OUTPUT"
            """;
        var buildScriptPath = Path.Combine(projectDir, "bin", "bundle", "build-appimage.sh");
        File.WriteAllText(buildScriptPath, buildScript);
        SetExecutable(buildScriptPath);

        // Try to build AppImage directly if appimagetool is available
        var builtAppImage = false;
        var appImagePath = Path.Combine(projectDir, "bin", "bundle", $"{projectName}-{bundleVersion}-{appImageArch}.AppImage");
        if (TryFindInPath("appimagetool"))
        {
            Console.WriteLine("  Found appimagetool, building AppImage...");
            var psi = new ProcessStartInfo
            {
                FileName = "appimagetool",
                UseShellExecute = false,
                WorkingDirectory = Path.Combine(projectDir, "bin", "bundle"),
            };
            psi.ArgumentList.Add(bundleDir);
            psi.ArgumentList.Add(appImagePath);
            psi.Environment["ARCH"] = appImageArch;

            var appImageProcess = Process.Start(psi);
            appImageProcess?.WaitForExit();
            builtAppImage = appImageProcess?.ExitCode == 0;
        }

        Console.WriteLine();
        Console.WriteLine($"  Linux AppDir created: {bundleDir}");
        if (builtAppImage)
        {
            Console.WriteLine($"  AppImage created: {appImagePath}");
        }
        else
        {
            Console.WriteLine($"  Run with: {appRunPath}");
            Console.WriteLine($"  To create AppImage: bash {buildScriptPath}");
        }
        return 0;
    }

    /// <summary>Maps the host process architecture to the `uname -m`-style label appimagetool expects.</summary>
    private static string AppImageArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "i686",
        Architecture.Arm64 => "aarch64",
        Architecture.Arm => "armhf",
        _ => "x86_64",
    };

    private static void SetExecutable(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "chmod",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("+x");
        psi.ArgumentList.Add(path);
        Process.Start(psi)?.WaitForExit();
    }

    private static bool TryFindInPath(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "which",
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add(command);

        try
        {
            var process = Process.Start(psi);
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string BuildPublishArgs(ReadOnlySpan<string> args)
    {
        var sb = new System.Text.StringBuilder("publish -c Release --nologo");

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--rid" && i + 1 < args.Length)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $" -r {args[i + 1]}");
                i++;
            }
            else if (args[i] == "--aot")
            {
                sb.Append(" -p:PublishAot=true");
            }
            else if (args[i] == "--self-contained")
            {
                sb.Append(" --self-contained");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves the directory the just-completed publish wrote to, deterministically, by asking MSBuild for the
    /// evaluated <c>PublishDir</c> with the exact same arguments used for the build (so RID, configuration and
    /// TFM all match what was produced). Returns null only if MSBuild reports nothing and no directory can be
    /// found, so a stale RID-named directory is never silently bundled.
    /// </summary>
    private static string? ResolvePublishDir(string dotnet, string projectDir, string publishArgs)
    {
        var queried = QueryPublishDir(dotnet, projectDir, publishArgs);
        if (queried is not null)
        {
            var resolved = Path.IsPathRooted(queried) ? queried : Path.Combine(projectDir, queried);
            if (Directory.Exists(resolved))
                return resolved;
        }

        // MSBuild query failed (older SDK, evaluation error) — fall back to the legacy guess so behavior never
        // regresses for the common case, but only return a path that actually exists.
        var releaseDir = Path.Combine(projectDir, "bin", "Release", "net10.0");
        var ridPath = Path.Combine(releaseDir, RuntimeInformation.RuntimeIdentifier, "publish");
        if (Directory.Exists(ridPath))
            return ridPath;
        var nonRid = Path.Combine(releaseDir, "publish");
        return Directory.Exists(nonRid) ? nonRid : null;
    }

    /// <summary>
    /// Runs `dotnet publish ... --getProperty:PublishDir` (a fast evaluation, no rebuild) and returns the
    /// evaluated relative/absolute PublishDir as MSBuild reports it, or null if the query failed.
    /// </summary>
    private static string? QueryPublishDir(string dotnet, string projectDir, string publishArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                Arguments = publishArgs + " --getProperty:PublishDir",
                WorkingDirectory = projectDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = Process.Start(psi);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) return null;

            // --getProperty prints the property value; take the last non-empty line to skip any preamble.
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length == 0 ? null : lines[^1];
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
