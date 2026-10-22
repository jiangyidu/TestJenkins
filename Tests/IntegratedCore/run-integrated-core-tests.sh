#!/usr/bin/env bash
# Reproduce core baseline + strict reader/atomic verification + safe resource tests.
# Requires UNITY_EDITOR_PATH and a Python environment with Standalone/requirements.txt.
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
package_dir="$(cd -- "$script_dir/../.." && pwd)"
result_dir="${1:-$package_dir/Tests/Results/integrated-core}"
mkdir -p "$result_dir/reader"
result_dir="$(cd -- "$result_dir" && pwd)"
if [[ -z "${UNITY_EDITOR_PATH:-}" ]]; then
    printf '%s\n' 'Set UNITY_EDITOR_PATH to the Unity2018.4.36f1 Editor directory containing Data/.' >&2
    exit 2
fi
unity_data="$UNITY_EDITOR_PATH/Data"
mono_bin="$unity_data/MonoBleedingEdge/bin/mono"
compiler="$unity_data/Tools/RoslynNet46/csc.exe"
bash "$package_dir/Tests/Standalone/run-core-tests.sh" "$result_dir/baseline"
"$mono_bin" "$compiler" -nologo -target:exe -langversion:6 -warnaserror+ \
    "-out:$result_dir/reader/ReaderSelfTests.exe" \
    "$package_dir"/Assets/PrefabToPsd/Editor/Core/*.cs \
    "$script_dir/ReaderSelfTests.cs" 2>&1 | tee "$result_dir/reader/compile.log"
"$mono_bin" "$result_dir/reader/ReaderSelfTests.exe" "$result_dir/reader" \
    "$result_dir/baseline/fixtures" 2>&1 | tee "$result_dir/reader/results.log"
bash "$script_dir/run-resource-tests.sh" "$result_dir/resources"
