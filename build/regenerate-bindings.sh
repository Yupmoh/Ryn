#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CONFIG_DIR="$SCRIPT_DIR/clangsharp"
OUTPUT_DIR="$REPO_ROOT/src/Ryn.Interop/Generated"

echo "==> Checking ClangSharp is installed..."
if ! command -v ClangSharpPInvokeGenerator &> /dev/null; then
    echo "ClangSharpPInvokeGenerator not found. Installing..."
    dotnet tool install --global ClangSharpPInvokeGenerator
fi

echo "==> Detecting libclang..."
LLVM_LIB=""
CLANGSHARP_LIB=""

if [[ "$(uname)" == "Darwin" ]]; then
    LLVM_LIB="$(find /opt/homebrew/Cellar/llvm@21 -name 'libclang.dylib' 2>/dev/null | head -1)"
    if [[ -z "$LLVM_LIB" ]]; then
        LLVM_LIB="$(find /opt/homebrew/Cellar/llvm -name 'libclang.dylib' 2>/dev/null | head -1)"
    fi
    CLANGSHARP_LIB="$(find ~/.dotnet/tools/.store/clangsharppinvokegenerator -name 'libClangSharp.dylib' 2>/dev/null | head -1)"
elif [[ "$(uname)" == "Linux" ]]; then
    # Pick the HIGHEST libclang version — CI runners often ship an older llvm (e.g. 18)
    # alongside the one we install (21), and ClangSharp must match its own libclang version.
    LLVM_LIB="$(find /usr/lib -name 'libclang-*.so*' 2>/dev/null | sort -V | tail -1)"
    if [[ -z "$LLVM_LIB" ]]; then
        LLVM_LIB="$(find /usr/lib64 -name 'libclang*.so*' 2>/dev/null | sort -V | tail -1)"
    fi
    CLANGSHARP_LIB="$(find ~/.dotnet/tools/.store/clangsharppinvokegenerator -name 'libClangSharp.so' 2>/dev/null | head -1)"
fi

if [[ -z "$LLVM_LIB" ]]; then
    echo "ERROR: Could not find libclang. Install LLVM 21 (brew install llvm@21 or apt install libclang-21-dev)."
    exit 1
fi

LLVM_LIB_DIR="$(dirname "$LLVM_LIB")"
CLANGSHARP_LIB_DIR="$(dirname "$CLANGSHARP_LIB")"

echo "   libclang: $LLVM_LIB_DIR"
echo "   libClangSharp: $CLANGSHARP_LIB_DIR"

echo "==> Locating clang builtin headers (resource dir)..."
# clang needs its own builtin headers (stddef.h, etc.) on the include path or every saucer header fails
# with "'stddef.h' file not found". The resource dir is version- and platform-specific, so resolve it at
# runtime instead of hardcoding a path. Anchor to the clang that ships beside the DETECTED libclang — a
# bare `clang` on PATH can be a different toolchain (e.g. Apple clang) whose headers mismatch this libclang.
RESOURCE_INCLUDE=""
for clang_bin in \
    "$(dirname "$LLVM_LIB_DIR")/bin/clang" \
    "/usr/lib/llvm-21/bin/clang" \
    "$(command -v clang-21 2>/dev/null || true)"; do
    if [[ -n "$clang_bin" && -x "$clang_bin" ]]; then
        rdir="$("$clang_bin" -print-resource-dir 2>/dev/null || true)"
        if [[ -n "$rdir" && -f "$rdir/include/stddef.h" ]]; then
            RESOURCE_INCLUDE="$rdir/include"
            break
        fi
    fi
done
# Fallback: glob the resource include next to the detected libclang / the well-known llvm-21 layout.
if [[ -z "$RESOURCE_INCLUDE" ]]; then
    for g in "$LLVM_LIB_DIR"/clang/*/include \
             "$(dirname "$LLVM_LIB_DIR")"/lib/clang/*/include \
             /usr/lib/llvm-21/lib/clang/*/include; do
        if [[ -f "$g/stddef.h" ]]; then
            RESOURCE_INCLUDE="$g"
            break
        fi
    done
fi
if [[ -z "$RESOURCE_INCLUDE" ]]; then
    echo "ERROR: could not locate clang builtin headers (stddef.h). On Linux install clang-21 (or" >&2
    echo "       libclang-common-21-dev); on macOS install llvm@21 (brew install llvm@21)." >&2
    exit 1
fi
echo "   resource include: $RESOURCE_INCLUDE"

echo "==> Clearing existing generated bindings..."
# Clear only the ClangSharp output subdirectory (matches --output in ryn-bindings.rsp). Clearing the
# whole $OUTPUT_DIR would also delete Generated/.editorconfig, which marks these files generated and
# suppresses analyzers on them — losing it breaks the build on the next regen.
rm -rf "$OUTPUT_DIR/Saucer.cs"
mkdir -p "$OUTPUT_DIR/Saucer.cs"

echo "==> Generating C# bindings from saucer headers..."
cd "$CONFIG_DIR"

# ClangSharp returns a NON-ZERO exit code whenever it emits warnings. The saucer headers use a
# 'Visibility' attribute it does not model, so it always warns "Generated bindings may be incomplete" —
# benign, the bindings we ship are generated with these same warnings. Don't let that abort the script
# (set -e): capture the code, then gate real success on actual file generation + a clean build below,
# which catches any genuine failure (missing/garbage bindings won't compile).
set +e
if [[ "$(uname)" == "Darwin" ]]; then
    DYLD_LIBRARY_PATH="$LLVM_LIB_DIR:$CLANGSHARP_LIB_DIR" ClangSharpPInvokeGenerator @ryn-bindings.rsp --additional -isystem --additional "$RESOURCE_INCLUDE"
else
    LD_LIBRARY_PATH="$LLVM_LIB_DIR:$CLANGSHARP_LIB_DIR" ClangSharpPInvokeGenerator @ryn-bindings.rsp --additional -isystem --additional "$RESOURCE_INCLUDE"
fi
CLANGSHARP_RC=$?
set -e
if [[ "$CLANGSHARP_RC" -ne 0 ]]; then
    echo "   note: ClangSharp exited $CLANGSHARP_RC (benign 'Unsupported attribute' warnings); verifying output below"
fi

FILE_COUNT=$(find "$OUTPUT_DIR" -name '*.cs' | wc -l | tr -d ' ')
echo "==> Generated $FILE_COUNT C# files"
if [[ "$FILE_COUNT" -eq 0 ]]; then
    echo "ERROR: ClangSharp produced no .cs files — treating as a hard failure." >&2
    exit 1
fi

echo "==> Verifying build..."
cd "$REPO_ROOT"
dotnet build src/Ryn.Interop/Ryn.Interop.csproj -c Release --nologo -v quiet

echo "==> Done! Bindings regenerated successfully."
