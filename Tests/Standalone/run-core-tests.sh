#!/usr/bin/env bash
# Reproducible Linux/macOS checks, using an existing Unity 2018.4 Editor installation.
# UNITY_EDITOR_PATH must identify the directory containing Data/ (Linux), or Contents/
# (macOS). PYTHON may identify a Python virtual environment; dependencies are not installed here.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
package_dir="$(cd -- "$script_dir/../.." && pwd)"
result_dir="${1:-$package_dir/Tests/Results/core}"
mkdir -p "$result_dir"
result_dir="$(cd -- "$result_dir" && pwd)"

if [[ -z "${UNITY_EDITOR_PATH:-}" ]]; then
    printf '%s\n' 'Set UNITY_EDITOR_PATH to the Unity Editor directory containing Data/.' >&2
    exit 2
fi
unity_data="$UNITY_EDITOR_PATH/Data"
if [[ ! -d "$unity_data" && -d "$UNITY_EDITOR_PATH/Contents" ]]; then
    unity_data="$UNITY_EDITOR_PATH/Contents"
fi
mono_bin="$unity_data/MonoBleedingEdge/bin/mono"
compiler="$unity_data/MonoBleedingEdge/lib/mono/4.5/mcs.exe"
if [[ ! -x "$mono_bin" || ! -f "$compiler" ]]; then
    printf '%s\n' 'Unity Mono compiler not found at the supplied Editor path.' >&2
    exit 2
fi

"$mono_bin" --version > "$result_dir/toolchain.txt"
"$mono_bin" "$compiler" -langversion:6 -warnaserror+ \
    "-out:$result_dir/CoreSelfTest.exe" \
    "$package_dir/Assets/PrefabToPsd/Editor/Core/"*.cs \
    "$script_dir/CoreSelfTest.cs" 2>&1 | tee "$result_dir/compile.log"
"$mono_bin" "$result_dir/CoreSelfTest.exe" "$result_dir/fixtures" 2>&1 | tee "$result_dir/core.log"
"${PYTHON:-python3}" "$script_dir/verify_psd.py" "$result_dir/fixtures" 2>&1 | tee "$result_dir/interop.log"
