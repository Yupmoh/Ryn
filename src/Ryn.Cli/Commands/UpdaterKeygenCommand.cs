using System.Security.Cryptography;

namespace Ryn.Cli.Commands;

/// <summary>
/// <c>ryn updater keygen</c> — generates the signing keypair the auto-updater uses to verify releases.
/// </summary>
/// <remarks>
/// The scheme is fixed by <c>Ryn.Plugins.Updater.UpdateSignature</c>: ECDSA over NIST P-256 with SHA-256
/// and DER-encoded (RFC 3279) signatures. The public key is exported as base64 X.509
/// SubjectPublicKeyInfo (SPKI) — the exact shape <c>UpdaterOptions.PublicKey</c> / <c>UpdateSignature.Verify</c>
/// consume — and the private key as base64 PKCS#8, the shape <c>UpdateSignature.Sign</c> consumes. The
/// generation here is a deliberate, dependency-free mirror of <c>UpdateSignature.GenerateKeyPair()</c>:
/// Ryn.Cli does not reference Ryn.Plugins.Updater, and the curve/encoding are part of the verified contract,
/// so re-deriving the same SPKI/PKCS#8 base64 from the BCL keeps the CLI self-contained and NativeAOT-safe.
/// </remarks>
internal static class UpdaterKeygenCommand
{
    internal static int Execute(ReadOnlySpan<string> args)
    {
        // The only positional/value option is --out (where to write the private key). Everything else is
        // rejected by the dispatch-layer flag check before we get here, so we just read the value.
        var outPath = GetArgValue(args, "--out");

        string publicKeyBase64;
        string privateKeyBase64;
        try
        {
            (publicKeyBase64, privateKeyBase64) = GenerateKeyPair();
        }
        catch (CryptographicException ex)
        {
            Console.Error.WriteLine($"Failed to generate a signing keypair: {ex.Message}");
            return 1;
        }

        // The private key is the secret that authorizes every future update. Default it to a clearly named,
        // owner-only file in the current directory rather than echoing it to the terminal (where it would
        // land in scrollback / shell history). Only the public key is printed.
        outPath ??= Path.Combine(Directory.GetCurrentDirectory(), "ryn-updater.key");

        try
        {
            WritePrivateKeyFile(outPath, privateKeyBase64);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write the private key to '{outPath}': {ex.Message}");
            return 1;
        }

        Console.WriteLine("Generated a Ryn updater signing keypair (ECDSA P-256).");
        Console.WriteLine();
        Console.WriteLine("Public key — put this in UpdaterOptions.PublicKey (or ryn.json):");
        Console.WriteLine();
        Console.WriteLine(publicKeyBase64);
        Console.WriteLine();
        Console.WriteLine($"Private key written to: {outPath}");
        Console.WriteLine();
        Console.WriteLine("  KEEP THE PRIVATE KEY SECRET. Anyone who has it can sign updates your app will");
        Console.WriteLine("  install. Do not commit it; store it in your release secrets (e.g. CI secret).");
        Console.WriteLine("  Sign each release asset with it (UpdateSignature.Sign) and publish the detached");
        Console.WriteLine("  base64 signature as the '<asset>.sig' release asset.");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine();
            Console.WriteLine("  The key file was created with owner-only (0600) permissions.");
        }

        return 0;
    }

    /// <summary>
    /// Mirrors <c>UpdateSignature.GenerateKeyPair()</c>: ECDSA P-256, public as base64 SPKI, private as
    /// base64 PKCS#8 — the exact encodings the updater verifies and signs with.
    /// </summary>
    private static (string PublicKeyBase64, string PrivateKeyBase64) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var priv = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
        return (pub, priv);
    }

    private static void WritePrivateKeyFile(string path, string privateKeyBase64)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (OperatingSystem.IsWindows())
        {
            // Windows has no POSIX mode; a new file under the current directory inherits NTFS ACLs from the
            // parent. We can't tighten that portably without P/Invoke, so the in-band "keep secret" warning
            // is the guard here.
            File.WriteAllText(fullPath, privateKeyBase64 + Environment.NewLine);
            return;
        }

        // Create the file with owner read/write only (0600) BEFORE writing, so the secret never briefly
        // exists with a broader mode. FileMode.CreateNew refuses to clobber an existing key by accident.
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = ownerOnly,
        };

        using var writer = new StreamWriter(new FileStream(fullPath, options));
        writer.Write(privateKeyBase64);
        writer.Write(Environment.NewLine);
    }

    private static string? GetArgValue(ReadOnlySpan<string> args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }
}
