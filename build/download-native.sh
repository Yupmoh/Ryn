#!/usr/bin/env bash
set -euo pipefail

# Downloads prebuilt saucer-bindings native libraries from the Ryn GitHub Releases.
# Falls back to building from source if download fails.
#
# Usage:
#   ./build/download-native.sh              # download for current platform
#   ./build/download-native.sh all          # download for all platforms
#   ./build/download-native.sh osx-arm64    # download for specific RID

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO="Yupmoh/Ryn"
INTEROP_DIR="$REPO_ROOT/src/Ryn.Interop"

detect_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"

    case "$os" in
        Darwin) echo "osx-$arch" ;;
        Linux)  echo "linux-x64" ;;
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

download_rid() {
    local rid="$1"
    local ext dest archive_name

    ext="$(archive_ext "$rid")"
    dest="$INTEROP_DIR/runtimes/$rid/native"
    archive_name="saucer-bindings-${rid}${ext}"

    mkdir -p "$dest"

    echo "==> Downloading $archive_name..."

    if ! gh release download --repo "$REPO" --pattern "$archive_name" --dir /tmp --clobber 2>/dev/null; then
        echo "    Failed to download from GitHub Releases."
        echo "    Trying latest release tag matching 'native-v*'..."

        local tag
        tag=$(gh release list --repo "$REPO" --limit 10 2>/dev/null | grep -o 'native-v[^ ]*' | head -1 || true)

        if [[ -z "$tag" ]]; then
            echo "    No native-v* release found."
            return 1
        fi

        if ! gh release download "$tag" --repo "$REPO" --pattern "$archive_name" --dir /tmp --clobber 2>/dev/null; then
            echo "    Download failed for tag $tag."
            return 1
        fi
    fi

    echo "    Extracting to $dest..."
    if [[ "$ext" == ".zip" ]]; then
        unzip -o "/tmp/$archive_name" -d "$dest"
    else
        tar -xzf "/tmp/$archive_name" -C "$dest"
    fi

    rm -f "/tmp/$archive_name"
    echo "    Done: $(ls "$dest")"
}

ALL_RIDS=(osx-arm64 linux-x64 win-x64)

case "${1:-}" in
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
