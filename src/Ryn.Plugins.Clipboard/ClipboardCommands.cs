using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Ipc;

namespace Ryn.Plugins.Clipboard;

public static class ClipboardCommands
{
    // Hard ceiling for any clipboard helper process. A well-behaved pbpaste/xclip/powershell
    // returns in milliseconds; a hang (e.g. a tool blocked on a full pipe or waiting for a
    // selection owner) is bounded here instead of wedging the IPC worker forever.
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    [RynCommand("clipboard.readText")]
    public static string ReadText()
    {
        if (OperatingSystem.IsMacOS())
            return MacClipboard.ReadText();

        if (OperatingSystem.IsLinux())
            return IsWayland()
                ? RunProcess("wl-paste", "--no-newline")
                : RunProcess("xclip", "-selection clipboard -o");

        if (OperatingSystem.IsWindows())
            return RunProcess("powershell", "-command Get-Clipboard");

        throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.writeText")]
    public static void WriteText(string text)
    {
        if (OperatingSystem.IsMacOS())
            MacClipboard.WriteText(text);
        else if (OperatingSystem.IsLinux())
        {
            if (IsWayland())
                RunProcessWithInput("wl-copy", "", text);
            else
                RunProcessWithInput("xclip", "-selection clipboard", text);
        }
        else if (OperatingSystem.IsWindows())
            // Pipe the text in via stdin so quotes, spaces, and newlines round-trip
            // faithfully instead of being mangled by inline -Value interpolation.
            RunProcessWithInput(
                "powershell",
                ["-NoProfile", "-Command", "$input | Set-Clipboard"],
                text);
        else
            throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.hasText")]
    public static bool HasText()
    {
        var text = ReadText();
        return !string.IsNullOrEmpty(text);
    }

    [RynCommand("clipboard.clear")]
    public static void Clear()
    {
        if (OperatingSystem.IsMacOS())
            MacClipboard.Clear();
        else if (OperatingSystem.IsLinux())
        {
            if (IsWayland())
                RunProcess("wl-copy", "--clear");
            else
                RunProcessWithInput("xclip", "-selection clipboard", "");
        }
        else if (OperatingSystem.IsWindows())
            RunProcess("powershell", "-command Set-Clipboard -Value $null");
        else
            throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.readImage")]
    public static string ReadImage()
    {
        if (OperatingSystem.IsMacOS())
            return ReadImageMacOS();

        if (OperatingSystem.IsLinux())
            return ReadImageLinux();

        if (OperatingSystem.IsWindows())
            return ReadImageWindows();

        throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.writeImage")]
    public static void WriteImage(string base64Png)
    {
        if (string.IsNullOrEmpty(base64Png))
            throw new ArgumentException("base64Png must not be null or empty.", nameof(base64Png));

        byte[] pngBytes = Convert.FromBase64String(base64Png);

        if (OperatingSystem.IsMacOS())
            WriteImageMacOS(pngBytes);
        else if (OperatingSystem.IsLinux())
            WriteImageLinux(pngBytes);
        else if (OperatingSystem.IsWindows())
            WriteImageWindows(pngBytes);
        else
            throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.hasImage")]
    public static bool HasImage()
    {
        if (OperatingSystem.IsMacOS())
            return HasImageMacOS();

        if (OperatingSystem.IsLinux())
            return HasImageLinux();

        if (OperatingSystem.IsWindows())
            return HasImageWindows();

        throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    // ── macOS image helpers ──────────────────────────────────────────────
    //
    // Image read/write still go through osascript: the PNG round-trip needs an
    // NSData<->NSBitmapImageRep dance that is far more code than the text path, and
    // the existing shell route is correct. Porting image ops to native NSPasteboard is
    // tracked as future work (see the PAP-24 roadmap note); the text path below is the
    // one that ran a process per keystroke-grade operation.

    private static string ReadImageMacOS()
    {
        EnsureToolExists("osascript");

        const string script =
            "ObjC.import('AppKit');" +
            "var pb = $.NSPasteboard.generalPasteboard;" +
            "var data = pb.dataForType($.NSPasteboardTypePNG);" +
            "if (!data.isNil()) { data.base64EncodedStringWithOptions(0).js; } else { ''; }";

        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("JavaScript");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start process 'osascript'.");

        var (output, _, timedOut) = DrainAndWait(process);
        if (timedOut)
            throw new InvalidOperationException("Process 'osascript' timed out.");

        // osascript may exit non-zero if no image — return empty
        return process.ExitCode != 0 ? "" : output.Trim();
    }

    private static void WriteImageMacOS(byte[] pngBytes)
    {
        EnsureToolExists("osascript");

        var tempPath = Path.Combine(Path.GetTempPath(), $"ryn_clip_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(tempPath, pngBytes);

            var script = $"set the clipboard to (read (POSIX file \"{tempPath}\") as «class PNGf»)";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("Failed to start process 'osascript'.");

            var (_, error, timedOut) = DrainAndWait(process);
            if (timedOut)
                throw new InvalidOperationException("Process 'osascript' timed out.");

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Process 'osascript' exited with code {process.ExitCode}: {error}".TrimEnd());
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool HasImageMacOS()
    {
        EnsureToolExists("osascript");

        const string script =
            "ObjC.import('AppKit');" +
            "var pb = $.NSPasteboard.generalPasteboard;" +
            "var data = pb.dataForType($.NSPasteboardTypePNG);" +
            "!data.isNil()";

        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("JavaScript");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start process 'osascript'.");

        var (output, _, timedOut) = DrainAndWait(process);
        if (timedOut)
            return false;

        return process.ExitCode == 0
            && string.Equals(output.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    // ── Linux image helpers ──────────────────────────────────────────────

    // True when running under a Wayland session (prefer wl-clipboard; xclip is X11-only and fails there).
    private static bool IsWayland() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string ReadImageLinux()
    {
        if (IsWayland())
        {
            EnsureToolExists("wl-paste");
            var wlBytes = RunProcessWithBinaryOutput("wl-paste", ["--type", "image/png"]);
            return wlBytes.Length == 0 ? "" : Convert.ToBase64String(wlBytes);
        }

        EnsureToolExists("xclip");
        byte[] pngBytes = RunProcessWithBinaryOutput("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]);
        return pngBytes.Length == 0 ? "" : Convert.ToBase64String(pngBytes);
    }

    private static void WriteImageLinux(byte[] pngBytes)
    {
        if (IsWayland())
        {
            EnsureToolExists("wl-copy");
            RunProcessWithBinaryInput("wl-copy", ["--type", "image/png"], pngBytes);
            return;
        }

        EnsureToolExists("xclip");
        RunProcessWithBinaryInput("xclip", ["-selection", "clipboard", "-t", "image/png"], pngBytes);
    }

    private static bool HasImageLinux()
    {
        try
        {
            if (IsWayland())
            {
                EnsureToolExists("wl-paste");
                var types = RunProcess("wl-paste", "--list-types");
                return types.Contains("image/png", StringComparison.Ordinal);
            }

            EnsureToolExists("xclip");
            var output = RunProcess("xclip", "-selection clipboard -t TARGETS -o");
            return output.Contains("image/png", StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ── Windows image helpers ────────────────────────────────────────────

    private static string ReadImageWindows()
    {
        const string script =
            "Add-Type -AssemblyName System.Windows.Forms;" +
            "Add-Type -AssemblyName System.Drawing;" +
            "$img = [System.Windows.Forms.Clipboard]::GetImage();" +
            "if ($img) {" +
            "  $ms = New-Object System.IO.MemoryStream;" +
            "  $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png);" +
            "  [Convert]::ToBase64String($ms.ToArray())" +
            "}";

        return RunProcess("powershell", $"-command {script}").Trim();
    }

    private static void WriteImageWindows(byte[] pngBytes)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ryn_clip_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(tempPath, pngBytes);

            var script =
                "Add-Type -AssemblyName System.Windows.Forms;" +
                "Add-Type -AssemblyName System.Drawing;" +
                $"$img = [System.Drawing.Image]::FromFile('{tempPath.Replace("'", "''", StringComparison.Ordinal)}');" +
                "[System.Windows.Forms.Clipboard]::SetImage($img)";

            RunProcess("powershell", $"-command {script}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool HasImageWindows()
    {
        const string script =
            "Add-Type -AssemblyName System.Windows.Forms;" +
            "[System.Windows.Forms.Clipboard]::ContainsImage()";

        var output = RunProcess("powershell", $"-command {script}").Trim();
        return string.Equals(output, "True", StringComparison.OrdinalIgnoreCase);
    }

    // ── Process helpers ──────────────────────────────────────────────────
    //
    // Every redirected child has BOTH stdout and stderr drained concurrently and is bounded
    // by ProcessTimeout. Reading one pipe to EOF before the other (the old shape) deadlocks
    // when the child fills the un-read pipe's OS buffer (~64KB): the child blocks on its
    // write, we block on the wrong read, neither side moves. DrainAndWait awaits both reads
    // before WaitForExit and kills the child on timeout so a stuck tool can't wedge the
    // IPC worker.

    private static (string StdOut, string StdErr, bool TimedOut) DrainAndWait(Process process)
    {
        // Start both reads before awaiting either — concurrent drain is what avoids the
        // two-pipe deadlock. Each task may be null if that stream wasn't redirected.
        var stdoutTask = process.StartInfo.RedirectStandardOutput
            ? process.StandardOutput.ReadToEndAsync()
            : null;
        var stderrTask = process.StartInfo.RedirectStandardError
            ? process.StandardError.ReadToEndAsync()
            : null;

        var exited = process.WaitForExit((int)ProcessTimeout.TotalMilliseconds);
        if (!exited)
        {
            TryKill(process);
            return (string.Empty, string.Empty, true);
        }

        // The overload above only waits for the process object; the async pipe reads still
        // need to complete (and the parameterless WaitForExit flushes any buffered output).
        process.WaitForExit();

        var stdout = stdoutTask is null ? string.Empty : stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask is null ? string.Empty : stderrTask.GetAwaiter().GetResult();
        return (stdout, stderr, false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already exited between the check and the kill */ }
        catch (System.ComponentModel.Win32Exception) { /* kill not permitted / race — nothing more we can do */ }
    }

    private static string RunProcess(string fileName, string arguments)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var (output, error, timedOut) = DrainAndWait(process);
        if (timedOut)
            throw new InvalidOperationException(
                $"Process '{fileName}' timed out after {ProcessTimeout.TotalSeconds:0} seconds and was terminated.");

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Process '{fileName}' exited with code {process.ExitCode}: {error}".TrimEnd());

        return output;
    }

    private static void RunProcessWithInput(string fileName, string arguments, string input)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        RunWithStdin(psi, fileName, process =>
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        });
    }

    private static void RunProcessWithInput(string fileName, string[] arguments, string input)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        RunWithStdin(psi, fileName, process =>
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        });
    }

    private static byte[] RunProcessWithBinaryOutput(string fileName, string[] arguments)
    {
        EnsureToolExists(fileName);

        // Only stdout is redirected. The previous code redirected stderr too but never read it,
        // which is precisely the two-pipe deadlock PAP-13 is about: if the child filled the
        // un-read stderr buffer it would block forever. Leaving stderr inherited means there is a
        // single pipe to drain, so no deadlock is possible. A watchdog bounds the copy so a tool
        // that never closes stdout (e.g. a stuck selection owner) can't wedge the IPC worker.
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        using var ms = new MemoryStream();
        using var watchdog = new Timer(_ => TryKill(process), null, ProcessTimeout, Timeout.InfiniteTimeSpan);

        // Synchronous copy: drains the single stdout pipe to EOF. If the watchdog fires first it
        // kills the child, the pipe hits EOF, and CopyTo returns — then ExitCode is non-zero.
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();

        // Return empty if the process failed or was killed on timeout (e.g. no image on clipboard).
        return process.ExitCode != 0 ? [] : ms.ToArray();
    }

    private static void RunProcessWithBinaryInput(string fileName, string[] arguments, byte[] input)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        RunWithStdin(psi, fileName, process =>
        {
            process.StandardInput.BaseStream.Write(input, 0, input.Length);
            process.StandardInput.Close();
        });
    }

    // Shared driver for the "write stdin then read to completion" helpers: write the input,
    // then drain stdout+stderr concurrently under the timeout. Throws on non-zero exit so the
    // text/binary write paths keep their original failure contract.
    private static void RunWithStdin(ProcessStartInfo psi, string fileName, Action<Process> writeInput)
    {
        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        writeInput(process);

        var (_, error, timedOut) = DrainAndWait(process);
        if (timedOut)
            throw new InvalidOperationException(
                $"Process '{fileName}' timed out after {ProcessTimeout.TotalSeconds:0} seconds and was terminated.");

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Process '{fileName}' exited with code {process.ExitCode}: {error}".TrimEnd());
    }

    private static void EnsureToolExists(string tool)
    {
        // powershell is always available on Windows; skip the 'which' check
        if (string.Equals(tool, "powershell", StringComparison.OrdinalIgnoreCase))
            return;

        var whichCommand = OperatingSystem.IsWindows() ? "where" : "which";

        var psi = new ProcessStartInfo
        {
            FileName = whichCommand,
            Arguments = tool,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return; // can't verify, proceed anyway

            // Drain stdout (which/where prints the resolved path) so the child can't block on a
            // full pipe, and bound the wait so a hung resolver can't wedge us either.
            var drainTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                TryKill(process);
                return; // can't verify within the budget — proceed anyway
            }

            process.WaitForExit();
            _ = drainTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Required tool '{tool}' is not installed or not found in PATH.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 'which'/'where' itself not found — can't verify, proceed anyway
        }
    }
}

// ── Native macOS clipboard (PAP-24) ──────────────────────────────────────
//
// Text clipboard ops now talk to NSPasteboard through the ObjC runtime instead of spawning
// pbcopy/pbpaste per call. This removes a process launch (and the per-op which/EnsureToolExists
// check) from every read/write/clear while preserving the exact command contract:
//   • ReadText  — returns the pasteboard's NSPasteboardTypeString verbatim (no trim), exactly
//                 like `pbpaste`; nil/no-string-type → empty string.
//   • WriteText — clearContents → declareTypes:owner: → setString:forType:, committing the exact
//                 string to the pasteboard server (verified cross-process against `pbpaste`),
//                 preserving trailing newlines and embedded tabs just like piping into `pbcopy`.
//   • Clear     — clearContents.
// NSPasteboard's general pasteboard is process-global and safe to touch from the IPC worker
// thread; none of these calls require AppKit main-thread marshaling. We bind via classic
// [DllImport] (not [LibraryImport]) because this project doesn't enable AllowUnsafeBlocks;
// DllImport is fully NativeAOT-compatible and needs no unsafe code here.
[SupportedOSPlatform("macos")]
internal static partial class MacClipboard
{
    // UTI value of NSPasteboardTypeString on modern macOS. Using the literal avoids a dlsym of
    // the AppKit global; verified to round-trip identically to the framework constant.
    private const string Utf8TextType = "public.utf8-plain-text";

    private static readonly nint NSStringClass = objc_getClass("NSString");
    private static readonly nint NSArrayClass = objc_getClass("NSArray");
    private static readonly nint NSPasteboardClass = ResolvePasteboardClass();

    private static readonly nint SelStringWithUTF8String = sel_registerName("stringWithUTF8String:");
    private static readonly nint SelUTF8String = sel_registerName("UTF8String");
    private static readonly nint SelArrayWithObject = sel_registerName("arrayWithObject:");
    private static readonly nint SelGeneralPasteboard = sel_registerName("generalPasteboard");
    private static readonly nint SelClearContents = sel_registerName("clearContents");
    private static readonly nint SelDeclareTypesOwner = sel_registerName("declareTypes:owner:");
    private static readonly nint SelSetStringForType = sel_registerName("setString:forType:");
    private static readonly nint SelStringForType = sel_registerName("stringForType:");

    internal static string ReadText()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = GeneralPasteboard();
            if (pasteboard == 0)
                return string.Empty;

            var typeStr = CreateNSString(Utf8TextType);
            var value = objc_msgSend_nint(pasteboard, SelStringForType, typeStr);
            // No string on the pasteboard (or only non-text content) → empty, matching pbpaste.
            return ReadNSString(value);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    internal static void WriteText(string text)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = GeneralPasteboard();
            if (pasteboard == 0)
                throw new InvalidOperationException("NSPasteboard generalPasteboard was unavailable.");

            var typeStr = CreateNSString(Utf8TextType);
            // declareTypes:owner: must run before setString:forType:, otherwise the set is
            // rejected (returns NO) and nothing reaches the pasteboard server.
            var types = objc_msgSend_nint(NSArrayClass, SelArrayWithObject, typeStr);
            objc_msgSend_nint(pasteboard, SelClearContents);
            objc_msgSend_nint_nint(pasteboard, SelDeclareTypesOwner, types, 0);

            var valueStr = CreateNSString(text);
            var ok = objc_msgSend_set(pasteboard, SelSetStringForType, valueStr, typeStr);
            if (!ok)
                throw new InvalidOperationException("NSPasteboard setString:forType: failed.");
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    internal static void Clear()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pasteboard = GeneralPasteboard();
            if (pasteboard != 0)
                objc_msgSend_nint(pasteboard, SelClearContents);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static nint GeneralPasteboard() =>
        NSPasteboardClass == 0 ? 0 : objc_msgSend_nint(NSPasteboardClass, SelGeneralPasteboard);

    // NSPasteboard lives in AppKit. The Ryn host has AppKit loaded once the webview exists, but
    // the clipboard plugin must not assume call order — load it (idempotently) so objc_getClass
    // resolves even if a command runs before the window is up.
    private static nint ResolvePasteboardClass()
    {
        var cls = objc_getClass("NSPasteboard");
        if (cls != 0)
            return cls;

        NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);
        return objc_getClass("NSPasteboard");
    }

    private static nint CreateNSString(string value) =>
        objc_msgSend_str(NSStringClass, SelStringWithUTF8String, value);

    private static string ReadNSString(nint nsstring)
    {
        if (nsstring == 0)
            return string.Empty;

        var utf8 = objc_msgSend_nint(nsstring, SelUTF8String);
        return utf8 == 0 ? string.Empty : Marshal.PtrToStringUTF8(utf8) ?? string.Empty;
    }

    // --- ObjC runtime P/Invoke (DllImport: no AllowUnsafeBlocks required) ---

    // CharSet.Ansi + BestFitMapping/ThrowOnUnmappableChar satisfy CA2101; the per-parameter
    // [MarshalAs(LPUTF8Str)] is what actually controls marshaling, so the string is still passed
    // as UTF-8 (verified to round-trip non-ASCII). [LibraryImport] would be cleaner but requires
    // AllowUnsafeBlocks, which this project doesn't enable — DllImport is equally NativeAOT-safe.
    [DllImport("libobjc.dylib", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libobjc.dylib", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport("libobjc.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint objc_msgSend_nint(nint receiver, nint selector);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint objc_msgSend_nint(nint receiver, nint selector, nint arg1);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint objc_msgSend_nint_nint(nint receiver, nint selector, nint arg1, nint arg2);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint objc_msgSend_str(
        nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg1);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_set(nint receiver, nint selector, nint arg1, nint arg2);
}
