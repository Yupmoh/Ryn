using Ryn.Core;
using Ryn.Plugins.Audio.Backends;

namespace Ryn.Plugins.Audio;

public sealed class AudioService : IDisposable
{
    private readonly IAudioBackend _backend;
    private bool _disposed;

    internal AudioService(IMainThreadDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (OperatingSystem.IsWindows())
            _backend = new WindowsAudioBackend();
        else if (OperatingSystem.IsMacOS())
            // macOS NSSound is an AppKit type; the backend marshals every call onto the UI thread (INT-02).
            _backend = new MacOsAudioBackend(dispatcher);
        else if (OperatingSystem.IsLinux())
            _backend = new LinuxAudioBackend();
        else
            _backend = new StubAudioBackend();
    }

    public void Play(string path, int volume = 100, bool loop = false) =>
        _backend.Play(path, Math.Clamp(volume, 0, 100), loop);
    public void PlaySystem(string name) => _backend.PlaySystem(name);
    public void Stop() => _backend.Stop();
    public void SetVolume(int percent) => _backend.SetVolume(Math.Clamp(percent, 0, 100));
    public bool IsPlaying() => _backend.IsPlaying();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.Dispose();
    }
}
