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
$InteropDir = Join-Path $RepoRoot "src" "Ryn.Interop"

$AllRids = @("osx-arm64", "linux-x64", "win-x64")

function Get-ArchiveExt($rid) {
    if ($rid -like "win-*") { return ".zip" } else { return ".tar.gz" }
}

function Download-Rid($rid) {
    $ext = Get-ArchiveExt $rid
    $archiveName = "saucer-bindings-${rid}${ext}"
    $dest = Join-Path $InteropDir "runtimes" $rid "native"
    $tempFile = Join-Path $env:TEMP $archiveName

    New-Item -ItemType Directory -Force -Path $dest | Out-Null

    Write-Host "==> Downloading $archiveName..."

    try {
        & gh release download --repo $Repo --pattern $archiveName --dir $env:TEMP --clobber 2>$null
        if ($LASTEXITCODE -ne 0) { throw "gh download failed" }
    }
    catch {
        Write-Host "    Trying latest native-v* release..."
        $tags = & gh release list --repo $Repo --limit 10 2>$null
        $tag = ($tags | Select-String "native-v" | Select-Object -First 1) -replace '\s.*', ''

        if (-not $tag) {
            Write-Host "    No native-v* release found."
            return $false
        }

        & gh release download $tag --repo $Repo --pattern $archiveName --dir $env:TEMP --clobber 2>$null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "    Download failed."
            return $false
        }
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
