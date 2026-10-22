#!/usr/bin/env python3
"""Run compilation and managed integration checks. Does not launch Unity GPU tests.

python Tests/run_all_checks.py /path/to/Unity/Editor [--original-importer /path/PsdTOUgui]
Install Tests/Standalone/requirements.txt in the invoking Python environment first.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("editor", type=Path)
    parser.add_argument("--original-importer", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    results = root / "Tests/Results"
    results.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env["UNITY_EDITOR_PATH"] = str(args.editor.resolve())
    env["PYTHON"] = sys.executable
    jobs = [
        ("compile", [sys.executable, str(root / "Tests/compile_unity.py"), str(args.editor)]),
        ("core", ["bash", str(root / "Tests/IntegratedCore/run-integrated-core-tests.sh")]),
        ("reference", [sys.executable, str(root / "Tests/Reference/run_reference_tests.py"), str(args.editor)]),
        ("workflow", [sys.executable, str(root / "Tests/Workflow/run-workflow-tests.py"), str(args.editor)]),
        ("probe", [sys.executable, str(root / "Tests/Probe/run_probe_tests.py"), str(args.editor),
                   "--output", str(results / "probe")])
    ]
    if args.original_importer:
        jobs.append(("original-reader", [sys.executable, str(root / "Tests/OriginalReader/run_original_reader.py"),
                                        str(args.editor), str(args.original_importer)]))
    options_text = (root / "Assets/PrefabToPsd/Editor/Unity/PrefabPsdOptions.cs").read_text(encoding="utf-8")
    version = re.search(r'const string Version = "([^"]+)"', options_text).group(1)
    status = {"target_unity": "2018.4.36f1", "version": version, "jobs": [],
              "unity_gpu_executed": False, "photoshop_gui_executed": False}
    summary = results / "all-checks-summary.json"
    for name, command in jobs:
        print("Running " + name, flush=True)
        process = subprocess.run(command, env=env, cwd=root, text=True,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (results / ("all-" + name + ".log")).write_text(process.stdout, encoding="utf-8")
        status["jobs"].append({"name": name, "exit_code": process.returncode})
        summary.write_text(json.dumps(status, indent=2), encoding="utf-8")
        if process.returncode:
            print(process.stdout, end="")
            return process.returncode
        print("PASS " + name, flush=True)
    core = json.loads((results / "integrated-core/summary.json").read_text(encoding="utf-8"))
    reference = (results / "reference/reference.log").read_text(encoding="utf-8")
    workflow = (results / "workflow/results.log").read_text(encoding="utf-8")
    status["core_assertions"] = core["total_managed_assertions"]
    status["reference_assertions"] = int(re.search(r"PASS: (\d+) reference-policy", reference).group(1))
    status["workflow_assertions"] = int(re.search(r"PASS: (\d+) workflow pure", workflow).group(1))
    probe = json.loads((results / "probe/summary.json").read_text(encoding="utf-8"))
    status["probe_assertions"] = probe["assertions"]
    status["managed_assertions"] = sum(status[key] for key in
                                      ("core_assertions", "reference_assertions", "workflow_assertions", "probe_assertions"))
    status["independent_psd_files"] = core["independent_psd_files"]
    status["independent_icc_profiles"] = core["independent_icc_profiles"]
    if args.original_importer:
        text = (results / "original-reader.txt").read_text(encoding="utf-8")
        status["original_reader_assertions"] = int(re.search(r"PASS: (\d+) original-reader", text).group(1))
    status["source_sha256"] = {
        path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted((root / "Assets/PrefabToPsd").rglob("*.cs"))
    }
    summary.write_text(json.dumps(status, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in status.items() if key != "source_sha256"}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
