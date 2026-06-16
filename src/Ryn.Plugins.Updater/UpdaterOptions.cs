namespace Ryn.Plugins.Updater;

public sealed class UpdaterOptions
{
    public string? GitHubOwner { get; set; }
    public string? GitHubRepo { get; set; }

    /// <summary>
    /// The application's current version. When null, the entry-assembly version is used. Never treated
    /// as "accept anything": if neither is available the updater refuses to apply (fail closed).
    /// </summary>
    public string? CurrentVersion { get; set; }

    public string? AssetPattern { get; set; }
    public bool CheckOnStartup { get; set; }
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// When <see langword="true"/>, prerelease tags (a SemVer build with a <c>-prerelease</c> suffix, e.g.
    /// <c>1.2.0-rc.1</c>) are eligible updates and the updater can move between prereleases of the same base
    /// version. When <see langword="false"/> (the default) the updater stays on stable releases: a prerelease
    /// is never offered, and a prerelease tag is never recorded as the downgrade floor (so an <c>rc</c> can
    /// never block the matching final release). Comparison always uses full SemVer prerelease ordering, so a
    /// stable release still correctly outranks its own prereleases regardless of this flag.
    /// </summary>
    /// <remarks>
    /// GitHub's <c>/releases/latest</c> endpoint (which this updater queries) excludes prereleases by design,
    /// so enabling this only has an effect when a custom feed is supplied or the latest stable tag itself
    /// carries a prerelease suffix.
    /// </remarks>
    public bool AllowPrerelease { get; set; }

    /// <summary>
    /// Timeout for an individual HTTP request made by the updater (release metadata, asset, signature). Bounds
    /// the startup check so an unreachable or slow endpoint logs a failure instead of hanging. Default 15s.
    /// </summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Base64-encoded X.509 SubjectPublicKeyInfo (ECDSA P-256) public key. <strong>Required.</strong>
    /// Updates are only applied if a detached signature over the downloaded bytes verifies against this
    /// key. Generate a keypair with <c>ryn updater keygen</c> (or <see cref="UpdateSignature.GenerateKeyPair"/>),
    /// embed the public key here, and sign each release asset with the private key.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// Suffix appended to the asset file name to locate its detached signature asset in the release
    /// (e.g. <c>MyApp-osx-arm64.zip</c> → <c>MyApp-osx-arm64.zip.sig</c>). The <c>.sig</c> asset must
    /// contain the base64-encoded signature produced by <see cref="UpdateSignature.Sign"/>.
    /// </summary>
    public string SignatureAssetSuffix { get; set; } = ".sig";

    /// <summary>Maximum bytes to download/buffer for an update artifact. Guards against a hostile or runaway asset. Default 512 MiB.</summary>
    public long MaxDownloadBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Path to a small file recording the highest version ever installed, used as a monotonic downgrade
    /// floor. When null, a per-user application-data location derived from the repo is used.
    /// </summary>
    public string? VersionFloorPath { get; set; }

    /// <summary>Host suffixes permitted for downloading release assets. Defaults to GitHub's release hosts.</summary>
    public List<string> AllowedDownloadHosts { get; set; } =
        ["github.com", "githubusercontent.com", "objects.githubusercontent.com"];
}
