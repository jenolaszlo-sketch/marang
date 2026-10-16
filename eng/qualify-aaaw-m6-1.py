#!/usr/bin/env python3
"""Run the existing M5 qualifier unchanged, then verify M6.1 evidence."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import platform
import re
import subprocess
import sys
import time
import traceback
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
OUT_ROOT = ROOT / "artifacts" / "aaaw-m6-1"
CLASS_FILES = {
    "RepositoryEffectJournalIntegrityTests": ROOT / "tests/Marang.Tests/RepositoryEffectJournalIntegrityTests.cs",
    "RepositoryRecoveryInspectorTests": ROOT / "tests/Marang.Tests/RepositoryRecoveryInspectorTests.cs",
    "RepositoryRecoveryEntryProcessTests": ROOT / "tests/Marang.Tests/RepositoryRecoveryEntryProcessTests.cs",
    "RepositoryRecoveryInspectorProcessTests": ROOT / "tests/Marang.Tests/RepositoryRecoveryInspectorProcessTests.cs",
    "RepositoryRecoveryAdmissionTests": ROOT / "tests/Marang.Tests/RepositoryRecoveryAdmissionTests.cs",
}
FRAMEWORK_CLASSES = {
    "net8.0": (
        "RepositoryEffectJournalIntegrityTests",
        "RepositoryRecoveryInspectorTests",
        "RepositoryRecoveryAdmissionTests",
    ),
    "net10.0": tuple(CLASS_FILES),
}
PROCESS_CLASSES = (
    "RepositoryRecoveryEntryProcessTests",
    "RepositoryRecoveryInspectorProcessTests",
)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def windows_inventory(argv: list[str]) -> dict[str, Any]:
    result = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=30)
    return {"argv": argv, "exitCode": result.returncode, "stdout": result.stdout,
            "stderr": result.stderr, "ok": result.returncode == 0}


def load_module(path: Path, name: str) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load qualification module: {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def class_source(class_name: str) -> str:
    text = CLASS_FILES[class_name].read_text(encoding="utf-8")
    match = re.search(rf"\bclass\s+{re.escape(class_name)}\b", text)
    if not match:
        raise ValueError(f"Expected M6 test class is missing: {class_name}")
    opening = text.find("{", match.end())
    if opening < 0:
        raise ValueError(f"Test class has no body: {class_name}")
    depth = 0
    index = opening
    state = "normal"
    raw_quotes = 0
    while index < len(text):
        char = text[index]
        following = text[index:index + 3]
        if state == "line-comment":
            if char == "\n":
                state = "normal"
        elif state == "block-comment":
            if text[index:index + 2] == "*/":
                state = "normal"
                index += 1
        elif state == "string":
            if char == "\\":
                index += 1
            elif char == '"':
                state = "normal"
        elif state == "char":
            if char == "\\":
                index += 1
            elif char == "'":
                state = "normal"
        elif state == "raw-string":
            if char == '"' and text[index:index + raw_quotes] == '"' * raw_quotes:
                state = "normal"
                index += raw_quotes - 1
        elif following == '"""':
            state = "raw-string"
            raw_quotes = 3
            index += 2
        elif text[index:index + 2] == "//":
            state = "line-comment"
            index += 1
        elif text[index:index + 2] == "/*":
            state = "block-comment"
            index += 1
        elif char == '"':
            state = "string"
        elif char == "'":
            state = "char"
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return text[opening + 1:index]
        index += 1
    raise ValueError(f"Test class body is unclosed: {class_name}")


def expected_cases(class_name: str) -> int:
    body = class_source(class_name)
    pending: list[str] = []
    expected = 0
    for line in body.splitlines():
        stripped = line.strip()
        if stripped.startswith("["):
            pending.append(stripped)
            continue
        if re.search(r"\bpublic\s+(?:async\s+)?(?:Task(?:\s*<[^>]+>)?|void)\s+\w+\s*\(", line):
            attributes = " ".join(pending)
            if re.search(r"\[\s*Fact\s*(?:\(\s*\))?\s*\]", attributes):
                expected += 1
            elif re.search(r"\[\s*Theory\s*(?:\(\s*\))?\s*\]", attributes):
                rows = len(re.findall(r"\[\s*InlineData\s*\(", attributes))
                if rows == 0:
                    raise ValueError(f"M6 theory must enumerate InlineData rows: {class_name}: {line.strip()}")
                expected += rows
            pending.clear()
            continue
        if stripped and not stripped.startswith("//"):
            pending.clear()
    if expected == 0:
        raise ValueError(f"No test cases derived from M6 class: {class_name}")
    return expected


def inline_scenarios(class_name: str) -> list[str]:
    return re.findall(r"\[\s*InlineData\s*\(\s*\"([^\"]+)\"", class_source(class_name))


def read_trx(path: Path) -> list[dict[str, str | None]]:
    import xml.etree.ElementTree as ET

    root = ET.parse(path).getroot()
    results: list[dict[str, str | None]] = []
    for node in root.iter():
        if node.tag.rsplit("}", 1)[-1] == "UnitTestResult":
            results.append({"testName": node.attrib.get("testName"), "outcome": node.attrib.get("outcome")})
    return results


def verify_m6_trx(trx_paths: dict[str, Path]) -> dict[str, Any]:
    result: dict[str, Any] = {"passed": True, "frameworks": {}}
    for framework, classes in FRAMEWORK_CLASSES.items():
        path = trx_paths.get(framework)
        framework_record: dict[str, Any] = {"trxPresent": bool(path and path.is_file()), "classes": {}}
        if path is None or not path.is_file():
            result["passed"] = False
            framework_record["error"] = "Full-suite TRX missing for framework."
            result["frameworks"][framework] = framework_record
            continue
        try:
            tests = read_trx(path)
        except Exception as exc:
            result["passed"] = False
            framework_record["error"] = f"{type(exc).__name__}: {exc}"
            result["frameworks"][framework] = framework_record
            continue
        for class_name in classes:
            expected = expected_cases(class_name)
            selected = [test for test in tests if class_name in (test["testName"] or "")]
            all_passed = len(selected) == expected and all(test["outcome"] == "Passed" for test in selected)
            framework_record["classes"][class_name] = {
                "expectedCasesFromSource": expected,
                "trxCases": len(selected),
                "allPassed": all_passed,
                "tests": selected,
            }
            result["passed"] = result["passed"] and all_passed
        result["frameworks"][framework] = framework_record
    return result


def fresh_case_directories(root: Path, scenarios: list[str], started_epoch: float) -> dict[str, list[Path]]:
    found: dict[str, list[Path]] = {}
    for scenario in scenarios:
        candidates = [path for path in root.glob(scenario + "-*") if path.is_dir() and path.stat().st_mtime >= started_epoch]
        found[scenario] = sorted(candidates)
    return found


def verify_process_artifacts(started_epoch: float) -> dict[str, Any]:
    entry_scenarios = inline_scenarios("RepositoryRecoveryEntryProcessTests")
    inspector_scenarios = inline_scenarios("RepositoryRecoveryInspectorProcessTests")
    entry_root = OUT_ROOT / "process-probes"
    inspector_root = OUT_ROOT / "inspection-process-probes"
    entry_dirs = fresh_case_directories(entry_root, entry_scenarios, started_epoch)
    inspector_dirs = fresh_case_directories(inspector_root, inspector_scenarios, started_epoch)
    details: dict[str, Any] = {"passed": True, "entryRefusals": {}, "freshProcessInspection": {}}
    for scenario, paths in entry_dirs.items():
        case_ok = len(paths) == 1 and (paths[0] / "phase2-error.json").is_file()
        record: dict[str, Any] = {"freshArtifactDirectories": len(paths), "requiredFailureEvidencePresent": False}
        if case_ok:
            directory = paths[0]
            try:
                error = json.loads((directory / "phase2-error.json").read_text(encoding="utf-8"))
                record["error"] = error
                preservation = json.loads((directory / "preservation.json").read_text(encoding="utf-8"))
                record["preservation"] = preservation
                receipts = [json.loads((directory / f"refusal-receipt-{attempt}.json").read_text(encoding="utf-8"))
                            for attempt in (1, 2)]
                record["receipts"] = receipts
                evidence_absent = all(not (directory / name).exists() for name in (
                    "workflow-process-id.txt", "checkout-path.txt", "runtime/hufu.db"))
                record["requiredFailureEvidencePresent"] = (
                    error.get("error") == "Marang.Runs.Integrity.RepositoryRecoveryRefusedException" and evidence_absent
                    and preservation.get("databaseUnchanged") is True and preservation.get("walContentPreserved") is True
                    and preservation.get("walLengthAfterInspection") in (None, 0)
                    and preservation.get("freshProcessAttempts") == 2
                    and len({receipt.get("CorrelationId") for receipt in receipts}) == 2
                    and all(receipt.get("Cause") == "NonzeroExit" and receipt.get("RootExitCode") == 1
                            and receipt.get("Cleanup") == "Confirmed" and receipt.get("CaptureStatus") == "Complete"
                            for receipt in receipts))
            except Exception as exc:
                record["error"] = f"{type(exc).__name__}: {exc}"
        case_ok = case_ok and record["requiredFailureEvidencePresent"]
        details["entryRefusals"][scenario] = record
        details["passed"] = details["passed"] and case_ok
    for scenario, paths in inspector_dirs.items():
        case_ok = len(paths) == 1
        record: dict[str, Any] = {"freshArtifactDirectories": len(paths), "requiredEvidencePresent": False}
        if case_ok:
            directory = paths[0]
            required = ("integrity-result.json", "stdout.log", "stderr.log", "preservation.json")
            record["requiredEvidencePresent"] = all((directory / name).is_file() for name in required)
            try:
                observed = json.loads((directory / "integrity-result.json").read_text(encoding="utf-8"))
                preservation = json.loads((directory / "preservation.json").read_text(encoding="utf-8"))
                expected_ready = scenario in {"healthy-initial", "healthy-pending", "healthy-committed"}
                record["result"] = observed
                record["resultMatchesCase"] = observed.get("canRecover") is expected_ready and (
                    observed.get("status") == ("Ready" if expected_ready else
                                                "MissingStateShape" if scenario == "table-missing" else "InconsistentState"))
                record["preservation"] = preservation
                record["evidencePreserved"] = preservation.get("databaseUnchanged") is True and preservation.get("walUnchanged") is True
            except Exception as exc:
                record["resultMatchesCase"] = False
                record["evidencePreserved"] = False
                record["error"] = f"{type(exc).__name__}: {exc}"
            record["requiredEvidencePresent"] = (record["requiredEvidencePresent"] and record.get("resultMatchesCase") is True
                                                  and record.get("evidencePreserved") is True)
            details["passed"] = details["passed"] and record["requiredEvidencePresent"]
        else:
            details["passed"] = False
        details["freshProcessInspection"][scenario] = record
    return details


def verify_m5_semantics(shared: Any, started_epoch: float) -> dict[str, Any]:
    main_path = shared.find_newest(ROOT / "artifacts/aaaw-m4/process-probes", "multi-effect-result.json", started_epoch)
    partial_path = shared.find_newest(ROOT / "artifacts/aaaw-m4/process-probes", "multi-effect-K4-result.json", started_epoch)
    negative_path = shared.find_newest(ROOT / "artifacts/aaaw-m5/process-probes", "multi-effect-reconcile-2.json", started_epoch)
    main = shared.check_m4_scenario(main_path, started_epoch) if main_path else {"observed": False, "verified": False}
    partial = shared.check_k4(partial_path, started_epoch) if partial_path else {"observed": False, "verified": False}
    negative = shared.check_negative_control(negative_path.parent, started_epoch) if negative_path else {"observed": False, "verified": False}
    barriers = shared.check_barriers(main_path.parent if main_path else ROOT / "artifacts/missing-m6-main",
                                     partial_path.parent if partial_path else ROOT / "artifacts/missing-m6-k4", started_epoch)
    passed = all((main.get("verified") is True, partial.get("verified") is True,
                  negative.get("verified") is True, len(barriers) == 12,
                  all(item.get("verified") is True for item in barriers.values())))
    return {"passed": passed, "canonical": main, "K4": partial, "actualStateControl": negative, "barriers": barriers}


def add_tree_files(selected: set[Path], root: Path, started_epoch: float | None = None) -> None:
    if not root.exists():
        return
    for path in root.rglob("*"):
        if path.is_file() and (started_epoch is None or path.stat().st_mtime >= started_epoch):
            selected.add(path)


def write_report(record: dict[str, Any], output: Path, selected: set[Path]) -> None:
    output.mkdir(parents=True, exist_ok=True)
    report_path = output / "qualification.json"
    record["capturedArtifacts"] = {"path": (output / "captured-artifacts.zip").relative_to(ROOT).as_posix(),
                                   "memberCount": len(selected) + 1}
    report_path.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    selected.add(report_path)
    manifest: list[dict[str, Any]] = []
    archive_path = output / "captured-artifacts.zip"
    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(selected):
            try:
                relative = path.relative_to(ROOT).as_posix()
            except ValueError:
                continue
            data = path.read_bytes()
            import hashlib
            manifest.append({"path": relative, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            archive.writestr(relative, data)
        archive.writestr("capture-manifest.json", json.dumps({"files": manifest}, indent=2, sort_keys=True) + "\n")


def validate_windows(args: argparse.Namespace) -> int:
    shared = load_module(ROOT / "eng/qualify-aaaw-m5.py", "aaaw_m5_shared_windows")
    started = float(args.started_epoch)
    output = OUT_ROOT / "windows-qualification"
    evidence: dict[str, Any] = {
        "schema": "aaaw-m6-1-qualification/v1", "platform": "windows", "startedAt": utc_now(),
        "source": {"start": {"commit": args.source_commit, "clean": True}, "end": shared.source_state()},
        "m5": {}, "m6": {}, "qualificationPassed": False,
    }
    try:
        m5_runs: list[dict[str, Any]] = []
        trx_paths: dict[str, Path] = {}
        for framework in ("net8.0", "net10.0"):
            path = Path(args.test_results_dir).resolve() / f"m6-{framework}.trx"
            trx_paths[framework] = path
            run = shared.parse_trx(path, framework) if path.is_file() else {"framework": framework, "missingTrx": True, "passed": False}
            # Match the reviewed macOS policy: allow at most the one exact,
            # case-sensitive filesystem-dependent test to be skipped.
            skips = run.get("skippedTests", [])
            allowed_skip = "Marang.Tests.RepositoryPortabilityTests.Distinct_case_sensitive_git_roots_have_distinct_repository_identities"
            counters = run.get("counters", {})
            results_present = path.is_file() and run.get("testResultCount") == counters.get("total", -1)
            reviewed_skips = len(skips) <= 1 and all(result.get("testName") == allowed_skip for result in skips)
            run["skippedResultCount"] = len(skips)
            run["passedWithReviewedFilesystemSkip"] = bool(
                path.is_file() and run.get("commandOk", True) and reviewed_skips
                and not run.get("failedTests")
                and counters.get("failed", 0) == 0 and counters.get("error", 0) == 0
                and counters.get("timeout", 0) == 0 and counters.get("aborted", 0) == 0
                and counters.get("passed", 0) + len(skips) == counters.get("total", -1)
                and results_present and run.get("baselineMinimumMet") is True
                and (framework != "net10.0" or (
                    run.get("requiredM4AndM5ProcessTestsPresent") is True
                    and run.get("requiredM5ControlPassed") is True
                    and all(test.get("outcome") == "Passed" for test in run.get("m4ProcessTests", []))
                ))
            )
            run["passed"] = run["passedWithReviewedFilesystemSkip"]
            m5_runs.append(run)
        provenance: dict[str, Any] = {"passed": False}
        inventory: dict[str, Any] = {"passed": False}
        try:
            sdk_info = windows_inventory(["dotnet", "--info"])
            sdk_list = windows_inventory(["dotnet", "--list-sdks"])
            runtime_list = windows_inventory(["dotnet", "--list-runtimes"])
            runtimes = runtime_list.get("stdout", "").splitlines()
            sdks = sdk_list.get("stdout", "").splitlines()
            inventory = {
                "dotnetInfo": sdk_info, "dotnetSdks": sdk_list, "dotnetRuntimes": runtime_list,
                "hostOS": platform.platform(), "hostSystem": platform.system(), "hostArchitecture": platform.machine(),
                "hasSdk8": any(re.match(r"^8\.", line.strip()) for line in sdks),
                "hasSdk10": any(re.match(r"^10\.", line.strip()) for line in sdks),
                "net8RuntimeVersions": [line.strip() for line in runtimes if re.match(r"^Microsoft\.NETCore\.App 8\.", line.strip())],
                "net10RuntimeVersions": [line.strip() for line in runtimes if re.match(r"^Microsoft\.NETCore\.App 10\.", line.strip())],
            }
            inventory["passed"] = all(command.get("ok") for command in (sdk_info, sdk_list, runtime_list)) and all((
                inventory["hasSdk8"], inventory["hasSdk10"], inventory["net8RuntimeVersions"], inventory["net10RuntimeVersions"]
            ))
            provenance = {
                "m1Lock": str(shared.M1_LOCK), "m5RuntimeLock": str(shared.M5_LOCK),
                "m1Packages": shared.validate_package_lock(shared.M1_LOCK),
                "m5RuntimePackages": shared.validate_package_lock(shared.M5_LOCK, require_runtime_set=True),
                "passed": True,
            }
        except Exception as exc:
            provenance["failure"] = f"{type(exc).__name__}: {exc}"
            inventory["passed"] = False
            inventory.setdefault("failure", f"{type(exc).__name__}: {exc}")
        evidence["packageProvenance"] = provenance
        evidence["runtimeInventory"] = inventory
        semantics = verify_m5_semantics(shared, started)
        source_end = evidence["source"]["end"]
        source_ok = source_end.get("clean") is True and source_end.get("commit") == args.source_commit
        evidence["source"]["unchangedAndClean"] = source_ok
        evidence["m5"] = {"testRuns": m5_runs, "semantics": semantics,
                           "passed": all(run.get("passed") is True for run in m5_runs) and semantics["passed"]
                           and provenance.get("passed") is True and inventory.get("passed") is True
                           and not args.runner_failure,
                           "runnerFailures": args.runner_failure}
        evidence["m6"]["tests"] = verify_m6_trx(trx_paths)
        evidence["m6"]["processArtifacts"] = verify_process_artifacts(started)
        evidence["qualificationPassed"] = (source_ok and evidence["m5"]["passed"] and
                                            evidence["m6"]["tests"]["passed"] and evidence["m6"]["processArtifacts"]["passed"])
    except Exception as exc:
        evidence["failure"] = {"type": type(exc).__name__, "message": str(exc), "traceback": traceback.format_exc(limit=10)}
    finally:
        evidence["completedAt"] = utc_now()
        selected: set[Path] = set()
        add_tree_files(selected, Path(args.test_results_dir).resolve(), started)
        if (output / "runner.log").is_file():
            selected.add(output / "runner.log")
        for folder in ("aaaw-m4/process-probes", "aaaw-m5/process-probes", "aaaw-m6-1/process-probes", "aaaw-m6-1/inspection-process-probes"):
            add_tree_files(selected, ROOT / "artifacts" / folder, started)
        write_report(evidence, output, selected)
    print(json.dumps({"qualificationPassed": evidence["qualificationPassed"], "evidence": str(output / "qualification.json")}, indent=2))
    return 0 if evidence["qualificationPassed"] else 1


def run_existing_m5(platform_name: str) -> tuple[int, dict[str, Any] | None, Path]:
    if platform_name == "darwin":
        module = load_module(ROOT / "eng/qualify-aaaw-m5-macos.py", "aaaw_m5_macos")
        return module.main(), None, ROOT / "artifacts/aaaw-m5/macos-qualification"
    module = load_module(ROOT / "eng/qualify-aaaw-m5.py", "aaaw_m5_linux")
    result = module.main()
    evidence_path = module.ARTIFACTS / "qualification.json"
    evidence = json.loads(evidence_path.read_text(encoding="utf-8")) if evidence_path.is_file() else None
    return result, evidence, module.ARTIFACTS


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--validate-windows", action="store_true", help="validate TRX from the Windows PowerShell runner")
    parser.add_argument("--test-results-dir")
    parser.add_argument("--started-epoch")
    parser.add_argument("--source-commit")
    parser.add_argument("--runner-failure", action="append", default=[])
    args = parser.parse_args()
    if args.validate_windows:
        if platform.system() != "Windows" or not all((args.test_results_dir, args.started_epoch, args.source_commit)):
            parser.error("Windows validation requires Windows and --test-results-dir, --started-epoch, --source-commit")
        return validate_windows(args)

    system = platform.system()
    if system not in {"Linux", "Darwin"}:
        parser.error("Use --validate-windows from the Windows PowerShell qualification runner")
    started = time.time()
    platform_name = "macos" if system == "Darwin" else "linux"
    output = OUT_ROOT / f"{platform_name}-qualification"
    evidence: dict[str, Any] = {
        "schema": "aaaw-m6-1-qualification/v1", "platform": platform_name, "startedAt": utc_now(),
        "m5": {}, "m6": {}, "qualificationPassed": False,
    }
    shared = load_module(ROOT / "eng/qualify-aaaw-m5.py", "aaaw_m5_shared_m6")
    m5_rc = 1
    m5_root = ROOT / ("artifacts/aaaw-m5/macos-qualification" if system == "Darwin" else "artifacts/aaaw-m5/linux-qualification")
    try:
        m5_rc, m5_record, m5_root = run_existing_m5("darwin" if system == "Darwin" else "linux")
        if system == "Darwin":
            m5_record_path = m5_root / "qualification.json"
            m5_record = json.loads(m5_record_path.read_text(encoding="utf-8")) if m5_record_path.is_file() else None
        evidence["m5"] = {"returnCode": m5_rc, "qualification": m5_record,
                           "passed": m5_rc == 0 and isinstance(m5_record, dict) and m5_record.get("qualificationPassed") is True}
        trx_paths: dict[str, Path] = {}
        if isinstance(m5_record, dict):
            for run in m5_record.get("testRuns", []):
                framework = run.get("framework")
                trx = run.get("trx", {})
                raw_path = trx.get("path") if isinstance(trx, dict) else None
                if framework in FRAMEWORK_CLASSES and raw_path:
                    trx_paths[framework] = ROOT / raw_path
        if system == "Darwin" and not trx_paths:
            trx_paths = {framework: m5_root / f"m5-{framework}.trx" for framework in FRAMEWORK_CLASSES}
        evidence["m6"]["tests"] = verify_m6_trx(trx_paths)
        evidence["m6"]["processArtifacts"] = verify_process_artifacts(started)
        evidence["qualificationPassed"] = (evidence["m5"]["passed"] and evidence["m6"]["tests"]["passed"]
                                            and evidence["m6"]["processArtifacts"]["passed"])
    except Exception as exc:
        evidence["failure"] = {"type": type(exc).__name__, "message": str(exc), "traceback": traceback.format_exc(limit=10)}
    finally:
        evidence["completedAt"] = utc_now()
        selected: set[Path] = set()
        if m5_root.exists():
            if system == "Linux" and isinstance(evidence.get("m5", {}).get("qualification"), dict):
                run_id = evidence["m5"]["qualification"].get("runId")
                if run_id:
                    add_tree_files(selected, m5_root / run_id)
                add_tree_files(selected, m5_root, started)
                if (m5_root / "qualification.json").is_file():
                    selected.add(m5_root / "qualification.json")
            else:
                add_tree_files(selected, m5_root, started)
                if (m5_root / "qualification.json").is_file():
                    selected.add(m5_root / "qualification.json")
        for folder in ("aaaw-m4/process-probes", "aaaw-m5/process-probes", "aaaw-m6-1/process-probes", "aaaw-m6-1/inspection-process-probes"):
            add_tree_files(selected, ROOT / "artifacts" / folder, started)
        write_report(evidence, output, selected)
    print(json.dumps({"qualificationPassed": evidence["qualificationPassed"], "evidence": str(output / "qualification.json")}, indent=2))
    return 0 if evidence["qualificationPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
