#!/usr/bin/env python3
"""Run the unchanged M6.1 native qualifier, then seal M6.2 transition evidence."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import platform
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
OUT_ROOT = ROOT / "artifacts" / "aaaw-m6-2"
OLD_SHA = "5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29"
OLD_BUILD = OUT_ROOT / "oldwriter-build.json"
CLASS_FILES = {
    "RepositoryProfileAdmissionTests": ROOT / "tests/Marang.Tests/RepositoryProfileAdmissionTests.cs",
    "RepositoryProfileCompatibilityTests": ROOT / "tests/Marang.Tests/RepositoryProfileCompatibilityTests.cs",
    "RepositoryProfileCompatibilityProcessTests": ROOT / "tests/Marang.Tests/RepositoryProfileCompatibilityProcessTests.cs",
}
FRAMEWORK_CLASSES = {
    "net8.0": ("RepositoryProfileAdmissionTests", "RepositoryProfileCompatibilityTests"),
    "net10.0": tuple(CLASS_FILES),
}
CASE_PREFIXES = (
    "K3", "K4", "K1-AfterBegin", "K1-AfterDdl", "K1-BeforeCommit", "K1-AfterCommit",
    "refusal-partial-column", "refusal-unknown-future",
)


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def load_module(path: Path, name: str) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load qualification module: {path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def git(*args: str) -> str:
    result = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, timeout=30)
    if result.returncode:
        raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr}")
    return result.stdout.strip()


def source_state() -> dict[str, Any]:
    return {"commit": git("rev-parse", "HEAD"), "clean": git("status", "--porcelain=v1", "--untracked-files=all") == "",
            "status": git("status", "--porcelain=v1", "--untracked-files=all").splitlines()}


def run_command(name: str, argv: list[str], timeout: int = 1800) -> dict[str, Any]:
    started = utc_now()
    environment = dict(os.environ)
    environment.update({"UseSharedCompilation": "false", "MSBUILDDISABLENODEREUSE": "1",
                        "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER": "1", "PYTHONDONTWRITEBYTECODE": "1"})
    result = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=timeout, env=environment)
    return {"name": name, "argv": argv, "cwd": str(ROOT), "startedAt": started, "finishedAt": utc_now(),
            "exitCode": result.returncode, "stdout": result.stdout, "stderr": result.stderr,
            "passed": result.returncode == 0}


def fresh_case_directories(root: Path, started_epoch: float) -> dict[str, list[Path]]:
    return {prefix: sorted(path for path in root.glob(prefix + "-*")
                           if path.is_dir() and path.stat().st_mtime >= started_epoch)
            for prefix in CASE_PREFIXES}


def verify_new_trx(trx_paths: dict[str, Path], shared: Any) -> dict[str, Any]:
    report: dict[str, Any] = {"passed": True, "frameworks": {}}
    shared.CLASS_FILES = CLASS_FILES
    for framework, classes in FRAMEWORK_CLASSES.items():
        trx = trx_paths.get(framework)
        item: dict[str, Any] = {"trxPresent": bool(trx and trx.is_file()), "classes": {}}
        if trx is None or not trx.is_file():
            item["error"] = "M6.2 framework TRX is missing."
            report["passed"] = False
            report["frameworks"][framework] = item
            continue
        tests = shared.read_trx(trx)
        for name in classes:
            expected = shared.expected_cases(name)
            matches = [test for test in tests if name in (test["testName"] or "")]
            passed = len(matches) == expected and all(test["outcome"] == "Passed" for test in matches)
            item["classes"][name] = {"expectedCasesFromSource": expected, "trxCases": len(matches),
                                     "allPassed": passed, "tests": matches}
            report["passed"] = report["passed"] and passed
        report["frameworks"][framework] = item
    return report


def read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def verify_stop(path: Path, required_cause: str) -> tuple[bool, dict[str, Any]]:
    if not path.is_file():
        return False, {"missing": str(path)}
    value = read_json(path)
    root = value.get("root", {})
    valid = (root.get("cause") == required_cause and root.get("cleanup") == "Confirmed"
             and root.get("captureStatus") == "Complete")
    if required_cause == "ExplicitStop":
        # OwnedProbeRun.WriteStopEvidenceAsync emits OriginalWorkflowProcessId
        # beside Root, not inside it (JsonSerializerDefaults.Web => camelCase).
        original_pid = value.get("originalWorkflowProcessId")
        valid = valid and isinstance(original_pid, int) and original_pid > 0
    return valid, value


def verify_process_artifacts(started_epoch: float, shared: Any, expected_new_sha: str) -> dict[str, Any]:
    root = OUT_ROOT / "process-probes"
    directories = fresh_case_directories(root, started_epoch)
    result: dict[str, Any] = {"passed": True, "cases": {}}
    new_probe_path = ROOT / "tests/Marang.RestartProbe/bin/Release/net10.0/Marang.RestartProbe.dll"
    new_probe_sha = hashlib.sha256(new_probe_path.read_bytes()).hexdigest() if new_probe_path.is_file() else None
    for prefix, paths in directories.items():
        case: dict[str, Any] = {"freshDirectories": len(paths), "passed": False}
        if len(paths) != 1:
            case["error"] = "Expected exactly one fresh process artifact directory."
            result["passed"] = False
            result["cases"][prefix] = case
            continue
        directory = paths[0]
        case["directory"] = str(directory.relative_to(ROOT).as_posix())
        if prefix in ("K3", "K4"):
            evidence_path = directory / "profile-compatibility-case.json"
            evidence = read_json(evidence_path) if evidence_path.is_file() else {}
            stop_ok, stop = verify_stop(directory / f"lifecycle-stop-{prefix}.json", "ExplicitStop")
            apply_log = directory / "physical-applies.log"
            phase_receipts = list(directory.glob("natural-multi-effect-v1-*.json"))
            legacy_admission_receipts = [read_json(path) for path in sorted(directory.glob("refusal-multi-effect-v1-*.json"))]
            upgrade_receipts = [read_json(path) for path in sorted(directory.glob("natural-profile-upgrade-*.json"))]
            phase_receipts_json = [read_json(path) for path in sorted(phase_receipts)]
            repeated_phase = "multi-effect-v1-catch-up" if prefix == "K4" else "multi-effect-v1"
            catchup_path = directory / "profile-current-partial-catchup.json"
            catchup = read_json(catchup_path) if prefix == "K4" and catchup_path.is_file() else {}
            catchup_ok = (prefix != "K4" or (catchup_path.is_file()
                           and catchup.get("currentProfileRequired") is True
                           and catchup.get("mutationBlocked") is True
                           and catchup.get("bClassification") == "PartiallyApplied"
                           and catchup.get("bStage") == "Blocked"
                           and catchup.get("cStage") == "Prepared"
                           and "bb288558-11d7-4a4d-91e2-a4332cecedc8" in catchup.get("cBlockingPrerequisites", [])
                           and bool(catchup.get("observedStateDigest"))
                           and catchup.get("declaredNoExecution") is True))
            case["evidence"] = evidence
            case["oldWriterStop"] = stop
            case["currentResumeReceiptCount"] = len(phase_receipts)
            case["legacyAdmissionRefusalReceipts"] = legacy_admission_receipts
            case["upgradeReceipts"] = upgrade_receipts
            case["currentResumeReceipts"] = phase_receipts_json
            case["repeatedResume"] = {"expectedPhase": repeated_phase, "evidence": catchup, "passed": catchup_ok}
            case["passed"] = (evidence.get("schema") == "aaaw-m6-2-profile-process-case/v1"
                               and evidence.get("oldSourceSha") == OLD_SHA
                               and evidence.get("newSourceSha") == expected_new_sha
                               and evidence.get("barrier") == prefix
                               and evidence.get("workflowId") == "dd7e19af-e327-4886-b286-ec96c26835a7"
                               and evidence.get("legacyAdmissionRefusedWithoutMutation") is True
                               and evidence.get("legacyAdmissionFingerprint")
                               and evidence.get("externalStateUnchangedByUpgrade") is True
                               and evidence.get("legacyApplyLogPreserved") is True
                               and (prefix != "K4" or evidence.get("catchUpSnapshotUnchanged") is True)
                               and evidence.get("repeatedResumePhase") == repeated_phase
                               and evidence.get("oldProbeSha256") == old_build_probe_sha()
                               and evidence.get("newProbeSha256") == new_probe_sha
                               and stop_ok and apply_log.is_file() and len(legacy_admission_receipts) == 1
                               and len(phase_receipts) == 2 and len(upgrade_receipts) == 2
                               and catchup_ok
                               and all(receipt.get("cause") == "NonzeroExit" and receipt.get("rootExitCode") not in (None, 0)
                                       and receipt.get("cleanup") == "Confirmed" and receipt.get("captureStatus") == "Complete"
                                       for receipt in legacy_admission_receipts)
                               and all(receipt.get("cause") == "NaturalExit" and receipt.get("rootExitCode") == 0
                                       and receipt.get("cleanup") == "Confirmed" and receipt.get("captureStatus") == "Complete"
                                       for receipt in upgrade_receipts + phase_receipts_json))
        elif prefix.startswith("K1-"):
            evidence_path = directory / "profile-checkpoint-case.json"
            evidence = read_json(evidence_path) if evidence_path.is_file() else {}
            checkpoint = prefix.removeprefix("K1-")
            stop_ok, stop = verify_stop(directory / f"lifecycle-stop-profile-{checkpoint}.json", "ExplicitStop")
            retry_receipts = [read_json(path) for path in sorted(directory.glob("natural-profile-upgrade-*.json"))]
            case["evidence"] = evidence
            case["stop"] = stop
            case["retryReceipts"] = retry_receipts
            case["passed"] = (evidence.get("schema") == "aaaw-m6-2-profile-checkpoint-case/v1"
                               and evidence.get("oldSourceSha") == OLD_SHA
                               and evidence.get("newSourceSha") == expected_new_sha
                               and evidence.get("checkpoint") == checkpoint and stop_ok
                               and evidence.get("committed") == (checkpoint == "AfterCommit")
                               and len(retry_receipts) == 1
                               and retry_receipts[0].get("cause") == "NaturalExit"
                               and retry_receipts[0].get("cleanup") == "Confirmed"
                               and retry_receipts[0].get("captureStatus") == "Complete"
                               and evidence.get("oldProbeSha256") == old_build_probe_sha()
                               and evidence.get("newProbeSha256") == new_probe_sha)
        else:
            evidence_path = directory / "profile-refusal-case.json"
            evidence = read_json(evidence_path) if evidence_path.is_file() else {}
            receipts = [read_json(path) for path in sorted(directory.glob("refusal-profile-upgrade-*.json"))]
            errors = [read_json(path) for path in sorted(directory.glob("phase2-error.json"))]
            case["evidence"] = evidence
            case["refusalReceipts"] = receipts
            case["errorEvidence"] = errors
            case["passed"] = (evidence.get("schema") == "aaaw-m6-2-profile-refusal-case/v1"
                               and evidence.get("damage") == prefix.removeprefix("refusal-")
                               and evidence.get("newSourceSha") == expected_new_sha
                               and evidence.get("attempts") == 2 and len(receipts) == 2 and len(errors) == 1
                               and all(receipt.get("cause") == "NonzeroExit" and receipt.get("rootExitCode") == 1
                                       and receipt.get("cleanup") == "Confirmed" and receipt.get("captureStatus") == "Complete"
                                       for receipt in receipts)
                               and evidence.get("oldProbeSha256") == old_build_probe_sha()
                               and evidence.get("newProbeSha256") == new_probe_sha)
        result["passed"] = result["passed"] and bool(case["passed"])
        result["cases"][prefix] = case

    old_build: dict[str, Any] = {}
    try:
        old_build = read_json(OLD_BUILD)
        old_ok = (old_build.get("passed") is True and old_build.get("oldSourceSha") == OLD_SHA
                  and old_build.get("newSourceSha") == expected_new_sha and old_build.get("newSourceClean") is True
                  and old_build.get("oldSource", {}).get("clean") is True
                  and old_build.get("oldSourceEnd", {}).get("clean") is True
                  and old_build.get("oldProbe", {}).get("sha256")
                  and all(command.get("passed") is True for command in old_build.get("commands", []))
                  and old_build.get("resolvedOldProbePackages"))
    except Exception as exc:
        old_ok = False
        old_build = {"error": f"{type(exc).__name__}: {exc}"}
    result["oldWriterBuild"] = old_build
    result["oldWriterBuildPassed"] = bool(old_ok)
    result["passed"] = result["passed"] and bool(old_ok)
    return result


def old_build_probe_sha() -> str | None:
    if not OLD_BUILD.is_file():
        return None
    try:
        record = read_json(OLD_BUILD).get("oldProbe", {})
        path = Path(record.get("path", ""))
        actual = hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None
        return actual if actual == record.get("sha256") else None
    except Exception:
        return None


def add_files(selected: set[Path], root: Path, start_epoch: float | None = None) -> None:
    if root.is_file():
        selected.add(root)
    elif root.is_dir():
        for path in root.rglob("*"):
            if path.is_file() and (start_epoch is None or path.stat().st_mtime >= start_epoch):
                selected.add(path)


def seal_report(report: dict[str, Any], output: Path, selected: set[Path]) -> None:
    output.mkdir(parents=True, exist_ok=True)
    archive_path = output / "captured-artifacts.zip"
    report_path = output / "qualification.json"
    report["capturedArtifacts"] = {"path": archive_path.relative_to(ROOT).as_posix(), "memberCount": len(selected) + 2}
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    selected.add(report_path)
    manifest: list[dict[str, Any]] = []
    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(selected):
            try:
                relative = path.relative_to(ROOT).as_posix()
            except ValueError:
                continue
            data = path.read_bytes()
            manifest.append({"path": relative, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            archive.writestr(relative, data)
        archive.writestr("capture-manifest.json", json.dumps({"files": manifest}, indent=2, sort_keys=True) + "\n")


def test_paths_from_m61(record: dict[str, Any], platform_name: str, test_results_dir: str | None) -> dict[str, Path]:
    if platform_name == "windows":
        if not test_results_dir:
            return {}
        root = Path(test_results_dir).resolve()
        return {framework: root / f"m6-{framework}.trx" for framework in FRAMEWORK_CLASSES}
    result: dict[str, Path] = {}
    qualification = record.get("m5", {}).get("qualification", {})
    for run in qualification.get("testRuns", []):
        framework = run.get("framework")
        trx = run.get("trx", {})
        raw = trx.get("path") if isinstance(trx, dict) else None
        if framework in FRAMEWORK_CLASSES and raw:
            path = Path(raw)
            result[framework] = path if path.is_absolute() else ROOT / path
    if platform_name == "macos" and not result:
        folder = ROOT / "artifacts/aaaw-m5/macos-qualification"
        result = {framework: folder / f"m5-{framework}.trx" for framework in FRAMEWORK_CLASSES}
    return result


def run_m61(platform_name: str) -> tuple[int, dict[str, Any] | None, Path]:
    module = load_module(ROOT / "eng/qualify-aaaw-m6-1.py", "aaaw_m6_1_native_m62")
    rc = module.main()
    folder = ROOT / f"artifacts/aaaw-m6-1/{platform_name}-qualification"
    path = folder / "qualification.json"
    return rc, read_json(path) if path.is_file() else None, folder


def bootstrap_oldwriter() -> dict[str, Any]:
    try:
        return run_command("bootstrap-exact-m6.1-old-writer", [sys.executable, "eng/prepare-aaaw-m6-2-oldwriter.py"], 3600)
    except Exception as exc:
        return {"name": "bootstrap-exact-m6.1-old-writer", "passed": False,
                "failure": f"{type(exc).__name__}: {exc}"}


def qualify(platform_name: str, validate_windows: bool = False, test_results_dir: str | None = None,
            started_epoch: float | None = None, source_commit: str | None = None, runner_failures: list[str] | None = None) -> int:
    output = OUT_ROOT / f"{platform_name}-qualification"
    output.mkdir(parents=True, exist_ok=True)
    m61_folder = ROOT / f"artifacts/aaaw-m6-1/{platform_name}-qualification"
    report: dict[str, Any] = {
        "schema": "aaaw-m6-2-qualification/v1", "platform": platform_name, "startedAt": utc_now(),
        "source": {"start": {"commit": source_commit, "clean": True}}, "oldWriterBootstrap": {},
        "m61": {}, "m62": {"tests": {}, "processArtifacts": {}}, "qualificationPassed": False,
    }
    selected: set[Path] = set()
    m61_report: dict[str, Any] | None = None
    run_started = started_epoch if started_epoch is not None else time.time()
    try:
        state = source_state()
        report["source"]["start"] = {**state, "expectedCommit": source_commit}
        if source_commit is not None and state.get("commit") != source_commit:
            raise RuntimeError("Source changed between the runner start and evidence validation.")
        bootstrap = read_json(OLD_BUILD) if OLD_BUILD.is_file() else {}
        report["oldWriterBootstrap"] = {"report": bootstrap, "passed": bootstrap.get("passed") is True
                                        and bootstrap.get("oldSourceSha") == OLD_SHA
                                        and bootstrap.get("newSourceSha") == state.get("commit")
                                        and bootstrap.get("newSourceClean") is True
                                        and bootstrap.get("oldSource", {}).get("commit") == OLD_SHA
                                        and bootstrap.get("oldSource", {}).get("clean") is True
                                        and bootstrap.get("oldSourceEnd", {}).get("commit") == OLD_SHA
                                        and bootstrap.get("oldSourceEnd", {}).get("clean") is True}

        if validate_windows:
            m61_path = m61_folder / "qualification.json"
            m61_report = read_json(m61_path) if m61_path.is_file() else None
            report["m61"] = {"qualification": m61_report,
                             "rawZipPresent": (m61_folder / "captured-artifacts.zip").is_file(),
                             "passed": isinstance(m61_report, dict) and m61_report.get("qualificationPassed") is True
                             and (m61_folder / "captured-artifacts.zip").is_file()}
        else:
            run_started = time.time()
            rc, m61_report, m61_folder = run_m61(platform_name)
            report["m61"] = {"returnCode": rc, "qualification": m61_report,
                             "rawZipPresent": (m61_folder / "captured-artifacts.zip").is_file(),
                             "passed": rc == 0 and isinstance(m61_report, dict) and m61_report.get("qualificationPassed") is True
                             and (m61_folder / "captured-artifacts.zip").is_file()}

        shared = load_module(ROOT / "eng/qualify-aaaw-m6-1.py", "aaaw_m6_1_shared_m62_validate")
        trx_paths = test_paths_from_m61(report["m61"], platform_name, test_results_dir)
        report["m62"]["tests"] = verify_new_trx(trx_paths, shared)
        report["m62"]["processArtifacts"] = verify_process_artifacts(run_started, shared, state["commit"])
        end = source_state()
        report["source"]["end"] = end
        expected_sha = source_commit or report["source"]["start"].get("commit")
        source_ok = report["source"]["start"].get("clean") is True and end.get("clean") is True and end.get("commit") == expected_sha
        report["source"]["unchangedAndClean"] = source_ok
        report["qualificationPassed"] = bool(source_ok and report["oldWriterBootstrap"]["passed"]
            and report["m61"].get("passed") is True and report["m62"]["tests"].get("passed") is True
            and report["m62"]["processArtifacts"].get("passed") is True and not runner_failures)
        if runner_failures:
            report["runnerFailures"] = runner_failures
    except Exception as exc:
        report["failure"] = {"type": type(exc).__name__, "message": str(exc), "traceback": traceback.format_exc(limit=12)}
    finally:
        report["completedAt"] = utc_now()
        add_files(selected, m61_folder / "qualification.json")
        add_files(selected, m61_folder / "captured-artifacts.zip")
        add_files(selected, OLD_BUILD)
        for prefix, paths in fresh_case_directories(OUT_ROOT / "process-probes", run_started).items():
            for path in paths:
                add_files(selected, path)
        if (output / "runner.log").is_file():
            selected.add(output / "runner.log")
        seal_report(report, output, selected)
    print(json.dumps({"qualificationPassed": report["qualificationPassed"], "evidence": str(output / "qualification.json")}, indent=2))
    return 0 if report["qualificationPassed"] else 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--validate-windows", action="store_true")
    parser.add_argument("--test-results-dir")
    parser.add_argument("--started-epoch")
    parser.add_argument("--source-commit")
    parser.add_argument("--runner-failure", action="append", default=[])
    args = parser.parse_args()
    if args.validate_windows:
        if platform.system() != "Windows" or not all((args.test_results_dir, args.started_epoch, args.source_commit)):
            parser.error("Windows validation requires Windows, --test-results-dir, --started-epoch, and --source-commit")
        return qualify("windows", True, args.test_results_dir, float(args.started_epoch), args.source_commit, args.runner_failure)

    system = platform.system()
    if system not in {"Linux", "Darwin"}:
        parser.error("Use --validate-windows through the PowerShell qualification runner on Windows")
    state = source_state()
    if not state["clean"]:
        raise SystemExit("M6.2 qualification requires an exact clean committed source tree.")
    # The real M6.1 writer is built first in its detached old-source worktree.
    bootstrap = bootstrap_oldwriter()
    if bootstrap["passed"] is not True:
        OUT_ROOT.mkdir(parents=True, exist_ok=True)
        platform_name = "macos" if system == "Darwin" else "linux"
        return qualify(platform_name, source_commit=state["commit"], runner_failures=["Exact old-writer bootstrap failed."])
    platform_name = "macos" if system == "Darwin" else "linux"
    return qualify(platform_name, source_commit=state["commit"])


if __name__ == "__main__":
    raise SystemExit(main())
