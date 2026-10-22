#!/usr/bin/env python3
"""Compile the real package and invoke its private GPU-test PSD reader without a GPU.

The production PSD writer receives independently specified pixel expectations;
the actual PrefabPsdSelfTests.PsdProbe is invoked using reflection. This tests
the PSD interpretation of the reported failure, not Unity rendering itself.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('editor', type=Path)
    parser.add_argument('--output', type=Path,
                        help='Optional test output directory; defaults beneath Tests/Probe.')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    data = args.editor.resolve() / 'Data'
    mono = data / 'MonoBleedingEdge/bin/mono'
    compiler = data / 'Tools/RoslynNet46/csc.exe'
    output = args.output.resolve() if args.output else Path(__file__).with_name('Results')
    output.mkdir(parents=True, exist_ok=True)
    executable = output / 'ProbeRegressionTests.exe'
    refs = sorted((data / 'Managed/UnityEngine').glob('*.dll'))
    refs += [data / 'Managed/UnityEditor.dll',
             data / 'UnityExtensions/Unity/GUISystem/UnityEngine.UI.dll']
    sources = sorted((root / 'Assets/PrefabToPsd').rglob('*.cs'))
    sources += [Path(__file__).with_name('ProbeRegressionTests.cs')]
    response = output / 'probe.rsp'
    lines = ['-nologo', '-langversion:6', '-target:exe',
             '-define:UNITY_EDITOR,UNITY_2018_4,UNITY_2018_4_OR_NEWER',
             '-out:"' + str(executable) + '"']
    lines += ['-r:"' + str(path) + '"' for path in refs]
    lines += ['"' + str(path) + '"' for path in sources]
    response.write_text('\n'.join(lines), encoding='utf-8')
    compile_run = subprocess.run([str(mono), str(compiler), '@' + str(response)],
                                 text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / 'compile.log').write_text(compile_run.stdout, encoding='utf-8')
    if compile_run.returncode:
        print(compile_run.stdout, end='')
        return compile_run.returncode
    env = os.environ.copy()
    env['MONO_PATH'] = os.pathsep.join(str(path) for path in
        (data / 'Managed', data / 'Managed/UnityEngine',
         data / 'UnityExtensions/Unity/GUISystem'))
    run = subprocess.run([str(mono), str(executable), str(output / 'fixtures')],
                         env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (output / 'results.log').write_text(run.stdout, encoding='utf-8')
    print(run.stdout, end='')
    match = re.search(r'PASS: (\d+) real-probe regression assertions', run.stdout)
    coverage = re.search(r'Coverage: (\d+) private-probe assertions; (\d+) capture-control assertions', run.stdout)
    summary = {'exit_code': run.returncode, 'assertions': int(match.group(1)) if match else 0,
               'production_scripts_compiled': len(sources) - 1,
               'unity_references': len(refs), 'actual_private_probe_invoked': bool(match),
               'unity_gpu_executed': False}
    if coverage:
        summary['private_probe_assertions'] = int(coverage.group(1))
        summary['capture_control_assertions'] = int(coverage.group(2))
    (output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    return run.returncode


if __name__ == '__main__':
    raise SystemExit(main())
