#!/usr/bin/env bash
# Uses the compiler shipped with Unity2018.4.36f1, without starting Unity itself.
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
package_dir="$(cd -- "$script_dir/../.." && pwd)"
result_dir="${1:-$package_dir/Tests/Results/integrated-core/resources}"
mkdir -p "$result_dir"
result_dir="$(cd -- "$result_dir" && pwd)"
if [[ -z "${UNITY_EDITOR_PATH:-}" ]]; then
    printf '%s\n' 'Set UNITY_EDITOR_PATH to the Unity Editor directory containing Data/.' >&2
    exit 2
fi
unity_data="$UNITY_EDITOR_PATH/Data"
mono_bin="$unity_data/MonoBleedingEdge/bin/mono"
compiler="$unity_data/Tools/RoslynNet46/csc.exe"
if [[ ! -x "$mono_bin" || ! -f "$compiler" ]]; then
    printf '%s\n' 'The Unity2018.4 Mono/Roslyn compiler was not found.' >&2
    exit 2
fi
"$mono_bin" "$compiler" -nologo -langversion:6 -warnaserror+ -target:exe \
    "-out:$result_dir/ResourceSelfTests.exe" \
    "$package_dir"/Assets/PrefabToPsd/Editor/Core/*.cs \
    "$script_dir/ResourceSelfTests.cs" 2>&1 | tee "$result_dir/compile.log"
"$mono_bin" "$result_dir/ResourceSelfTests.exe" "$script_dir/srgb.icc" "$result_dir" \
    2>&1 | tee "$result_dir/resource-tests.log"
"${PYTHON:-python3}" "$script_dir/verify_resources.py" "$result_dir" \
    2>&1 | tee "$result_dir/independent-resources.log"
