#!/usr/bin/env python3
"""Optional: compile the unchanged uploaded PsdTOUgui source and read our PSD fixtures.
Usage: python Tests/OriginalReader/run_original_reader.py UNITY_EDITOR_DIR PSDTOUGUI_DIR
The original plugin is not redistributed with this package.
"""
import argparse
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("editor", type=Path)
    parser.add_argument("original", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    data = args.editor.resolve() / "Data"
    mono = data / "MonoBleedingEdge/bin/mono"
    compiler = data / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
    out = root / "Tests/Results/original-reader-build"
    out.mkdir(parents=True, exist_ok=True)
    original = args.original.resolve()
    if not (original / "Editor/Readers/Managed/ManagedPsdDocument.cs").is_file():
        parser.error("Expected the extracted PsdTOUgui directory from the original upload.")
    sources = sorted(original.rglob("*.cs"))
    refs = sorted((data / "Managed/UnityEngine").glob("*.dll"))
    refs += [data / "Managed/UnityEditor.dll", data / "UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll"]
    response = out / "original.rsp"
    lines = ["-nologo", "-target:library", "-define:UNITY_EDITOR",
             '-out:"' + str(out / "Original.dll") + '"']
    lines += ['-r:"' + str(p) + '"' for p in refs]
    lines += ['"' + str(p) + '"' for p in sources]
    response.write_text("\n".join(lines), encoding="utf-8")
    subprocess.run([str(mono), str(compiler), "@" + str(response)], check=True)
    exe = out / "OriginalReaderCheck.exe"
    subprocess.run([str(mono), str(compiler), "-nologo", "-langversion:6", "-out:" + str(exe),
                    str(Path(__file__).with_name("OriginalReaderCheck.cs"))], check=True)
    env = os.environ.copy()
    env["MONO_PATH"] = os.pathsep.join(str(p) for p in
        [data / "Managed", data / "Managed/UnityEngine", data / "UnityExtensions/Unity/GUISystem"])
    fixtures = root / "Tests/Results/integrated-core/baseline/fixtures"
    if not fixtures.is_dir():
        fixtures = root / "Tests/Results/core/fixtures"
    if not fixtures.is_dir():
        parser.error("Run the integrated core tests first to generate PSD fixtures.")
    result = subprocess.run([str(mono), str(exe), str(out / "Original.dll"),
                             str(fixtures)], env=env, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (root / "Tests/Results/original-reader.txt").write_text(result.stdout, encoding="utf-8")
    print(result.stdout, end="")
    return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
