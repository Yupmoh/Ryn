# Downloads prebuilt saucer-bindings native libraries for Windows.
# Usage:
#   .\build\download-native.ps1              # download for current platform
#   .\build\download-native.ps1 -Rid win-x64 # download for specific RID
#   .\build\download-native.ps1 -All         # download for all platforms

param(
    [string]$Rid = "",
    [switch]$All
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Repo = "Yupmoh/Ryn"
$InteropDir = Join-Path (Join-Path $RepoRoot "src") "Ryn.Interop"

$AllRids = @("osx-arm64", "linux-x64", "win-x64")

function Get-GitHubToken {
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    if ($env:GH_TOKEN) { return $env:GH_TOKEN }

    try {
        $credInput = "protocol=https`nhost=github.com`n"
        $tempFile = [System.IO.Path]::GetTempFileName()
        [System.IO.File]::WriteAllText($tempFile, $credInput)
        $output = cmd /c "git credential fill < `"$tempFile`"" 2>$null
        Remove-Item $tempFile -ErrorAction SilentlyContinue
        foreach ($line in $output) {
            if ($line -match "^password=(.+)$") {
                return $Matches[1]
            }
        }
    }
    catch {}

    return $null
}

function Get-AuthHeaders {
    $token = Get-GitHubToken
    $headers = @{ "Accept" = "application/vnd.github+json" }
    if ($token) {
        $headers["Authorization"] = "Bearer $token"
    }
    return $headers
}

function Get-ArchiveExt($rid) {
    if ($rid -like "win-*") { return ".zip" } else { return ".tar.gz" }
}

# Verify a downloaded archive against the pinned SHA-256 in native-checksums.txt before extraction.
# Supply-chain protection: this fails CLOSED (parity with download-native.sh's verify_checksum). A
# checksum mismatch OR a missing pin both abort the download (return $false -> non-zero exit) so a
# tampered, corrupt, or unpinned artifact is never extracted or used.
#
# Escape hatch (opt-in, loud): set RYN_ALLOW_UNVERIFIED_NATIVE=1 to downgrade a *missing* pin to a
# warning. This exists only for bootstrapping a new native-v* release before its checksums have been
# pinned; it never bypasses a real mismatch. Leave it unset for any trusted/CI flow.
function Test-Checksum($file, $name) {
    $checksumsFile = Join-Path $PSScriptRoot "native-checksums.txt"
    $expected = $null
    if (Test-Path $checksumsFile) {
        foreach ($line in Get-Content $checksumsFile) {
            if ($line -match '^\s*#') { continue }
            if ($line -match "([0-9a-fA-F]{64})\s+$([regex]::Escape($name))\s*$") {
                $expected = $Matches[1].ToLowerInvariant()
                break
            }
        }
    }

    if (-not $expected) {
        if ($env:RYN_ALLOW_UNVERIFIED_NATIVE -eq "1") {
            Write-Host "    WARNING: no pinned checksum for $name -- RYN_ALLOW_UNVERIFIED_NATIVE=1 is set, using it UNVERIFIED."
            Write-Host "        Regenerate pins (see build/native-checksums.txt) before trusting this artifact."
            return $true
        }
        Write-Host "    NO PINNED CHECKSUM for $name in build/native-checksums.txt"
        Write-Host "        Refusing to use an unpinned native artifact (fail-closed supply-chain check)."
        Write-Host "        Pin its SHA-256 in build/native-checksums.txt, or build from source instead"
        Write-Host "        (set RYN_ALLOW_UNVERIFIED_NATIVE=1 only to bootstrap a brand-new release)."
        return $false
    }

    $actual = (Get-FileHash -Path $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Write-Host "    CHECKSUM MISMATCH for $name"
        Write-Host "        expected: $expected"
        Write-Host "        actual:   $actual"
        Write-Host "    Refusing to use a tampered or corrupt artifact."
        return $false
    }

    Write-Host "    checksum verified ($expected)"
    return $true
}

function Download-Rid($rid) {
    $ext = Get-ArchiveExt $rid
    $archiveName = "saucer-bindings-${rid}${ext}"
    $dest = Join-Path (Join-Path (Join-Path $InteropDir "runtimes") $rid) "native"
    $tempFile = Join-Path $env:TEMP $archiveName

    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    Write-Host "==> Downloading $archiveName..."

    $headers = Get-AuthHeaders
    $apiBase = "https://api.github.com/repos/$Repo"

    try {
        $releases = Invoke-RestMethod -Uri "$apiBase/releases" -Headers $headers -UseBasicParsing
        $release = $null

        # Try latest release first, then fall back to native-v* tagged releases
        foreach ($r in $releases) {
            $asset = $r.assets | Where-Object { $_.name -eq $archiveName } | Select-Object -First 1
            if ($asset) {
                $release = $r
                break
            }
        }

        if (-not $release) {
            foreach ($r in $releases) {
                if ($r.tag_name -like "native-v*") {
                    $asset = $r.assets | Where-Object { $_.name -eq $archiveName } | Select-Object -First 1
                    if ($asset) {
                        $release = $r
                        break
                    }
                }
            }
        }

        if (-not $release -or -not $asset) {
            Write-Host "    No release found containing $archiveName."
            return $false
        }

        Write-Host "    Found in release $($release.tag_name)"

        $downloadHeaders = @{ "Accept" = "application/octet-stream" }
        $token = Get-GitHubToken
        if ($token) { $downloadHeaders["Authorization"] = "Bearer $token" }

        Invoke-WebRequest -Uri $asset.url -Headers $downloadHeaders -OutFile $tempFile -UseBasicParsing
    }
    catch {
        Write-Host "    Download failed: $_"
        return $false
    }

    if (-not (Test-Checksum $tempFile $archiveName)) {
        Remove-Item -Path $tempFile -ErrorAction SilentlyContinue
        return $false
    }

    Write-Host "    Extracting to $dest..."
    if ($ext -eq ".zip") {
        Expand-Archive -Path $tempFile -DestinationPath $dest -Force
    }
    else {
        tar -xzf $tempFile -C $dest
    }

    Remove-Item -Path $tempFile -ErrorAction SilentlyContinue
    Write-Host "    Done: $(Get-ChildItem $dest | ForEach-Object { $_.Name })"
    return $true
}

if ($All) {
    Write-Host "==> Downloading native libraries for all platforms..."
    foreach ($r in $AllRids) {
        if (-not (Download-Rid $r)) {
            Write-Host "    WARNING: Failed for $r, skipping."
        }
    }
}
elseif ($Rid) {
    if (-not (Download-Rid $Rid)) {
        Write-Host "==> Download failed for $Rid."
        exit 1
    }
}
else {
    $currentRid = "win-x64"
    if (-not (Download-Rid $currentRid)) {
        Write-Host ""
        Write-Host "==> Download failed. Build from source with:"
        Write-Host "    bash build/build-native.sh"
        exit 1
    }
}

Write-Host ""
Write-Host "==> Native libraries ready."
