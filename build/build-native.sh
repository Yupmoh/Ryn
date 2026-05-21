#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="$REPO_ROOT/build/native"
VENDOR_DIR="$REPO_ROOT/vendor/saucer-bindings"

OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
    Darwin)
        RID="osx-$ARCH"
        LIB_PREFIX="lib"
        LIB_EXT=".dylib"
        CMAKE_EXTRA="-DCMAKE_OSX_ARCHITECTURES=$ARCH -Dsaucer_backend=WebKit"
        ;;
    Linux)
        RID="linux-$ARCH"
        LIB_PREFIX="lib"
        LIB_EXT=".so"
        CMAKE_EXTRA=""
        ;;
    MINGW*|MSYS*|CYGWIN*)
        RID="win-x64"
        LIB_PREFIX=""
        LIB_EXT=".dll"
        CMAKE_EXTRA=""
        ;;
    *)
        echo "ERROR: Unsupported OS: $OS"
        exit 1
        ;;
esac

DEST_DIR="$REPO_ROOT/src/Ryn.Interop/runtimes/$RID/native"
LIB_NAME="${LIB_PREFIX}saucer-bindings${LIB_EXT}"
DESKTOP_LIB_NAME="${LIB_PREFIX}saucer-bindings-desktop${LIB_EXT}"

echo "==> Building saucer-bindings for $RID..."

GENERATOR=""
if command -v ninja &> /dev/null; then
    GENERATOR="-G Ninja"
fi

cmake -S "$VENDOR_DIR" -B "$BUILD_DIR" \
    $GENERATOR \
    -DCMAKE_BUILD_TYPE=Release \
    -Dsaucer_bindings_modules="desktop;loop" \
    -Dsaucer_bindings_inline_modules="loop" \
    $CMAKE_EXTRA

cmake --build "$BUILD_DIR" --config Release --parallel "$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4)"

echo "==> Copying libraries to $DEST_DIR..."
mkdir -p "$DEST_DIR"

cp "$BUILD_DIR/$LIB_NAME" "$DEST_DIR/"
echo "   $LIB_NAME"

if [[ -f "$BUILD_DIR/modules/desktop/$DESKTOP_LIB_NAME" ]]; then
    cp "$BUILD_DIR/modules/desktop/$DESKTOP_LIB_NAME" "$DEST_DIR/"
    echo "   $DESKTOP_LIB_NAME"
fi

echo ""
echo "==> Verifying library..."
if command -v nm &> /dev/null; then
    SYMBOL_COUNT=$(nm -gC "$DEST_DIR/$LIB_NAME" 2>/dev/null | grep -c "saucer_" || true)
    echo "   Found $SYMBOL_COUNT saucer symbols"
fi

echo "==> Done! Native libraries built for $RID"
