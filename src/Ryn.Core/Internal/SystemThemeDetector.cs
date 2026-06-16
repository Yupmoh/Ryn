using System.Diagnostics;
using System.Text;

namespace Ryn.Core.Internal;

/// <summary>
/// Polls the OS for its light/dark preference by shelling out to a per-platform probe
/// (<c>defaults</c> / <c>reg</c> / <c>gsettings</c>). The probe is a child process, so each tick is
/// bounded: stdout and stderr are both drained asynchronously and the process is killed if it
/// overruns <see cref="ProbeTimeout"/>, so a hung or chatty probe can never leak a handle or wedge
/// the pipe buffer. Native OS change-notification subscriptions are deliberately not used here —
/// that is roadmap work (see ARC-18/PAP-11); this type stays a leak-free, low-frequency poller.
/// </summary>
internal sealed class SystemThemeDetector : IDisposable
{
    /// <summary>
    /// Default poll cadence. Theme changes are rare and user-initiated, so a leisurely interval keeps
    /// the per-tick child-process spawn off the hot path while still feeling responsive.
    /// </summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Lower bound applied to any requested interval to stop pathologically aggressive polling.</summary>
    internal static readonly TimeSpan MinimumPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a single probe may run before it is killed and the prior theme is kept.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private Timer? _timer;
    private volatile AppTheme _current;
    private volatile bool _disposed;

    internal event Action<AppTheme>? ThemeChanged;

    internal AppTheme Current => _current;

    internal SystemThemeDetector()
    {
        _current = Detect();
    }

    /// <summary>Starts polling at the default cadence.</summary>
    internal void StartPolling() => StartPolling(DefaultPollInterval);

    /// <summary>
    /// Starts polling at <paramref name="interval"/> (clamped to <see cref="MinimumPollInterval"/>).
    /// Calling more than once is a no-op after the first start; calling after disposal is a no-op.
    /// </summary>
    internal void StartPolling(TimeSpan interval)
    {
        if (_disposed || _timer is not null) return;

        if (interval < MinimumPollInterval) interval = MinimumPollInterval;

        // Pass a WeakReference, not `this`, as the timer state. A Timer roots its state for as long as it
        // is alive, so closing over `this` would make the detector self-rooting and its finalizer
        // unreachable — defeating the ARC-18 backstop. With a weak reference the detector can be collected
        // when the owner drops it without disposing; the finalizer then stops the timer.
        var weakSelf = new WeakReference<SystemThemeDetector>(this);
        _timer = new Timer(static state =>
        {
            var weak = (WeakReference<SystemThemeDetector>)state!;
            if (!weak.TryGetTarget(out var self) || self._disposed) return;

            var theme = Detect();
            // Re-check after the (blocking) probe: Dispose may have raced in while it ran, and we must
            // not raise events for a detector the owner has already torn down.
            if (self._disposed) return;

            if (theme != self._current)
            {
                self._current = theme;
                self.ThemeChanged?.Invoke(theme);
            }
        }, weakSelf, interval, interval);
    }

    internal static AppTheme Detect()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return DetectMacOs();
            if (OperatingSystem.IsWindows())
                return DetectWindows();
            if (OperatingSystem.IsLinux())
                return DetectLinux();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        return AppTheme.Light;
    }

    private static AppTheme DetectMacOs()
    {
        var output = RunProbe("defaults", static args =>
        {
            args.Add("read");
            args.Add("-g");
            args.Add("AppleInterfaceStyle");
        });
        return output.Trim().Equals("Dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark : AppTheme.Light;
    }

    private static AppTheme DetectWindows()
    {
        var output = RunProbe("reg", static args =>
        {
            args.Add("query");
            args.Add(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            args.Add("/v");
            args.Add("AppsUseLightTheme");
        });
        // The value is REG_DWORD 0 for dark, 1 for light; `reg query` prints it as 0x0 / 0x1.
        return output.Contains("0x0", StringComparison.Ordinal) ? AppTheme.Dark : AppTheme.Light;
    }

    private static AppTheme DetectLinux()
    {
        var output = RunProbe("gsettings", static args =>
        {
            args.Add("get");
            args.Add("org.gnome.desktop.interface");
            args.Add("color-scheme");
        });
        return output.Contains("dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark : AppTheme.Light;
    }

    /// <summary>
    /// Runs a short-lived probe and returns its stdout. Both stdout and stderr are drained on
    /// background read threads so a verbose probe cannot fill (and deadlock on) a pipe buffer, and the
    /// child is force-killed if it overruns <see cref="ProbeTimeout"/> rather than being abandoned.
    /// Returns an empty string on any failure, which the callers treat as the Light fallback.
    /// </summary>
    private static string RunProbe(string fileName, Action<System.Collections.ObjectModel.Collection<string>> configureArgs)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        configureArgs(psi.ArgumentList);

        using var process = Process.Start(psi);
        if (process is null) return string.Empty;

        var stdout = new StringBuilder();
        // Drain stderr too — RedirectStandardError without a reader lets a chatty probe block once the OS
        // pipe buffer fills, hanging the child and (before the kill below) leaking it. We discard the text
        // but must keep reading it.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
        {
            // Overran its budget: kill the whole tree so we never abandon a wedged child. WaitForExit()
            // after Kill lets the async readers flush and the handle be reclaimed by the using-dispose.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited between the wait and the kill */ }
            catch (System.ComponentModel.Win32Exception) { /* race with OS teardown */ }
            try { process.WaitForExit(500); }
            catch (InvalidOperationException) { }
            return string.Empty;
        }

        // Flush any buffered async-read callbacks now that the process has exited.
        process.WaitForExit();
        return stdout.ToString();
    }

    public void Dispose()
    {
        StopTimer();
        GC.SuppressFinalize(this);
    }

    private void StopTimer()
    {
        if (_disposed) return;
        _disposed = true;
        // System.Threading.Timer.Dispose only touches its own handle, so this is safe from both the public
        // Dispose and the finalizer, and it stops the polling callbacks promptly.
        _timer?.Dispose();
        _timer = null;
    }

    // Backstop for ARC-18: the owning RynWindow disposes this on teardown, but a missed Dispose (e.g. an
    // early-failed startup that never reaches the owner's dispose path) must not leave the 5s timer — and
    // its per-tick child-process spawns — running for the rest of the process. Stopping the timer here ends
    // the callbacks.
    ~SystemThemeDetector() => StopTimer();
}
