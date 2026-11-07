#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="$REPO_ROOT/build/native"
VENDOR_DIR="$REPO_ROOT/vendor/saucer-bindings"

OS="$(uname -s)"
RAW_ARCH="$(uname -m)"

case "$RAW_ARCH" in
    x86_64) ARCH="x64" ;;
    aarch64) ARCH="arm64" ;;
    *) ARCH="$RAW_ARCH" ;;
esac

case "$OS" in
    Darwin)
        RID="osx-$ARCH"
        LIB_PREFIX="lib"
        LIB_EXT=".dylib"
        CMAKE_EXTRA="-DCMAKE_OSX_ARCHITECTURES=$RAW_ARCH -Dsaucer_backend=WebKit"
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
ARCH_FLAG=""
if command -v ninja &> /dev/null; then
    GENERATOR="-G Ninja"
elif [[ "$OS" == MINGW* || "$OS" == MSYS* || "$OS" == CYGWIN* ]]; then
    ARCH_FLAG="-A x64"
fi

cmake -S "$VENDOR_DIR" -B "$BUILD_DIR" \
    $GENERATOR \
    $ARCH_FLAG \
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

# Copy transitive native dependencies (Windows only)
if [[ "$OS" == MINGW* || "$OS" == MSYS* || "$OS" == CYGWIN* ]]; then
    SAUCER_DLL="$BUILD_DIR/_deps/saucer-build/Release/saucer.dll"
    if [[ -f "$SAUCER_DLL" ]]; then
        cp "$SAUCER_DLL" "$DEST_DIR/"
        echo "   saucer.dll"
    fi

    WV2_DLL=$(find "$BUILD_DIR/_deps/saucer-build" -name "WebView2Loader.dll" -path "*/win-x64/*" 2>/dev/null | head -1)
    if [[ -n "$WV2_DLL" && -f "$WV2_DLL" ]]; then
        cp "$WV2_DLL" "$DEST_DIR/"
        echo "   WebView2Loader.dll"
    fi
fi

# Build ryn_pty native shim (Unix only)
PTY_SRC="$REPO_ROOT/src/Ryn.Plugins.Shell/native/ryn_pty.c"
if [[ "$OS" != MINGW* && "$OS" != MSYS* && "$OS" != CYGWIN* && -f "$PTY_SRC" ]]; then
    echo "==> Building ryn-pty shim..."
    PTY_LIB="${LIB_PREFIX}ryn-pty${LIB_EXT}"
    PTY_FLAGS="-shared -fPIC -O2"
    if [[ "$OS" == "Darwin" ]]; then
        PTY_FLAGS="$PTY_FLAGS -lutil"
    else
        PTY_FLAGS="$PTY_FLAGS -lutil"
    fi
    cc $PTY_FLAGS -o "$DEST_DIR/$PTY_LIB" "$PTY_SRC"
    echo "   $PTY_LIB"
fi

# Build the Unix owned-sidecar crash watchdog (separate from PTY semantics and ABI).
SIDECAR_SRC="$REPO_ROOT/src/Ryn.Plugins.Sidecar/native/ryn_sidecar.c"
if [[ "$OS" != MINGW* && "$OS" != MSYS* && "$OS" != CYGWIN* && -f "$SIDECAR_SRC" ]]; then
    echo "==> Building ryn-sidecar watchdog..."
    SIDECAR_LIB="${LIB_PREFIX}ryn-sidecar${LIB_EXT}"
    cc -shared -fPIC -O2 -o "$DEST_DIR/$SIDECAR_LIB" "$SIDECAR_SRC"
    echo "   $SIDECAR_LIB"
fi

echo ""
echo "==> Verifying library..."
if command -v nm &> /dev/null; then
    SYMBOL_COUNT=$(nm -gC "$DEST_DIR/$LIB_NAME" 2>/dev/null | grep -c "saucer_" || true)
    echo "   Found $SYMBOL_COUNT saucer symbols"
fi

echo "==> Done! Native libraries built for $RID"
