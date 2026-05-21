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
    LLVM_LIB="$(find /usr/lib -name 'libclang-*.so*' 2>/dev/null | head -1)"
    if [[ -z "$LLVM_LIB" ]]; then
        LLVM_LIB="$(find /usr/lib64 -name 'libclang*.so*' 2>/dev/null | head -1)"
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

echo "==> Clearing existing generated bindings..."
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "==> Generating C# bindings from saucer headers..."
cd "$CONFIG_DIR"

if [[ "$(uname)" == "Darwin" ]]; then
    DYLD_LIBRARY_PATH="$LLVM_LIB_DIR:$CLANGSHARP_LIB_DIR" ClangSharpPInvokeGenerator @ryn-bindings.rsp
else
    LD_LIBRARY_PATH="$LLVM_LIB_DIR:$CLANGSHARP_LIB_DIR" ClangSharpPInvokeGenerator @ryn-bindings.rsp
fi

FILE_COUNT=$(find "$OUTPUT_DIR" -name '*.cs' | wc -l | tr -d ' ')
echo "==> Generated $FILE_COUNT C# files"

echo "==> Verifying build..."
cd "$REPO_ROOT"
dotnet build src/Ryn.Interop/Ryn.Interop.csproj -c Release --nologo -v quiet

echo "==> Done! Bindings regenerated successfully."
