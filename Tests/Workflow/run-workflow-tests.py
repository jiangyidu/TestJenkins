#!/usr/bin/env python3
"""Run managed workflow methods from the package's real Unity compatibility assembly.

python Tests/compile_unity.py /path/Unity/Editor
python Tests/Workflow/run-workflow-tests.py /path/Unity/Editor
No Unity native objects or rendering are invoked by these tests.
"""
from pathlib import Path
import argparse
import os
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('editor', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    data = args.editor.resolve() / 'Data'
    mono = data / 'MonoBleedingEdge/bin/mono'
    compiler = data / 'Tools/RoslynNet46/csc.exe'
    assembly = root / 'Tests/Results/PrefabToPsd.Compatibility.dll'
    if not assembly.is_file():
        parser.error('Compile the full package using Tests/compile_unity.py first.')
    output = root / 'Tests/Results/workflow'
    output.mkdir(parents=True, exist_ok=True)
    executable = output / 'WorkflowPureTests.exe'
    refs = sorted((data / 'Managed/UnityEngine').glob('*.dll'))
    refs += [data / 'Managed/UnityEditor.dll', data / 'UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll', assembly]
    lines = ['-nologo', '-langversion:6', '-target:exe', '-out:"' + str(executable) + '"']
    lines += ['-r:"' + str(p) + '"' for p in refs]
    lines += ['"' + str(Path(__file__).with_name('WorkflowPureTests.cs')) + '"']
    response = output / 'compile.rsp'
    response.write_text('\n'.join(lines), encoding='utf-8')
    compile_result = subprocess.run([str(mono), str(compiler), '@' + str(response)],
                                    text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / 'compile.log').write_text(compile_result.stdout, encoding='utf-8')
    if compile_result.returncode:
        print(compile_result.stdout, end='')
        return compile_result.returncode
    env = os.environ.copy()
    env['MONO_PATH'] = os.pathsep.join(str(p) for p in [assembly.parent, data / 'Managed',
        data / 'Managed/UnityEngine', data / 'UnityExtensions/Unity/GUISystem'])
    run = subprocess.run([str(mono), str(executable), str(output)], env=env,
                         text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / 'results.log').write_text(run.stdout, encoding='utf-8')
    print(run.stdout, end='')
    return run.returncode


if __name__ == '__main__':
    raise SystemExit(main())
