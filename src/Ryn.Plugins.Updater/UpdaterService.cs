using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ryn.Core;

namespace Ryn.Plugins.Updater;

public sealed class UpdaterService : IDisposable
{
    // Manual-redirect cap. GitHub's release-asset path is one hop (api/github.com -> objects.githubusercontent.com);
    // a small bound stops a hostile or looping endpoint from bouncing us forever while still allowing real chains.
    private const int MaxRedirects = 5;

    private readonly UpdaterOptions _options;
    private readonly IRynApplicationLifetime? _lifetime;
    private readonly SocketsHttpHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, VerifiedDownload> _verifiedDownloads = new(StringComparer.Ordinal);
    private bool _disposed;

    // DI activates this with the lifetime injected; the tests construct it with options only (lifetime null →
    // Apply falls back to a hard exit, which is the correct behaviour when there is no host loop to unwind).
    internal UpdaterService(UpdaterOptions options, IRynApplicationLifetime? lifetime = null)
    {
        _options = options;
        _lifetime = lifetime;

        // Redirects are followed manually (SUP-06): auto-redirect would silently land an asset download on a
        // host that was never checked against AllowedDownloadHosts. With it off we re-validate every Location
        // hop ourselves before following it. The handler is owned by this service and disposed in Dispose()
        // (disposeHandler:false), giving deterministic ownership.
        _httpHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
        _httpClient = new HttpClient(_httpHandler, disposeHandler: false)
        {
            // Bounds every request so the startup check (PAP-05) can't hang the app on a slow/unreachable host.
            Timeout = _options.HttpTimeout > TimeSpan.Zero ? _options.HttpTimeout : TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Ryn-Updater/1.0");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public async ValueTask<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.GitHubOwner) || string.IsNullOrEmpty(_options.GitHubRepo))
            throw new InvalidOperationException("GitHubOwner and GitHubRepo must be configured.");

        var url = $"https://api.github.com/repos/{_options.GitHubOwner}/{_options.GitHubRepo}/releases/latest";

        // Auto-redirect is disabled on the handler (SUP-06), so follow the API's own redirects manually here,
        // constrained to GitHub API hosts (the download allowlist governs asset hosts, not this metadata call).
        var requestUri = new Uri(url);
        using var response = await GetFollowingApiRedirectsAsync(requestUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize(json, UpdaterJsonContext.Default.GitHubRelease);

        if (release is null)
            return null;

        var remoteVersion = SemVer.Parse(release.TagName);
        if (remoteVersion is null)
            return null;

        // Stable-only by default: a prerelease tag is only an eligible update when the app opted in. This keeps
        // the default channel on finals while still letting opted-in builds move prerelease→prerelease.
        if (remoteVersion.IsPrerelease && !_options.AllowPrerelease)
            return null;

        // Downgrade protection: the effective current version is the max of the configured/assembly
        // version and the persisted monotonic floor. An unknown current version is NOT "accept anything".
        // Comparison is full SemVer including prerelease ordering, so 1.2.0-rc.1 < 1.2.0-rc.2 < 1.2.0 and a
        // release always outranks its own prereleases.
        var effectiveCurrent = GetEffectiveCurrentVersion();
        if (effectiveCurrent is not null && remoteVersion.CompareTo(effectiveCurrent) <= 0)
            return null;

        var asset = FindMatchingAsset(release);
        if (asset?.BrowserDownloadUrl is null)
            return null;

        var assetUri = ValidateDownloadUri(asset.BrowserDownloadUrl);

        // Locate the detached signature asset (assetName + suffix).
        var sigName = asset.Name + _options.SignatureAssetSuffix;
        var sigAsset = Array.Find(release.Assets ?? [], a =>
            string.Equals(a.Name, sigName, StringComparison.OrdinalIgnoreCase));
        var sigUri = sigAsset?.BrowserDownloadUrl is { } su ? ValidateDownloadUri(su) : null;

        return new UpdateInfo
        {
            Version = release.TagName,
            ReleaseUrl = new Uri(release.HtmlUrl ?? $"https://github.com/{_options.GitHubOwner}/{_options.GitHubRepo}/releases"),
            AssetUrl = assetUri,
            SignatureUrl = sigUri,
            ReleaseNotes = release.Body,
        };
    }

    /// <summary>
    /// Downloads the update, verifies its detached signature against the embedded public key, and — only
    /// if verification succeeds — registers it and returns an opaque handle. The raw file path is never
    /// exposed to or accepted from the frontend; <see cref="ApplyUpdateAsync"/> takes this handle.
    /// </summary>
    public async ValueTask<string> DownloadUpdateAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (string.IsNullOrEmpty(_options.PublicKey))
            throw new InvalidOperationException(
                "Updater.PublicKey is not configured. Refusing to download an update that cannot be cryptographically verified.");

        if (update.SignatureUrl is null)
            throw new SecurityException(
                $"No detached signature asset ('{_options.SignatureAssetSuffix}') was found for this release. Refusing to proceed.");

        ValidateDownloadUri(update.AssetUrl.AbsoluteUri);
        ValidateDownloadUri(update.SignatureUrl.AbsoluteUri);

        var extension = GetAssetExtension(update.AssetUrl.AbsoluteUri);
        var tempPath = Path.Combine(Path.GetTempPath(), $"ryn_update_{Guid.NewGuid():N}{extension}");

        try
        {
            await DownloadToFileAsync(update.AssetUrl, tempPath, progress, cancellationToken).ConfigureAwait(false);

            // Fetch + verify the detached signature BEFORE the file is ever eligible to run.
            // The signature is tiny; cap the read so a hostile endpoint can't stream gigabytes. Redirects are
            // followed manually so each hop's host is re-validated against the allowlist (SUP-06).
            using var sigResponse = await SendWithValidatedRedirectsAsync(
                update.SignatureUrl, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            sigResponse.EnsureSuccessStatusCode();
            if (sigResponse.Content.Headers.ContentLength is > 8192)
                throw new SecurityException("Update signature asset is implausibly large.");
            var sigBytes = await sigResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (sigBytes.Length > 8192)
                throw new SecurityException("Update signature asset is implausibly large.");
            var signatureBase64 = System.Text.Encoding.UTF8.GetString(sigBytes).Trim();
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(signatureBase64);
            }
            catch (FormatException ex)
            {
                throw new SecurityException("Update signature asset is not valid base64.", ex);
            }

            var data = await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (!UpdateSignature.Verify(data, signature, _options.PublicKey))
                throw new SecurityException("Update signature verification failed. The download is corrupt or untrusted.");

            var handle = Guid.NewGuid().ToString("N");
            _verifiedDownloads[handle] = new VerifiedDownload(tempPath, update.Version);
            return handle;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task DownloadToFileAsync(Uri uri, string tempPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await SendWithValidatedRedirectsAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        if (_options.MaxDownloadBytes > 0 && totalBytes > _options.MaxDownloadBytes)
            throw new SecurityException($"Update asset ({totalBytes} bytes) exceeds the maximum allowed size ({_options.MaxDownloadBytes} bytes).");

        var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var contentStreamDispose = contentStream.ConfigureAwait(false);
        var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await using var fileStreamDispose = fileStream.ConfigureAwait(false);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            totalRead += bytesRead;
            if (_options.MaxDownloadBytes > 0 && totalRead > _options.MaxDownloadBytes)
                throw new SecurityException($"Update asset exceeds the maximum allowed size ({_options.MaxDownloadBytes} bytes).");

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

            if (totalBytes > 0)
                progress?.Report((double)totalRead / totalBytes);
        }

        progress?.Report(1.0);
    }

    /// <summary>
    /// GETs <paramref name="uri"/> following 3xx redirects manually, re-validating every hop's <c>Location</c>
    /// against the host allowlist before following it (SUP-06). The <see cref="HttpClient"/> has automatic
    /// redirects disabled, so without this an asset download could be bounced onto an unchecked host. The first
    /// <paramref name="uri"/> is assumed already validated by the caller; each redirect target is validated here.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        Uri uri, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            // The request is disposed each iteration; once SendAsync returns, the response (and its body stream
            // for ResponseHeadersRead) no longer depends on the request object, so the escaping response is safe.
            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(HttpMethod.Get, current))
            {
                response = await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            }

            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            using (response)
            {
                if (location is null)
                    throw new SecurityException($"Update host '{current.Host}' returned a redirect with no Location header.");

                // Resolve relative redirects against the current URL, then re-run the full HTTPS + allowlist check.
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (hop >= MaxRedirects)
                    throw new SecurityException($"Update download exceeded the maximum of {MaxRedirects} redirects.");

                current = ValidateDownloadUri(next.AbsoluteUri);
            }
        }
    }

    /// <summary>
    /// GETs the GitHub release-metadata URL, following redirects manually (auto-redirect is off on the handler)
    /// but constraining every hop to a <c>github.com</c> host over HTTPS. Renamed-repo redirects stay within the
    /// API host, so this preserves the previous auto-follow behaviour without letting metadata be redirected to
    /// an arbitrary origin.
    /// </summary>
    private async Task<HttpResponseMessage> GetFollowingApiRedirectsAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(HttpMethod.Get, current))
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            }

            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            using (response)
            {
                if (location is null)
                    throw new SecurityException($"GitHub API host '{current.Host}' returned a redirect with no Location header.");
                if (hop >= MaxRedirects)
                    throw new SecurityException($"Update metadata request exceeded the maximum of {MaxRedirects} redirects.");

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!string.Equals(next.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException($"GitHub API redirect must use HTTPS (got '{next.Scheme}').");
                if (!next.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    && !next.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException($"GitHub API redirected to an unexpected host '{next.Host}'.");

                current = next;
            }
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode status) => status is
        System.Net.HttpStatusCode.MovedPermanently or
        System.Net.HttpStatusCode.Found or
        System.Net.HttpStatusCode.SeeOther or
        System.Net.HttpStatusCode.TemporaryRedirect or
        System.Net.HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// Applies a previously downloaded-and-verified update identified by the opaque handle returned from
    /// <see cref="DownloadUpdateAsync"/>. Rejects any handle not produced this session — the frontend can
    /// never point this at an arbitrary file.
    /// </summary>
    public ValueTask ApplyUpdateAsync(string downloadHandle, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(downloadHandle);

        if (!_verifiedDownloads.TryGetValue(downloadHandle, out var verified))
            throw new SecurityException("Unknown or unverified update handle. Download the update through the updater before applying.");

        if (!File.Exists(verified.Path))
            throw new FileNotFoundException("The verified update file is no longer present.", verified.Path);

        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine current executable path.");

        // Record the new version as the monotonic downgrade floor before relaunching.
        PersistVersionFloor(verified.Version);

        if (OperatingSystem.IsMacOS())
            ApplyMacOS(verified.Path, currentExePath);
        else if (OperatingSystem.IsWindows())
            ApplyWindows(verified.Path, currentExePath);
        else if (OperatingSystem.IsLinux())
            ApplyLinux(verified.Path, currentExePath);
        else
            throw new PlatformNotSupportedException("Auto-update is not supported on this platform.");

        // The detached relaunch script is now spawned (it backs up + swaps before re-opening). Stop THIS instance
        // through the lifecycle (PAP-06) so disposal runs — plugin/pty children, window-state save, server drain —
        // and the window closes before the script's relaunch, instead of hard-exiting from the IPC thread and
        // skipping all of that. When there is no host loop to unwind (e.g. unit tests) fall back to a hard exit.
        RequestShutdownOrExit();

        return ValueTask.CompletedTask;
    }

    private void RequestShutdownOrExit()
    {
        if (_lifetime is not null)
            _lifetime.RequestShutdown();
        else
            Environment.Exit(0);
    }

    // ---- platform apply (no shell-string interpolation; all paths passed as positional argv) ----

    private static void ApplyMacOS(string downloadPath, string currentExePath)
    {
        var appBundle = FindAppBundle(currentExePath);

        if (appBundle is not null)
        {
            var parentDir = Path.GetDirectoryName(appBundle)!;
            var appName = Path.GetFileName(appBundle);
            var backupPath = Path.Combine(parentDir, $".{appName}.bak");

            string newAppSource;
            string stagingToClean;
            if (downloadPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Extract in managed code, then locate the .app inside the staging directory.
                var staging = Path.Combine(Path.GetTempPath(), $"ryn_update_stage_{Guid.NewGuid():N}");
                System.IO.Compression.ZipFile.ExtractToDirectory(downloadPath, staging);
                newAppSource = Directory.EnumerateDirectories(staging, "*.app", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidOperationException("No .app bundle found inside the downloaded archive.");
                stagingToClean = staging;
            }
            else
            {
                newAppSource = downloadPath;
                stagingToClean = ""; // nothing extracted; $5 is a no-op rm of an empty path
            }

            // Constant script body; paths arrive as $1..$5 and are never interpolated into the script text.
            // The final rm clears the temp staging dir created for the zip case (it was previously leaked).
            const string script =
                "app=\"$1\"; backup=\"$2\"; src=\"$3\"; relaunch=\"$4\"; stage=\"$5\"; " +
                "rm -rf \"$backup\"; mv \"$app\" \"$backup\" && cp -R \"$src\" \"$app\" && open -n \"$relaunch\" && rm -rf \"$backup\"; " +
                "[ -n \"$stage\" ] && rm -rf \"$stage\"";
            LaunchDetachedShell(script, appBundle, backupPath, newAppSource, appBundle, stagingToClean);
        }
        else
        {
            ReplaceStandaloneBinary(downloadPath, currentExePath);
        }
    }

    private static void ApplyWindows(string downloadPath, string currentExePath)
    {
        var backupPath = $"{currentExePath}.bak";

        // Constant batch body using positional parameters (%1=current, %2=download, %3=backup). Each is
        // wrapped as "%~N" — %~N strips any quotes the arg already carries, and the surrounding quotes are
        // re-added explicitly, so paths containing spaces (e.g. "C:\Program Files\App") survive intact.
        const string batchScript =
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "move /y \"%~1\" \"%~3\"\r\n" +
            "move /y \"%~2\" \"%~1\"\r\n" +
            "start \"\" \"%~1\"\r\n" +
            "del \"%~3\"\r\n" +
            "del \"%~f0\"\r\n";

        var batchPath = Path.Combine(Path.GetTempPath(), $"ryn_update_{Guid.NewGuid():N}.bat");
        File.WriteAllText(batchPath, batchScript);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(batchPath);
        psi.ArgumentList.Add(currentExePath);
        psi.ArgumentList.Add(downloadPath);
        psi.ArgumentList.Add(backupPath);
        Process.Start(psi);
    }

    private static void ApplyLinux(string downloadPath, string currentExePath)
    {
        ReplaceStandaloneBinary(downloadPath, currentExePath);
    }

    private static void ReplaceStandaloneBinary(string downloadPath, string currentExePath)
    {
        var backupPath = $"{currentExePath}.bak";
        const string script =
            "cur=\"$1\"; dl=\"$2\"; backup=\"$3\"; " +
            "mv \"$cur\" \"$backup\" && mv \"$dl\" \"$cur\" && chmod +x \"$cur\" && \"$cur\" & rm -f \"$backup\"";
        LaunchDetachedShell(script, currentExePath, downloadPath, backupPath);
    }

    private static string? FindAppBundle(string executablePath)
    {
        var dir = Path.GetDirectoryName(executablePath);
        while (dir is not null)
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Launches <c>/bin/sh -c &lt;constant-script&gt; sh &lt;positional-args...&gt;</c> — injection-proof because the script is constant and every path is a separate argv element.</summary>
    private static void LaunchDetachedShell(string script, params string[] positionalArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("sh"); // $0
        foreach (var arg in positionalArgs)
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    // ---- version / floor ----

    private SemVer? GetEffectiveCurrentVersion()
    {
        var configured = _options.CurrentVersion is not null ? SemVer.Parse(_options.CurrentVersion) : null;
        configured ??= SemVer.Parse(Assembly.GetEntryAssembly()?.GetName().Version?.ToString());
        var floor = ReadVersionFloor();

        if (configured is null) return floor;
        if (floor is null) return configured;
        return configured.CompareTo(floor) >= 0 ? configured : floor;
    }

    private string VersionFloorFile() =>
        _options.VersionFloorPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ryn", "updates", $"{_options.GitHubOwner}_{_options.GitHubRepo}.floor");

    private SemVer? ReadVersionFloor()
    {
        try
        {
            var path = VersionFloorFile();
            return File.Exists(path) ? SemVer.Parse(File.ReadAllText(path).Trim()) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void PersistVersionFloor(string version)
    {
        try
        {
            var parsed = SemVer.Parse(version);
            if (parsed is null) return;

            // Stable-only apps must never record a prerelease floor: doing so could leave a stale prerelease
            // floor that compares above some other prerelease but, more importantly, keeps the floor file
            // carrying a channel the app doesn't track. Because SemVer orders rc < final, a recorded prerelease
            // floor never blocks its matching final anyway — but we still skip it when prereleases are off so the
            // persisted floor reflects only versions the app actually rides. (SUP-03)
            if (parsed.IsPrerelease && !_options.AllowPrerelease)
                return;

            var existing = ReadVersionFloor();
            if (existing is not null && parsed.CompareTo(existing) <= 0) return;

            var path = VersionFloorFile();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, parsed.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- helpers ----

    private Uri ValidateDownloadUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new SecurityException($"Update download URL '{url}' is not a valid absolute URL.");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"Update download URL must use HTTPS (got '{uri.Scheme}').");

        var host = uri.Host;
        var ok = false;
        foreach (var allowed in _options.AllowedDownloadHosts)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
            {
                ok = true;
                break;
            }
        }

        if (!ok)
            throw new SecurityException($"Update download host '{host}' is not in the allowed list.");

        return uri;
    }

    private GitHubAsset? FindMatchingAsset(GitHubRelease release)
    {
        if (release.Assets is null || release.Assets.Length == 0)
            return null;

        bool IsSignature(GitHubAsset a) =>
            a.Name is not null && a.Name.EndsWith(_options.SignatureAssetSuffix, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(_options.AssetPattern))
        {
            var regex = new Regex(_options.AssetPattern, RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
            foreach (var asset in release.Assets)
            {
                if (!IsSignature(asset) && asset.Name is not null && regex.IsMatch(asset.Name))
                    return asset;
            }
            return null;
        }

        var platformHints = GetPlatformHints();
        foreach (var asset in release.Assets)
        {
            if (asset.Name is null || IsSignature(asset))
                continue;

            foreach (var hint in platformHints)
            {
                if (asset.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return asset;
            }
        }

        return null;
    }

    private static string[] GetPlatformHints()
    {
        if (OperatingSystem.IsMacOS())
            return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? ["macos-arm64", "osx-arm64", "darwin-arm64", "macos", "osx", "darwin"]
                : ["macos-x64", "osx-x64", "darwin-x64", "macos", "osx", "darwin"];

        if (OperatingSystem.IsWindows())
            return ["win-x64", "windows-x64", "win64", "windows"];

        if (OperatingSystem.IsLinux())
            return ["linux-x64", "linux-amd64", "linux"];

        return [];
    }

    private static string GetAssetExtension(string url)
    {
        var uri = new Uri(url);
        var ext = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrEmpty(ext) ? ".bin" : ext;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _httpClient.Dispose();
        _httpHandler.Dispose();
    }

    private sealed record VerifiedDownload(string Path, string Version);

    /// <summary>
    /// A minimal SemVer 2.0.0 value that orders correctly across prereleases — the previous
    /// <see cref="Version"/>-based parse discarded the <c>-prerelease</c> suffix, so every prerelease of a base
    /// version compared equal and an rc could never advance to its final (SUP-03). Build metadata (after <c>+</c>)
    /// is ignored for ordering, exactly as the spec requires. Parsing is lenient about a leading <c>v</c> and a
    /// missing minor/patch (<c>1</c> / <c>1.2</c> → <c>1.0.0</c> / <c>1.2.0</c>) to match the prior behaviour.
    /// </summary>
    private sealed class SemVer : IComparable<SemVer>
    {
        private readonly int _major;
        private readonly int _minor;
        private readonly int _patch;
        private readonly string[] _prerelease; // empty => stable release
        private readonly string _display;

        private SemVer(int major, int minor, int patch, string[] prerelease, string display)
        {
            _major = major;
            _minor = minor;
            _patch = patch;
            _prerelease = prerelease;
            _display = display;
        }

        public bool IsPrerelease => _prerelease.Length > 0;

        public static SemVer? Parse(string? versionString)
        {
            if (string.IsNullOrEmpty(versionString))
                return null;

            var s = versionString.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
                s = s[1..];

            // Split off build metadata first (ignored for precedence), then the prerelease segment.
            var plus = s.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
                s = s[..plus];

            string corePart;
            string[] prerelease;
            var dash = s.IndexOf('-', StringComparison.Ordinal);
            if (dash >= 0)
            {
                corePart = s[..dash];
                var pre = s[(dash + 1)..];
                if (pre.Length == 0)
                    return null; // trailing '-' with no identifiers is malformed
                prerelease = pre.Split('.');
                foreach (var id in prerelease)
                    if (id.Length == 0)
                        return null; // empty identifier (e.g. "1.0.0-a..b")
            }
            else
            {
                corePart = s;
                prerelease = [];
            }

            var parts = corePart.Split('.');
            if (parts.Length is < 1 or > 3)
                return null;

            if (!TryParseComponent(parts[0], out var major))
                return null;
            var minor = 0;
            var patch = 0;
            if (parts.Length >= 2 && !TryParseComponent(parts[1], out minor))
                return null;
            if (parts.Length >= 3 && !TryParseComponent(parts[2], out patch))
                return null;

            return new SemVer(major, minor, patch, prerelease, versionString.Trim());
        }

        private static bool TryParseComponent(string text, out int value) =>
            int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);

        public int CompareTo(SemVer? other)
        {
            if (other is null) return 1;

            var cmp = _major.CompareTo(other._major);
            if (cmp != 0) return cmp;
            cmp = _minor.CompareTo(other._minor);
            if (cmp != 0) return cmp;
            cmp = _patch.CompareTo(other._patch);
            if (cmp != 0) return cmp;

            // Equal core. Per SemVer: a version WITH a prerelease has LOWER precedence than one without.
            var thisPre = _prerelease.Length > 0;
            var otherPre = other._prerelease.Length > 0;
            if (!thisPre && !otherPre) return 0;
            if (!thisPre) return 1;  // this is the release, other is a prerelease
            if (!otherPre) return -1; // this is a prerelease, other is the release

            return ComparePrerelease(_prerelease, other._prerelease);
        }

        // SemVer 2.0.0 §11: compare prerelease identifiers left to right. Numeric identifiers compare
        // numerically; alphanumerics compare by ASCII ordinal; numeric always ranks below alphanumeric; a
        // larger set of fields (when all preceding are equal) ranks higher.
        private static int ComparePrerelease(string[] a, string[] b)
        {
            var min = Math.Min(a.Length, b.Length);
            for (var i = 0; i < min; i++)
            {
                var aNum = int.TryParse(a[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ai);
                var bNum = int.TryParse(b[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bi);

                int cmp;
                if (aNum && bNum)
                    cmp = ai.CompareTo(bi);
                else if (aNum)
                    cmp = -1; // numeric < alphanumeric
                else if (bNum)
                    cmp = 1;
                else
                    cmp = string.CompareOrdinal(a[i], b[i]);

                if (cmp != 0) return cmp;
            }

            return a.Length.CompareTo(b.Length);
        }

        public override string ToString() => _display;
    }
}
