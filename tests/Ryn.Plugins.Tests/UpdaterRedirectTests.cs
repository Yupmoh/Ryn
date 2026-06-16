using System.Reflection;
using System.Security;
using FluentAssertions;
using Ryn.Plugins.Updater;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for SUP-06: the updater follows HTTP redirects manually (auto-redirect is off) and
/// re-runs the host allowlist check on every hop, so a 302 that bounces an allowed download onto a
/// disallowed host is rejected with <see cref="SecurityException"/>, while the legitimate
/// <c>github.com → objects.githubusercontent.com</c> asset hop (both allow-listed) is accepted.
///
/// The per-hop decision is <c>ValidateDownloadUri</c>; the redirect follower calls it on each
/// <c>Location</c>. These tests exercise that real method directly (it is a private instance method) plus
/// the public <see cref="UpdaterService.DownloadUpdateAsync"/> entry point, which validates the asset and
/// signature hosts up-front — a disallowed host fails closed there before any bytes are fetched. The live
/// 3xx mechanics over a loopback server are not exercised here (see notes) because every hop is HTTPS-gated
/// and a deterministic loopback TLS endpoint is not available in CI; validating the gate function is the
/// equivalent deterministic lock.
/// </summary>
public sealed class UpdaterRedirectTests
{
    private static readonly MethodInfo ValidateDownloadUri =
        typeof(UpdaterService).GetMethod("ValidateDownloadUri", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("UpdaterService.ValidateDownloadUri was not found.");

    private static UpdaterService NewService(params string[] allowedHosts)
    {
        var options = new UpdaterOptions
        {
            GitHubOwner = "o",
            GitHubRepo = "r",
            AllowedDownloadHosts = allowedHosts.Length > 0
                ? new List<string>(allowedHosts)
                : new List<string> { "github.com", "githubusercontent.com", "objects.githubusercontent.com" },
        };

        var ctor = typeof(UpdaterService).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(UpdaterOptions), typeof(Ryn.Core.IRynApplicationLifetime) }, null)
            ?? throw new InvalidOperationException("internal UpdaterService ctor not found.");
        return (UpdaterService)ctor.Invoke(new object?[] { options, null });
    }

    private static void Validate(UpdaterService service, string url)
    {
        try
        {
            ValidateDownloadUri.Invoke(service, new object?[] { url });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void AllowedGitHubAssetHop_IsAccepted()
    {
        using var service = NewService();

        // Both ends of the real GitHub asset redirect chain are allow-listed, so each hop validates.
        Validate(service, "https://github.com/o/r/releases/download/v1/app.zip");
        var act = () => Validate(service, "https://objects.githubusercontent.com/github-production-release/app.zip");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("https://evil.example.com/app.zip")]          // wholly different host
    [InlineData("https://github.com.evil.example.com/app.zip")] // suffix-confusion attempt
    [InlineData("https://notgithub.com/app.zip")]
    public void RedirectTargetOnDisallowedHost_IsRejected(string redirectTarget)
    {
        using var service = NewService();

        // This is exactly the check the manual redirect follower runs on each Location hop.
        var act = () => Validate(service, redirectTarget);
        act.Should().Throw<SecurityException>().WithMessage("*not in the allowed list*");
    }

    [Fact]
    public void NonHttpsRedirectTarget_IsRejected()
    {
        using var service = NewService();

        // A downgrade to plain HTTP on a redirect hop is refused even if the host would be allowed.
        var act = () => Validate(service, "http://github.com/o/r/releases/download/v1/app.zip");
        act.Should().Throw<SecurityException>().WithMessage("*HTTPS*");
    }

    [Fact]
    public async Task DownloadUpdate_RejectsAssetOnDisallowedHost_BeforeAnyNetwork()
    {
        // End-to-end through the public API: an asset URL whose host is not allow-listed is refused up-front
        // (fail-closed) — the same SecurityException the redirect follower would raise on a hostile hop.
        var (pub, _) = UpdateSignature.GenerateKeyPair();

        var info = new UpdateInfo
        {
            Version = "1.0.0",
            ReleaseUrl = new Uri("https://github.com/o/r/releases"),
            AssetUrl = new Uri("https://evil.example.com/app.zip"),
            SignatureUrl = new Uri("https://evil.example.com/app.zip.sig"),
        };

        // Give it a key so it doesn't bail on the missing-key check first; the host check must still reject
        // before any bytes are fetched.
        using var keyed = NewServiceWithKey(pub);
        var act = async () => await keyed.DownloadUpdateAsync(info);
        await act.Should().ThrowAsync<SecurityException>();
    }

    private static UpdaterService NewServiceWithKey(string publicKey)
    {
        var options = new UpdaterOptions
        {
            GitHubOwner = "o",
            GitHubRepo = "r",
            PublicKey = publicKey,
            AllowedDownloadHosts = new List<string> { "github.com", "githubusercontent.com", "objects.githubusercontent.com" },
        };
        var ctor = typeof(UpdaterService).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(UpdaterOptions), typeof(Ryn.Core.IRynApplicationLifetime) }, null)!;
        return (UpdaterService)ctor.Invoke(new object?[] { options, null });
    }
}
