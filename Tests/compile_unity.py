#!/usr/bin/env python3
"""Compile against an installed Unity Editor's real managed assemblies (no Editor license needed).

This checks API and language compatibility; it does NOT execute Unity rendering.
Usage: python Tests/compile_unity.py /path/to/Unity/Editor
"""
import argparse
import pathlib
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("editor", type=pathlib.Path)
    args = parser.parse_args()
    editor = args.editor.resolve()
    if editor.is_file():
        editor = editor.parent
    if not (editor / "Data").is_dir() and (editor / "Editor/Data").is_dir():
        editor /= "Editor"
    data = editor / "Data"
    if not data.is_dir():
        parser.error("Expected the Unity 2018.4 Editor directory containing Data/ (Linux/Windows).")
    mono_dir = data / "MonoBleedingEdge"
    mono = next((p for p in (mono_dir / "bin/mono", mono_dir / "bin/mono.exe",
                             mono_dir / "bin-linux64/mono") if p.is_file()), None)
    compiler = data / "Tools/RoslynNet46/csc.exe"
    if not compiler.is_file():
        compiler = mono_dir / "lib/mono/4.5/csc.exe"
    if mono is None or not compiler.is_file():
        parser.error("Unity bundled Mono/C# compiler was not found.")
    root = pathlib.Path(__file__).resolve().parent.parent
    result = root / "Tests/Results"
    result.mkdir(parents=True, exist_ok=True)
    sources = sorted((root / "Assets/PrefabToPsd").rglob("*.cs"))
    refs = sorted((data / "Managed/UnityEngine").glob("*.dll"))
    refs += [data / "Managed/UnityEditor.dll", data / "UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll"]
    for ref in refs:
        if not ref.is_file():
            parser.error("Missing reference: " + str(ref))
    lines = ["-nologo", "-target:library", "-langversion:6",
             "-define:UNITY_EDITOR,UNITY_2018_4,UNITY_2018_4_OR_NEWER",
             '-out:"' + str(result / "PrefabToPsd.Compatibility.dll") + '"']
    lines += ['-r:"' + str(p) + '"' for p in refs]
    lines += ['"' + str(p) + '"' for p in sources]
    rsp = result / "compile.rsp"
    rsp.write_text("\n".join(lines), encoding="utf-8")
    process = subprocess.run([str(mono), str(compiler), "@" + str(rsp)],
                             text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    report = ("Real Unity Editor API compile; C# language version 6\n"
              + "Compiler: " + str(compiler) + "\n"
              + "Sources: " + str(len(sources)) + "\n"
              + "References: " + str(len(refs)) + "\n"
              + process.stdout
              + ("PASS: all package C# files compiled.\n" if process.returncode == 0 else "FAIL\n")
              + "This is compilation only, not a graphics/editor integration test.\n")
    (result / "unity-api-compile.txt").write_text(report, encoding="utf-8")
    print(report, end="")
    return process.returncode


if __name__ == "__main__":
    sys.exit(main())
