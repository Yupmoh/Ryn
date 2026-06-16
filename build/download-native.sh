#!/usr/bin/env bash
set -euo pipefail

# Downloads prebuilt saucer-bindings native libraries from the Ryn GitHub Releases.
# Falls back to building from source if download fails.
#
# Usage:
#   ./build/download-native.sh              # download for current platform
#   ./build/download-native.sh all          # download for all platforms
#   ./build/download-native.sh osx-arm64    # download for specific RID
#   ./build/download-native.sh pin [rid...] # (re)pin native-checksums.txt from the release archives
#
# Downloads are verified against build/native-checksums.txt and fail CLOSED: a checksum mismatch or a
# missing pin aborts (non-zero exit). Set RYN_ALLOW_UNVERIFIED_NATIVE=1 to downgrade a *missing* pin to
# a warning (bootstrapping a fresh release only — never bypasses a real mismatch).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO="Yupmoh/Ryn"
API_BASE="https://api.github.com/repos/$REPO"
INTEROP_DIR="$REPO_ROOT/src/Ryn.Interop"

get_github_token() {
    if [[ -n "${GITHUB_TOKEN:-}" ]]; then echo "$GITHUB_TOKEN"; return; fi
    if [[ -n "${GH_TOKEN:-}" ]]; then echo "$GH_TOKEN"; return; fi

    local token
    token=$(printf 'protocol=https\nhost=github.com\n' | git credential fill 2>/dev/null | grep '^password=' | cut -d= -f2- || true)
    if [[ -n "$token" ]]; then echo "$token"; return; fi

    echo ""
}

auth_header() {
    local token
    token="$(get_github_token)"
    if [[ -n "$token" ]]; then
        echo "Authorization: Bearer $token"
    else
        echo "X-No-Auth: true"
    fi
}

detect_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"

    case "$arch" in
        x86_64) arch="x64" ;;
        aarch64) arch="arm64" ;;
    esac

    case "$os" in
        Darwin) echo "osx-$arch" ;;
        Linux)  echo "linux-$arch" ;;
        MINGW*|MSYS*|CYGWIN*) echo "win-x64" ;;
        *) echo "unknown"; return 1 ;;
    esac
}

archive_ext() {
    case "$1" in
        win-*) echo ".zip" ;;
        *)     echo ".tar.gz" ;;
    esac
}

CHECKSUMS_FILE="$SCRIPT_DIR/native-checksums.txt"

compute_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

# Verify a downloaded archive against the pinned SHA-256 in native-checksums.txt before extraction.
# Supply-chain protection: this fails CLOSED. A checksum mismatch OR a missing pin both abort the
# download (non-zero exit) so a tampered, corrupt, or unpinned artifact is never extracted or used.
#
# Escape hatch (opt-in, loud): set RYN_ALLOW_UNVERIFIED_NATIVE=1 to downgrade a *missing* pin to a
# warning. This exists only for bootstrapping a new native-v* release before its checksums have been
# pinned; it never bypasses a real mismatch. Leave it unset for any trusted/CI flow.
verify_checksum() {
    local file="$1" name="$2"
    local expected actual
    expected=$(grep -E "[[:space:]]$name\$" "$CHECKSUMS_FILE" 2>/dev/null | grep -v '^#' | awk '{print $1}' | head -n1 || true)

    if [[ -z "$expected" ]]; then
        if [[ "${RYN_ALLOW_UNVERIFIED_NATIVE:-}" == "1" ]]; then
            echo "    ⚠️  WARNING: no pinned checksum for $name — RYN_ALLOW_UNVERIFIED_NATIVE=1 is set, using it UNVERIFIED."
            echo "        Regenerate pins (see build/native-checksums.txt) before trusting this artifact."
            return 0
        fi
        echo "    ✖ NO PINNED CHECKSUM for $name in build/native-checksums.txt"
        echo "        Refusing to use an unpinned native artifact (fail-closed supply-chain check)."
        echo "        Pin its SHA-256 in build/native-checksums.txt, or build from source instead"
        echo "        (set RYN_ALLOW_UNVERIFIED_NATIVE=1 only to bootstrap a brand-new release)."
        return 1
    fi

    actual="$(compute_sha256 "$file")"
    if [[ "$actual" != "$expected" ]]; then
        echo "    ✖ CHECKSUM MISMATCH for $name"
        echo "        expected: $expected"
        echo "        actual:   $actual"
        echo "    Refusing to use a tampered or corrupt artifact."
        return 1
    fi

    echo "    ✓ checksum verified ($expected)"
    return 0
}

# Download the release asset for a RID into $tmp_file (no checksum verification here).
# Echoes nothing; returns 0 on success with the file at the caller-provided path.
fetch_archive() {
    local archive_name="$1" tmp_file="$2"
    local auth_hdr
    auth_hdr="$(auth_header)"

    # Query releases via REST API
    local releases_json
    releases_json=$(curl -sS -H "$auth_hdr" -H "Accept: application/vnd.github+json" "$API_BASE/releases" 2>/dev/null) || {
        echo "    Failed to query GitHub API."
        return 1
    }

    # Find the asset URL — try all releases, prefer native-v* tagged ones
    local asset_url=""
    asset_url=$(echo "$releases_json" | python3 -c "
import json, sys
releases = json.load(sys.stdin)
for r in releases:
    for a in r.get('assets', []):
        if a['name'] == '$archive_name':
            print(a['url'])
            sys.exit(0)
sys.exit(1)
" 2>/dev/null) || true

    if [[ -z "$asset_url" ]]; then
        echo "    No release found containing $archive_name."
        return 1
    fi

    echo "    Found asset, downloading..."

    if ! curl -sS -L -H "$auth_hdr" -H "Accept: application/octet-stream" -o "$tmp_file" "$asset_url"; then
        echo "    Download failed."
        return 1
    fi
}

# Regenerate build/native-checksums.txt from the authoritative GitHub release archives.
# This is the *only* sanctioned way to (re)pin: it downloads each published archive and records
# its real SHA-256. Run it once per native-v* release, review the diff, and commit the result.
pin_checksums() {
    local rids=("$@")
    [[ ${#rids[@]} -eq 0 ]] && rids=("${ALL_RIDS[@]}")

    local tmp_pins="/tmp/native-checksums.new.txt"
    {
        echo "# Pinned SHA-256 checksums for prebuilt saucer-bindings native archives."
        echo "#"
        echo "# Format (one per line):   <sha256-hex>  <archive-file-name>"
        echo "#"
        echo "# download-native.sh / download-native.ps1 verify every downloaded archive against the entry"
        echo "# here BEFORE extracting it. Verification fails CLOSED: a mismatch OR a missing entry aborts"
        echo "# the download (non-zero exit) so an unpinned or tampered artifact is never used. The only"
        echo "# override is RYN_ALLOW_UNVERIFIED_NATIVE=1, which downgrades a *missing* pin to a warning."
        echo "#"
        echo "# To (re)generate from the authoritative GitHub release archives:"
        echo "#   ./build/download-native.sh pin            # all RIDs"
        echo "#   ./build/download-native.sh pin osx-arm64  # one RID"
        echo "# then review and commit. (Manual equivalent: download each saucer-bindings-<rid>.{tar.gz,zip}"
        echo "# from the latest native-v* release and run  shasum -a 256 <archive>.)"
    } > "$tmp_pins"

    local rid ext archive_name tmp_file sum failed=0
    for rid in "${rids[@]}"; do
        ext="$(archive_ext "$rid")"
        archive_name="saucer-bindings-${rid}${ext}"
        tmp_file="/tmp/$archive_name"
        echo "==> Pinning $archive_name..."
        if ! fetch_archive "$archive_name" "$tmp_file"; then
            echo "    Could not fetch $archive_name — skipping."
            failed=1
            continue
        fi
        sum="$(compute_sha256 "$tmp_file")"
        printf '%s  %s\n' "$sum" "$archive_name" >> "$tmp_pins"
        rm -f "$tmp_file"
        echo "    $sum"
    done

    mv "$tmp_pins" "$CHECKSUMS_FILE"
    echo ""
    echo "==> Wrote $CHECKSUMS_FILE. Review the diff and commit."
    return "$failed"
}

download_rid() {
    local rid="$1"
    local ext dest archive_name

    ext="$(archive_ext "$rid")"
    dest="$INTEROP_DIR/runtimes/$rid/native"
    archive_name="saucer-bindings-${rid}${ext}"

    mkdir -p "$dest"

    echo "==> Downloading $archive_name..."

    local tmp_file="/tmp/$archive_name"
    if ! fetch_archive "$archive_name" "$tmp_file"; then
        rm -f "$tmp_file"
        return 1
    fi

    if ! verify_checksum "$tmp_file" "$archive_name"; then
        rm -f "$tmp_file"
        return 1
    fi

    echo "    Extracting to $dest..."
    if [[ "$ext" == ".zip" ]]; then
        unzip -o "$tmp_file" -d "$dest"
    else
        tar -xzf "$tmp_file" -C "$dest"
    fi

    rm -f "$tmp_file"
    echo "    Done: $(ls "$dest")"
}

ALL_RIDS=(osx-arm64 linux-x64 win-x64)

case "${1:-}" in
    pin|--update-checksums)
        # Regenerate build/native-checksums.txt from the authoritative release archives.
        shift || true
        pin_checksums "$@"
        exit $?
        ;;
    all)
        echo "==> Downloading native libraries for all platforms..."
        for rid in "${ALL_RIDS[@]}"; do
            download_rid "$rid" || echo "    WARNING: Failed for $rid, skipping."
        done
        ;;
    "")
        rid="$(detect_rid)"
        if ! download_rid "$rid"; then
            echo ""
            echo "==> Download failed. Falling back to building from source..."
            exec "$SCRIPT_DIR/build-native.sh"
        fi
        ;;
    *)
        if ! download_rid "$1"; then
            echo ""
            echo "==> Download failed for $1."
            exit 1
        fi
        ;;
esac

echo ""
echo "==> Native libraries ready."
