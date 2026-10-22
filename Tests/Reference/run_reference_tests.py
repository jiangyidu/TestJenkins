#!/usr/bin/env python3
"""Managed reference-assistance tests; no Unity GPU/native objects are created."""
import argparse
import os
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('editor', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    data = args.editor.resolve() / 'Data'
    mono = data / 'MonoBleedingEdge/bin/mono'
    compiler = data / 'Tools/RoslynNet46/csc.exe'
    output = root / 'Tests/Results/reference'
    output.mkdir(parents=True, exist_ok=True)
    refs = sorted((data / 'Managed/UnityEngine').glob('*.dll')) + [
        data / 'Managed/UnityEditor.dll', data / 'UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll']
    sources = sorted((root / 'Assets/PrefabToPsd/Editor/Core').glob('*.cs'))
    sources += [root / ('Assets/PrefabToPsd/Editor/Unity/' + name) for name in
                ('PrefabPsdOptions.cs', 'PrefabPsdMetadata.cs', 'PrefabPsdReference.cs')]
    sources += [Path(__file__).with_name('ReferenceSelfTest.cs')]
    response = output / 'reference.rsp'
    lines = ['-nologo', '-langversion:6', '-define:UNITY_EDITOR', '-target:exe',
             '-out:"' + str(output / 'ReferenceSelfTest.exe') + '"']
    lines += ['-r:"' + str(p) + '"' for p in refs]
    lines += ['"' + str(p) + '"' for p in sources]
    response.write_text('\n'.join(lines), encoding='utf-8')
    result = subprocess.run([str(mono), str(compiler), '@' + str(response)],
        text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / 'compile.log').write_text(result.stdout, encoding='utf-8')
    if result.returncode:
        print(result.stdout, end='')
        return result.returncode
    env = os.environ.copy()
    env['MONO_PATH'] = os.pathsep.join(str(p) for p in
        (data / 'Managed', data / 'Managed/UnityEngine', data / 'UnityExtensions/Unity/GUISystem'))
    result = subprocess.run([str(mono), str(output / 'ReferenceSelfTest.exe'),
                            str(output / 'fixtures'), str(root / 'Tests/IntegratedCore/srgb.icc')],
                           env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / 'reference.log').write_text(result.stdout, encoding='utf-8')
    print(result.stdout, end='')
    return result.returncode


if __name__ == '__main__':
    raise SystemExit(main())
