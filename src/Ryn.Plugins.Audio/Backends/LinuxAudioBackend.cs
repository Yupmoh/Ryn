using System.Diagnostics;
using System.Runtime.Versioning;

namespace Ryn.Plugins.Audio.Backends;

[SupportedOSPlatform("linux")]
internal sealed class LinuxAudioBackend : IAudioBackend
{
    // paplay's native --volume is a linear scale where 65536 == 100% (PA_VOLUME_NORM).
    private const int PaVolumeNorm = 65536;

    private Process? _currentProcess;
    private readonly object _lock = new();
    private volatile bool _looping;
    private string? _loopPath;
    private int _loopVolume;
    private bool _disposed;

    public void Play(string path, int volume, bool loop)
    {
        Stop();

        _looping = loop;
        _loopPath = loop ? path : null;
        _loopVolume = volume;

        // PAP-07: do NOT change the user's global system volume (the old `pactl set-sink-volume @DEFAULT_SINK@`
        // permanently lowered the whole desktop's output and never restored it). Apply the requested level as
        // a per-stream volume on the paplay invocation instead, which dies with the stream. The aplay fallback
        // has no per-stream volume control, so it plays at full volume — documented on SetVolume.
        StartPlayback(path, volume);
    }

    private void StartPlayback(string path, int volume)
    {
        var usePaplay = IsToolAvailable("paplay");
        var tool = usePaplay ? "paplay" : "aplay";

        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        if (usePaplay && volume < 100)
        {
            // Map 0-100% to paplay's linear 0-65536 scale, clamped to PA_VOLUME_NORM (no amplification).
            var paVolume = Math.Clamp(volume * PaVolumeNorm / 100, 0, PaVolumeNorm);
            psi.ArgumentList.Add($"--volume={paVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        psi.ArgumentList.Add(path);

        try
        {
            var process = Process.Start(psi);
            if (process is not null)
            {
                lock (_lock)
                {
                    _currentProcess = process;
                }

                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (_currentProcess == process)
                            _currentProcess = null;
                    }
                    process.Dispose();

                    if (_looping && _loopPath is not null)
                        StartPlayback(_loopPath, _loopVolume);
                };
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void PlaySystem(string name)
    {
        Stop();

        // Use canberra-gtk-play for freedesktop sound theme names
        if (!IsToolAvailable("canberra-gtk-play")) return;

        var psi = new ProcessStartInfo("canberra-gtk-play")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(name);

        try
        {
            var process = Process.Start(psi);
            if (process is not null)
            {
                lock (_lock)
                {
                    _currentProcess = process;
                }

                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (_currentProcess == process)
                            _currentProcess = null;
                    }
                    process.Dispose();
                };
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Stop()
    {
        _looping = false;
        _loopPath = null;

        Process? process;
        lock (_lock)
        {
            process = _currentProcess;
            _currentProcess = null;
        }

        if (process is not null && !process.HasExited)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    public void SetVolume(int percent)
    {
        // No-op by design. Volume is applied per-stream at Play() time via paplay's --volume, so there is no
        // live handle to retarget here, and changing it mid-stream would require PulseAudio API bindings.
        // The aplay fallback has no per-stream volume at all. We deliberately do NOT touch the global system
        // sink (PAP-07) — that would alter the whole desktop's volume, not just this app's playback.
        _ = percent;
    }

    public bool IsPlaying()
    {
        lock (_lock)
        {
            return _currentProcess is not null && !_currentProcess.HasExited;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static bool IsToolAvailable(string tool)
    {
        var psi = new ProcessStartInfo("which")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(tool);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
