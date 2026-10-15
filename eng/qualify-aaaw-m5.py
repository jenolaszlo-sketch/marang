#!/usr/bin/env python3
"""Bounded Linux consumer qualification for Marang M5.

This is an evidence runner, not a process-lifecycle provider. The canonical
M4/M5 tests must exercise the checked-in Gagamba owner and runtime boundary.
Each tool invocation is bounded, output retention is capped, and failures are
written to artifacts/aaaw-m5/linux-qualification/qualification.json.
"""

from __future__ import annotations

import hashlib
import json
import os
import platform
import signal
import subprocess
import sys
import threading
import time
import traceback
import uuid
import xml.etree.ElementTree as ET
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts" / "aaaw-m5" / "linux-qualification"
M1_LOCK = ROOT / "eng" / "aaaw-m2-packages.json"
M5_LOCK = ROOT / "eng" / "aaaw-m5-runtime-packages.json"
M1_PACKAGE_DIR = ROOT / "artifacts" / "aaaw-m1" / "packages"
EXPECTED_RUNTIME_COMMIT = "0c1f05e199588622a5987f95a9149374f5bd858b"
M4_FIXTURE_COMMIT = "a0916534d0e82e499cffb2dea526b64357eb32e2"
BASELINE_TEST_COUNTS = {"net8.0": 201, "net10.0": 245}
REQUIRED_RUNTIME_PACKAGES = {
    "Gagamba.Runtime",
    "Gagamba.Execution",
    "Gagamba.Execution.Windows",
    "Gagamba.Execution.Linux",
    "Gagamba.Execution.MacOS",
}
MAX_RETAINED_BYTES_PER_STREAM = 2 * 1024 * 1024
COMMAND_TIMEOUT_SECONDS = 60 * 60
TERMINATION_GRACE_SECONDS = 10


class BoundedPipeCapture:
    def __init__(self, stream: Any, limit: int) -> None:
        self.stream = stream
        self.limit = limit
        self.retained = bytearray()
        self.bytes_seen = 0
        self.error: str | None = None
        self.thread = threading.Thread(target=self._drain, daemon=True)

    def start(self) -> None:
        self.thread.start()

    def _drain(self) -> None:
        try:
            while True:
                chunk = self.stream.read(64 * 1024)
                if not chunk:
                    return
                self.bytes_seen += len(chunk)
                room = self.limit - len(self.retained)
                if room > 0:
                    self.retained.extend(chunk[:room])
        except Exception as exc:  # record pipe failures without losing the command result
            self.error = f"{type(exc).__name__}: {exc}"

    def text(self) -> str:
        return self.retained.decode("utf-8", errors="replace")


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_record(path: Path) -> dict[str, Any]:
    data = path.read_bytes()
    return {"path": path.relative_to(ROOT).as_posix(), "sha256": sha256_bytes(data), "bytes": len(data)}


def git_output(*args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(ROOT), *args],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=15,
    )
    return result.stdout.strip()


def source_state() -> dict[str, Any]:
    try:
        head = git_output("rev-parse", "HEAD")
        status = git_output("status", "--porcelain=v1", "--untracked-files=all")
        return {"commit": head, "branch": git_output("branch", "--show-current"), "clean": status == "", "status": status.splitlines()}
    except Exception as exc:
        return {"commit": None, "clean": False, "statusError": f"{type(exc).__name__}: {exc}"}


def package_entries(lock_path: Path) -> list[dict[str, Any]]:
    raw = json.loads(lock_path.read_text(encoding="utf-8"))
    if isinstance(raw, list):
        entries = raw
    elif isinstance(raw, dict):
        entries = raw.get("packages") or raw.get("Packages")
    else:
        entries = None
    if not isinstance(entries, list) or not entries:
        raise ValueError(f"{lock_path.name} must contain a non-empty package array")
    return entries


def validate_package_lock(lock_path: Path, *, require_runtime_set: bool = False) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    seen: set[str] = set()
    for entry in package_entries(lock_path):
        package_id = entry.get("id") or entry.get("packageId") or entry.get("Id")
        version = entry.get("version") or entry.get("Version")
        commit = entry.get("commit") or entry.get("sourceCommit") or entry.get("Commit")
        expected_hash = (entry.get("sha256") or entry.get("sha256Lower") or entry.get("Sha256") or "").lower()
        if not package_id or not version or not commit or len(expected_hash) != 64:
            raise ValueError(f"incomplete package pin in {lock_path.name}: {entry!r}")
        if package_id in seen:
            raise ValueError(f"duplicate package id in {lock_path.name}: {package_id}")
        seen.add(package_id)
        package_path = M1_PACKAGE_DIR / f"{package_id}.{version}.nupkg"
        if not package_path.is_file():
            raise FileNotFoundError(f"pinned package does not exist: {package_path}")
        actual_hash = hashlib.sha256(package_path.read_bytes()).hexdigest()
        if actual_hash != expected_hash:
            raise ValueError(f"package SHA-256 mismatch for {package_id}: {actual_hash}")
        with zipfile.ZipFile(package_path) as archive:
            nuspec_names = [name for name in archive.namelist() if name.lower().endswith(".nuspec")]
            if len(nuspec_names) != 1:
                raise ValueError(f"expected one nuspec in {package_path.name}, found {len(nuspec_names)}")
            metadata = ET.fromstring(archive.read(nuspec_names[0]))
            values = {element.tag.rsplit("}", 1)[-1]: element for element in metadata.iter()}
            nuspec_id = (values.get("id").text or "") if values.get("id") is not None else ""
            nuspec_version = (values.get("version").text or "") if values.get("version") is not None else ""
            repository = values.get("repository")
            nuspec_commit = repository.attrib.get("commit") if repository is not None else None
        if (nuspec_id, nuspec_version, nuspec_commit) != (package_id, version, commit):
            raise ValueError(
                f"nuspec provenance mismatch for {package_id}: "
                f"{nuspec_id}/{nuspec_version}/{nuspec_commit}"
            )
        if lock_path == M5_LOCK and commit != EXPECTED_RUNTIME_COMMIT:
            raise ValueError(f"Gagamba package {package_id} is not pinned to {EXPECTED_RUNTIME_COMMIT}")
        records.append({
            "id": package_id,
            "version": version,
            "sourceCommit": commit,
            "sha256": actual_hash,
            "file": file_record(package_path),
        })
    if require_runtime_set:
        missing = sorted(REQUIRED_RUNTIME_PACKAGES - seen)
        if missing:
            raise ValueError(f"runtime package lock omits lifecycle package(s): {', '.join(missing)}")
    return records


def linux_preflight() -> dict[str, Any]:
    result: dict[str, Any] = {
        "platform": platform.platform(),
        "system": platform.system(),
        "machine": platform.machine(),
        "effectiveUid": os.geteuid() if hasattr(os, "geteuid") else None,
        "cgroupParentConfigured": bool(os.environ.get("GAGAMBA_LINUX_CGROUP")),
        "cgroupParent": os.environ.get("GAGAMBA_LINUX_CGROUP"),
        "checks": {},
    }
    if platform.system() != "Linux":
        result["checks"]["linuxHost"] = False
        return result
    euid = os.geteuid()
    result["checks"]["nonRoot"] = euid != 0
    raw_parent = os.environ.get("GAGAMBA_LINUX_CGROUP")
    if not raw_parent:
        result["checks"]["configuredCgroupParent"] = False
        result["checks"]["cgroupV2Path"] = False
        result["checks"]["writableCgroupParent"] = False
        return result

    parent = Path(raw_parent)
    exists = parent.is_absolute() and parent.is_dir()
    result["checks"]["configuredCgroupParent"] = exists
    writable = exists and os.access(parent, os.R_OK | os.W_OK | os.X_OK)
    result["checks"]["writableCgroupParent"] = writable
    has_cgroup_files = exists and (parent / "cgroup.controllers").is_file() and (parent / "cgroup.subtree_control").is_file()
    result["checks"]["cgroupV2ControlFiles"] = has_cgroup_files

    mount_paths: list[Path] = []
    try:
        for line in Path("/proc/self/mountinfo").read_text(encoding="utf-8").splitlines():
            before, separator, after = line.partition(" - ")
            if not separator or not after.startswith("cgroup2 "):
                continue
            fields = before.split()
            if len(fields) >= 5:
                mount_paths.append(Path(fields[4].replace("\\040", " ")))
    except OSError:
        pass
    is_under_cgroup2 = exists and any(
        os.path.commonpath((str(parent.resolve()), str(mount.resolve()))) == str(mount.resolve())
        for mount in mount_paths
    )
    result["checks"]["cgroupV2Path"] = is_under_cgroup2 and has_cgroup_files
    result["cgroup2Mounts"] = [str(path) for path in mount_paths]
    result["checks"]["ready"] = all((
        result["checks"]["nonRoot"],
        result["checks"]["configuredCgroupParent"],
        result["checks"]["writableCgroupParent"],
        result["checks"]["cgroupV2Path"],
    ))
    result["delegationProof"] = (
        "Static mount, control-file, identity, and access checks only. Successful canonical Gagamba prepare/launch/stop receipts are the runtime evidence of delegation."
    )
    return result


def run_command(name: str, argv: list[str], timeout_seconds: int = COMMAND_TIMEOUT_SECONDS,
                environment: dict[str, str] | None = None) -> dict[str, Any]:
    started = utc_now()
    record: dict[str, Any] = {
        "name": name,
        "argv": argv,
        "startedAt": started,
        "timeoutSeconds": timeout_seconds,
        "exitCode": None,
        "timedOut": False,
        "termination": "not-needed",
        "stdoutBytesSeen": 0,
        "stderrBytesSeen": 0,
        "stdoutTruncated": False,
        "stderrTruncated": False,
        "stdout": "",
        "stderr": "",
        "cleanupConfirmed": None,
        "orphanedProcessGroupObserved": False,
        "ok": False,
    }
    process: subprocess.Popen[bytes] | None = None
    stdout_capture: BoundedPipeCapture | None = None
    stderr_capture: BoundedPipeCapture | None = None
    try:
        process = subprocess.Popen(
            argv,
            cwd=ROOT,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            start_new_session=True,
            close_fds=True,
            env=environment,
        )
        assert process.stdout is not None and process.stderr is not None
        record["processId"] = process.pid
        record["processGroupId"] = process.pid
        stdout_capture = BoundedPipeCapture(process.stdout, MAX_RETAINED_BYTES_PER_STREAM)
        stderr_capture = BoundedPipeCapture(process.stderr, MAX_RETAINED_BYTES_PER_STREAM)
        stdout_capture.start()
        stderr_capture.start()
        deadline = time.monotonic() + timeout_seconds
        while process.poll() is None and time.monotonic() < deadline:
            time.sleep(0.1)
        timed_out = process.poll() is None
        if timed_out:
            record["timedOut"] = True
            record["termination"] = "SIGTERM-group"
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=TERMINATION_GRACE_SECONDS)
            except subprocess.TimeoutExpired:
                record["termination"] = "SIGKILL-group-after-SIGTERM"
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait(timeout=TERMINATION_GRACE_SECONDS)
        else:
            process.wait()
        # A command's leader can exit while a compiler/test host remains in
        # the isolated session. Reap that session rather than leaving an
        # unowned qualification workload behind.
        group_alive = True
        group_deadline = time.monotonic() + (TERMINATION_GRACE_SECONDS if timed_out else 2)
        while time.monotonic() < group_deadline:
            try:
                os.killpg(process.pid, 0)
            except ProcessLookupError:
                group_alive = False
                break
            time.sleep(0.1)
        if group_alive:
            record["orphanedProcessGroupObserved"] = not timed_out
            if not timed_out:
                record["termination"] = "SIGTERM-group-after-leader-exit"
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                group_alive = False
            if group_alive:
                kill_deadline = time.monotonic() + TERMINATION_GRACE_SECONDS
                while time.monotonic() < kill_deadline:
                    try:
                        os.killpg(process.pid, 0)
                    except ProcessLookupError:
                        group_alive = False
                        break
                    time.sleep(0.1)
            if group_alive:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                    record["termination"] = "SIGKILL-group-after-session-drain-timeout"
                except ProcessLookupError:
                    group_alive = False
            final_deadline = time.monotonic() + TERMINATION_GRACE_SECONDS
            while group_alive and time.monotonic() < final_deadline:
                try:
                    os.killpg(process.pid, 0)
                except ProcessLookupError:
                    group_alive = False
                    break
                time.sleep(0.1)
        record["cleanupConfirmed"] = not group_alive
        stdout_capture.thread.join(timeout=TERMINATION_GRACE_SECONDS)
        stderr_capture.thread.join(timeout=TERMINATION_GRACE_SECONDS)
        record["exitCode"] = process.returncode
        record["stdoutBytesSeen"] = stdout_capture.bytes_seen
        record["stderrBytesSeen"] = stderr_capture.bytes_seen
        record["stdoutTruncated"] = stdout_capture.bytes_seen > len(stdout_capture.retained)
        record["stderrTruncated"] = stderr_capture.bytes_seen > len(stderr_capture.retained)
        record["stdout"] = stdout_capture.text()
        record["stderr"] = stderr_capture.text()
        record["outputDrainComplete"] = not stdout_capture.thread.is_alive() and not stderr_capture.thread.is_alive()
        record["captureErrors"] = [error for error in (stdout_capture.error, stderr_capture.error) if error]
        if not record["outputDrainComplete"]:
            record["captureErrors"].append("bounded output drain did not complete")
        record["ok"] = (process.returncode == 0 and not record["timedOut"]
                        and not record["captureErrors"] and record["cleanupConfirmed"] is True
                        and not record["orphanedProcessGroupObserved"])
    except Exception as exc:
        record["error"] = f"{type(exc).__name__}: {exc}"
        record["traceback"] = traceback.format_exc(limit=6)
        if process is not None and process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGTERM)
                record["termination"] = "SIGTERM-group-after-runner-error"
                process.wait(timeout=TERMINATION_GRACE_SECONDS)
            except Exception:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                    record["termination"] = "SIGKILL-group-after-runner-error"
                    process.wait(timeout=TERMINATION_GRACE_SECONDS)
                except Exception as cleanup_error:
                    record["cleanupError"] = f"{type(cleanup_error).__name__}: {cleanup_error}"
    finally:
        record["completedAt"] = utc_now()
    return record


def xml_local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_trx(path: Path, framework: str) -> dict[str, Any]:
    root = ET.parse(path).getroot()
    counters = next((node for node in root.iter() if xml_local_name(node.tag) == "Counters"), None)
    if counters is None:
        raise ValueError(f"TRX has no ResultSummary/Counters: {path}")
    counts = {key.lower(): int(value) for key, value in counters.attrib.items() if value.isdigit()}
    results = []
    for node in root.iter():
        if xml_local_name(node.tag) != "UnitTestResult":
            continue
        results.append({
            "testName": node.attrib.get("testName"),
            "outcome": node.attrib.get("outcome"),
            "duration": node.attrib.get("duration"),
        })
    skipped = [result for result in results if (result["outcome"] or "").lower() in {"notexecuted", "skipped", "inconclusive"}]
    failures = [result for result in results if (result["outcome"] or "").lower() not in {"passed", "notexecuted", "skipped", "inconclusive"}]
    m4 = [result for result in results if any(
        name in (result["testName"] or "").lower()
        for name in ("multi_effect_graph_survives", "partial_b_fixture_blocks", "multi_effect_reconciliation_refuses")
    )]
    m5 = [result for result in m4 if "multi_effect_reconciliation_refuses" in (result["testName"] or "").lower()]
    minimum = BASELINE_TEST_COUNTS[framework]
    record = {
        "framework": framework,
        "counters": counts,
        "testResultCount": len(results),
        "skippedTests": skipped,
        "failedTests": failures,
        "m4ProcessTestCount": len(m4),
        "m4ProcessTests": m4,
        "m5TestCount": len(m5),
        "m5Tests": m5,
        "baselineMinimum": minimum,
        "baselineMinimumMet": counts.get("total", 0) >= minimum,
        "trx": file_record(path),
    }
    record["passed"] = (
        counts.get("failed", 0) == 0
        and counts.get("error", 0) == 0
        and counts.get("timeout", 0) == 0
        and counts.get("aborted", 0) == 0
        and counts.get("passed", 0) == counts.get("total", -1)
        and counts.get("total", 0) >= minimum
        and len(results) == counts.get("total", -1)
        and not failures
        and not skipped
    )
    if framework == "net10.0":
        required_m4_fragments = (
            "multi_effect_graph_survives_each_restart",
            "partial_b_fixture_blocks_its_dependent_effect",
            "multi_effect_reconciliation_refuses_to_continue",
        )
        lower_names = [(result["testName"] or "").lower() for result in m4]
        required_tests_present = all(any(fragment in name for name in lower_names)
                                     for fragment in required_m4_fragments)
        record["requiredM4AndM5ProcessTestsPresent"] = required_tests_present
        record["requiredM5ControlPassed"] = len(m5) == 1 and all(
            (result["outcome"] or "").lower() == "passed" for result in m5
        )
        record["passed"] = record["passed"] and required_tests_present and record["requiredM5ControlPassed"]
    return record


def find_newest(path: Path, filename: str, started_at: float) -> Path | None:
    if not path.exists():
        return None
    candidates = [candidate for candidate in path.rglob(filename) if candidate.is_file() and candidate.stat().st_mtime >= started_at]
    return max(candidates, key=lambda item: item.stat().st_mtime_ns) if candidates else None


def check_m4_scenario(path: Path, started_at: float) -> dict[str, Any]:
    result: dict[str, Any] = {"observed": False, "verified": False}
    if not path.exists() or path.stat().st_mtime < started_at:
        result["reason"] = "No successful workflow result file was produced after this qualification began."
        return result
    try:
        scenario = json.loads(path.read_text(encoding="utf-8"))
        expected_barriers = ["K1", "K2", "K3", "K5", "K6", "K7", "K8", "K9", "K10", "K11", "K12"]
        assertions = {
            "fixtureCommit": scenario.get("sourceCommit") == M4_FIXTURE_COMMIT,
            "recoveryBarriers": scenario.get("recoveryBarriers") == expected_barriers,
            "finalBranchCompleted": scenario.get("finalBranchCompleted") is True,
            "workflowValidationPassed": scenario.get("workflowValidationPassed") is True,
            "localValidationPassed": scenario.get("localValidationPassed") is True,
            "noDuplicateMutation": scenario.get("noDuplicateMutation") is True,
            "inventoryInvocationCount": scenario.get("inventoryInvocationCount") == 1,
            "acceptedLogicalEffects": scenario.get("acceptedLogicalEffects") == ["A", "B2", "C2"],
            "activeAcceptedEffects": scenario.get("activeAcceptedEffects") == ["A", "B2", "C2"],
            "exactStateLineage": scenario.get("exactStateLineage") is True,
            "aWasExplicitlyAdopted": scenario.get("aWasExplicitlyAdopted") is True,
            "oldBAndCRemainAppliedEvidence": scenario.get("oldBAndCRemainAppliedEvidence") is True,
            "oldGraphValidationPassed": scenario.get("oldGraphValidationPassed") is False,
            "activeGraphMutationBlocked": scenario.get("activeGraphMutationBlocked") is False,
            "applyCounts": scenario.get("effectApplyCounts") == {"A": 1, "B": 1, "C": 1, "B2": 1, "C2": 1},
        }
        result.update({"observed": True, "verified": all(assertions.values()), "assertions": assertions,
                       "file": file_record(path), "fixtureSourceCommit": scenario.get("sourceCommit"),
                       "recoveryBarrierCount": len(scenario.get("recoveryBarriers") or []),
                       "effectApplyCounts": scenario.get("effectApplyCounts"),
                       "inventoryInvocationCount": scenario.get("inventoryInvocationCount")})
    except Exception as exc:
        result["reason"] = f"{type(exc).__name__}: {exc}"
    return result


def check_k4(path: Path, started_at: float) -> dict[str, Any]:
    result: dict[str, Any] = {"observed": False, "verified": False}
    if not path.exists() or path.stat().st_mtime < started_at:
        result["reason"] = "No K4 partial-state result was produced after this qualification began."
        return result
    try:
        scenario = json.loads(path.read_text(encoding="utf-8"))
        assertions = {
            "partiallyApplied": scenario.get("prerequisiteClassification") == "PartiallyApplied",
            "dependentBlocked": scenario.get("dependentEffectBlocked") is True,
            "physicalApplyCount": scenario.get("physicalApplyCount") == 1,
            "dependentPhysicalApplyCount": scenario.get("dependentPhysicalApplyCount") == 0,
            "dependentStage": scenario.get("dependentStage") == "Prepared",
            "namedBlockerPresent": bool(scenario.get("dependentBlockedByEffectIds")),
        }
        result.update({"observed": True, "verified": all(assertions.values()), "assertions": assertions,
                       "file": file_record(path)})
    except Exception as exc:
        result["reason"] = f"{type(exc).__name__}: {exc}"
    return result


def check_barriers(scenario_dir: Path, k4_dir: Path, started_at: float) -> dict[str, Any]:
    barriers: dict[str, Any] = {}
    for name in [f"K{i}" for i in range(1, 13)]:
        directory = k4_dir if name == "K4" else scenario_dir
        context_path = directory / f"barrier-context-{name}.json"
        used_path = directory / f"barrier-used-{name}"
        stop_path = directory / f"lifecycle-stop-{name}.json"
        barrier: dict[str, Any] = {"contextPresent": context_path.is_file(), "usedMarkerPresent": used_path.is_file(),
                                   "lifecycleReceiptPresent": stop_path.is_file()}
        if not all((barrier["contextPresent"], barrier["usedMarkerPresent"], barrier["lifecycleReceiptPresent"])):
            barrier["verified"] = False
            barrier["reason"] = "Missing barrier context, used marker, or lifecycle stop receipt."
            barriers[name] = barrier
            continue
        try:
            context = json.loads(context_path.read_text(encoding="utf-8"))
            stop = json.loads(stop_path.read_text(encoding="utf-8"))
            root = stop.get("root") or stop.get("Root") or {}
            commands = stop.get("brokerCommands") or stop.get("BrokerCommands") or []
            root_ok = root.get("cause") == "ExplicitStop" and root.get("cleanup") == "Confirmed"
            commands_ok = all(command.get("accepted") is True and command.get("cleanup") == "Confirmed"
                              for command in commands)
            k12_ok = name != "K12" or (
                bool(commands)
                and all(command.get("cause") == "ExplicitStop" for command in commands)
                and context.get("validationProcessRunning") is True
            )
            barrier.update({"rootCause": root.get("cause"), "rootCleanup": root.get("cleanup"),
                            "brokerCommandCount": len(commands), "brokerCommandCauses": [c.get("cause") for c in commands],
                            "k12ActualValidationWasRunning": context.get("validationProcessRunning") if name == "K12" else None,
                            "verified": root_ok and commands_ok and k12_ok,
                            "context": file_record(context_path), "stopReceipt": file_record(stop_path)})
        except Exception as exc:
            barrier["verified"] = False
            barrier["reason"] = f"{type(exc).__name__}: {exc}"
        barriers[name] = barrier
    return barriers


def check_negative_control(directory: Path, started_at: float) -> dict[str, Any]:
    result: dict[str, Any] = {"observed": False, "verified": False}
    first_path = directory / "multi-effect-reconcile-1.json"
    second_path = directory / "multi-effect-reconcile-2.json"
    if not all(path.is_file() and path.stat().st_mtime >= started_at for path in (first_path, second_path)):
        result["reason"] = "Both fresh actual-state reconciliation observations were not produced."
        return result
    try:
        first = json.loads(first_path.read_text(encoding="utf-8"))
        second = json.loads(second_path.read_text(encoding="utf-8"))
        first_digest = first.get("observedStateAfter", {}).get("stateDigest")
        second_digest = second.get("observedStateAfter", {}).get("stateDigest")
        first_project_hash = first.get("actualProjectFileSha256")
        second_project_hash = second.get("actualProjectFileSha256")
        assertions = {
            "unexpectedActualStateDetected": first.get("observedStateDiffersFromAExpected") is True,
            "beforeAfterStateStable": first.get("observedStateBefore", {}).get("stateDigest") == first_digest,
            "mutationBlocked": first.get("mutationBlocked") is True,
            "validatorNotCalled": first.get("validatorCalled") is False,
            "checkpointNotCalled": first.get("checkpointCalled") is False,
            "BAndCUnchanged": first.get("noBOrCMutation") is True and first.get("bRemainedPrepared") is True and first.get("cRemainedPrepared") is True,
            "actualStateStableAcrossRuns": bool(first_digest) and first_digest == second_digest,
            "tamperedProjectHashStableAcrossRuns": bool(first_project_hash) and first_project_hash == second_project_hash,
            "projectWasClassifiedStale": first.get("allowlistedProjectObservedAsStale") is True,
        }
        result.update({"observed": True, "verified": all(assertions.values()), "assertions": assertions,
                       "firstObservation": file_record(first_path), "secondObservation": file_record(second_path),
                       "observedStateDigest": first_digest, "tamperedProjectSha256": first_project_hash})
    except Exception as exc:
        result["reason"] = f"{type(exc).__name__}: {exc}"
    return result


def write_evidence(evidence: dict[str, Any]) -> None:
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    temp = ARTIFACTS / "qualification.json.tmp"
    temp.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temp.replace(ARTIFACTS / "qualification.json")


def main() -> int:
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:10]
    run_started_epoch = time.time()
    evidence: dict[str, Any] = {
        "schema": "aaaw-m5-linux-qualification/v1",
        "runId": run_id,
        "startedAt": utc_now(),
        "host": linux_preflight(),
        "source": {"start": source_state()},
        "packageProvenance": {"m1": {"ok": False}, "m5Runtime": {"ok": False}},
        "commands": [],
        "testRuns": [],
        "observed": {},
        "inferred": [],
        "notExercised": [
            "macOS qualification",
            "Gagamba owner-death cleanup",
            "cgroup escape resistance and hostile multi-user isolation",
            "power-loss recovery",
            "general exactly-once external effects",
        ],
        "qualificationPassed": False,
    }
    run_output = ARTIFACTS / run_id
    trx_dir = run_output / "trx"
    trx_dir.mkdir(parents=True, exist_ok=True)
    started_ok = False

    try:
        evidence["sdkVersion"] = run_command("dotnet-version", ["dotnet", "--version"], timeout_seconds=30)
        clean_source = evidence["source"]["start"].get("clean") is True
        linux_ready = evidence["host"].get("system") == "Linux" and evidence["host"].get("checks", {}).get("ready") is True
        if not clean_source or not linux_ready:
            raise RuntimeError("Linux delegated profile and clean committed source are required before qualification")
        bootstrap = run_command("bootstrap-pinned-feed", [sys.executable, "eng/bootstrap-aaaw-m5-feed.py"])
        evidence["commands"].append(bootstrap)
        if not bootstrap["ok"]:
            raise RuntimeError("The exact local consumer feed could not be materialized")
        runtime_packages = validate_package_lock(M5_LOCK, require_runtime_set=True)
        evidence["packageProvenance"]["m5Runtime"] = {"ok": True, "lock": file_record(M5_LOCK), "packages": runtime_packages}
        m1_packages = validate_package_lock(M1_LOCK)
        evidence["packageProvenance"]["m1"] = {"ok": True, "lock": file_record(M1_LOCK), "packages": m1_packages}
        started_ok = clean_source and linux_ready and evidence["sdkVersion"]["ok"] and all(
            item["ok"] for item in evidence["packageProvenance"].values()
        )
        evidence["preflightPassed"] = started_ok
        if not started_ok:
            raise RuntimeError("Linux, source-clean, SDK, or package-provenance preflight did not pass")

        environment = os.environ.copy()
        environment["MSBUILDDISABLENODEREUSE"] = "1"
        environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
        environment["VSTEST_CONNECTION_TIMEOUT"] = "180"
        environment["UseSharedCompilation"] = "false"
        inventory_commands = [
            run_command("dotnet-sdks", ["dotnet", "--list-sdks"], timeout_seconds=30, environment=environment),
            run_command("dotnet-runtimes", ["dotnet", "--list-runtimes"], timeout_seconds=30, environment=environment),
        ]
        evidence["commands"].extend(inventory_commands)
        sdk_text = inventory_commands[0].get("stdout", "")
        runtime_text = inventory_commands[1].get("stdout", "")
        sdk_majors = sorted({line.strip().split(".", 1)[0] for line in sdk_text.splitlines() if line.strip()})
        runtime_inventory = [line.strip() for line in runtime_text.splitlines() if line.strip()]
        evidence["runtimeInventory"] = {
            "dotnetSdks": sdk_text,
            "dotnetRuntimes": runtime_text,
            "sdkMajors": sdk_majors,
            "hasSdk8": any(version.startswith("8.") for version in (line.split()[0] for line in sdk_text.splitlines() if line.strip())),
            "hasSdk10": any(version.startswith("10.") for version in (line.split()[0] for line in sdk_text.splitlines() if line.strip())),
            "net8RuntimeVersions": [line for line in runtime_inventory if line.startswith("Microsoft.NETCore.App 8.")],
            "net10RuntimeVersions": [line for line in runtime_inventory if line.startswith("Microsoft.NETCore.App 10.")],
            "nativeHostPackagePin": "Microsoft.NETCore.App.Host.$(NETCoreSdkRuntimeIdentifier) 8.0.31",
        }
        inventory_ok = all(command["ok"] for command in inventory_commands) and all((
            evidence["runtimeInventory"]["hasSdk8"],
            evidence["runtimeInventory"]["hasSdk10"],
            bool(evidence["runtimeInventory"]["net8RuntimeVersions"]),
            bool(evidence["runtimeInventory"]["net10RuntimeVersions"]),
        ))
        evidence["runtimeInventory"]["inventoryPassed"] = inventory_ok
        safe_environment_names = (
            "GAGAMBA_LINUX_CGROUP", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROLL_FORWARD",
            "DOTNET_ROLL_FORWARD_TO_PRERELEASE", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_CLI_HOME",
            "NUGET_PACKAGES", "MSBuildSDKsPath", "MSBUILDDISABLENODEREUSE",
            "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER", "UseSharedCompilation", "VSTEST_CONNECTION_TIMEOUT",
        )
        evidence["qualificationEnvironment"] = {
            name: os.environ[name] for name in safe_environment_names if name in os.environ
        }
        if not inventory_ok:
            raise RuntimeError("required .NET 8/10 SDK and runtime inventory was incomplete")
        write_evidence(evidence)
        command_specs = [
            ("native-prerequisite-restore", ["dotnet", "restore", "eng/aaaw-m5-native-prerequisites.csproj",
                                              "--source", "https://api.nuget.org/v3/index.json", "--disable-parallel"]),
            ("restore", ["dotnet", "restore", "Marang.slnx", "--configfile", "NuGet.AAAWM1.Config", "--disable-parallel"]),
            ("build", ["dotnet", "build", "Marang.slnx", "-c", "Release", "--no-restore", "-m:1", "-p:UseSharedCompilation=false"]),
            ("format", ["dotnet", "format", "Marang.slnx", "--verify-no-changes", "--no-restore"]),
        ]
        command_map: dict[str, dict[str, Any]] = {}
        restore_ok = False
        build_ok = False
        for name, argv in command_specs:
            if name == "restore" and not command_map.get("native-prerequisite-restore", {}).get("ok"):
                evidence["commands"].append({"name": name, "argv": argv, "skipped": True,
                                              "reason": "native host prerequisite restore failed"})
                continue
            if name == "build" and not restore_ok:
                evidence["commands"].append({"name": name, "argv": argv, "skipped": True, "reason": "restore failed"})
                continue
            if name == "format" and not build_ok:
                evidence["commands"].append({"name": name, "argv": argv, "skipped": True, "reason": "build failed"})
                continue
            result = run_command(name, argv, environment=environment)
            command_map[name] = result
            evidence["commands"].append(result)
            write_evidence(evidence)
            if name == "restore":
                restore_ok = result["ok"]
            elif name == "build":
                build_ok = result["ok"]

        if build_ok:
            for framework in ("net8.0", "net10.0"):
                trx_path = trx_dir / f"m5-{framework}.trx"
                argv = [
                    "dotnet", "test", "tests/Marang.Tests/Marang.Tests.csproj",
                    "-c", "Release", "-f", framework, "--no-build", "--no-restore",
                    "--logger", f"trx;LogFileName={trx_path.name}",
                    "--results-directory", str(trx_dir),
                ]
                command = run_command(f"test-{framework}", argv, environment=environment)
                command_map[f"test-{framework}"] = command
                evidence["commands"].append(command)
                test_record: dict[str, Any] = {"framework": framework, "command": command, "trx": None, "passed": False}
                if trx_path.is_file():
                    try:
                        test_record.update(parse_trx(trx_path, framework))
                        test_record["passed"] = test_record["passed"] and command["ok"]
                    except Exception as exc:
                        test_record["parseError"] = f"{type(exc).__name__}: {exc}"
                        test_record["trx"] = file_record(trx_path)
                else:
                    test_record["parseError"] = "dotnet test did not write the requested TRX file"
                evidence["testRuns"].append(test_record)
                write_evidence(evidence)
        else:
            for framework in ("net8.0", "net10.0"):
                evidence["testRuns"].append({"framework": framework, "passed": False,
                                             "notRun": True, "reason": "solution build failed; no stale --no-build run attempted"})

        if build_ok:
            probe_root = ROOT / "artifacts" / "aaaw-m4" / "process-probes"
            main_path = find_newest(probe_root, "multi-effect-result.json", run_started_epoch)
            main_result = check_m4_scenario(main_path, run_started_epoch) if main_path else {
                "observed": False, "verified": False, "reason": "no fresh canonical M4 workflow result"}
            k4_path = find_newest(probe_root, "multi-effect-K4-result.json", run_started_epoch)
            k4_result = check_k4(k4_path, run_started_epoch) if k4_path else {
                "observed": False, "verified": False, "reason": "no fresh K4 partial-state result"}
            negative_root = ROOT / "artifacts" / "aaaw-m5" / "process-probes"
            negative_dirs = [path.parent for path in negative_root.rglob("multi-effect-reconcile-1.json")
                             if path.is_file() and path.stat().st_mtime >= run_started_epoch]
            negative_dir = max(negative_dirs, key=lambda item: (item / "multi-effect-reconcile-1.json").stat().st_mtime_ns) if negative_dirs else None
            negative = check_negative_control(negative_dir, run_started_epoch) if negative_dir else {
                "observed": False, "verified": False, "reason": "no fresh M5 actual-state negative-control results"}
            scenario_dir = main_path.parent if main_path else probe_root / "missing"
            k4_dir = k4_path.parent if k4_path else probe_root / "missing-k4"
            barriers = check_barriers(scenario_dir, k4_dir, run_started_epoch)
            evidence["observed"].update({
                "m4CanonicalWorkflow": main_result,
                "m4PartialK4": k4_result,
                "barriers": barriers,
                "m5ActualStateNegativeControl": negative,
            })
            platform_ids = set()
            for barrier in barriers.values():
                receipt_path_text = barrier.get("stopReceipt", {}).get("path")
                if receipt_path_text:
                    try:
                        receipt = json.loads((ROOT / receipt_path_text).read_text(encoding="utf-8"))
                        root_receipt = receipt.get("root") or receipt.get("Root") or {}
                        if root_receipt.get("platform"):
                            platform_ids.add(root_receipt["platform"])
                        for command in receipt.get("brokerCommands") or receipt.get("BrokerCommands") or []:
                            if command.get("platform"):
                                platform_ids.add(command["platform"])
                    except Exception:
                        pass
            evidence["observed"]["gagambaPlatformsInReceipts"] = sorted(platform_ids)
            evidence["observed"]["platformMatchesLinuxCgroupV2"] = platform_ids == {"linux-cgroup-v2"}
        else:
            evidence["observed"]["m4CanonicalWorkflow"] = {"observed": False, "verified": False, "reason": "build failed"}
            evidence["observed"]["m4PartialK4"] = {"observed": False, "verified": False, "reason": "build failed"}
            evidence["observed"]["m5ActualStateNegativeControl"] = {"observed": False, "verified": False, "reason": "build failed"}

        end_state = source_state()
        evidence["source"]["end"] = end_state
        evidence["source"]["unchangedAndClean"] = (
            end_state.get("clean") is True
            and end_state.get("commit") == evidence["source"]["start"].get("commit")
        )
        tests_ok = len(evidence["testRuns"]) == 2 and all(item.get("passed") is True for item in evidence["testRuns"])
        observed = evidence["observed"]
        behavior_ok = all((
            observed.get("m4CanonicalWorkflow", {}).get("verified") is True,
            observed.get("m4PartialK4", {}).get("verified") is True,
            observed.get("m5ActualStateNegativeControl", {}).get("verified") is True,
            observed.get("platformMatchesLinuxCgroupV2") is True,
            len(observed.get("barriers", {})) == 12,
            all(item.get("verified") is True for item in observed.get("barriers", {}).values()),
        )) if build_ok else False
        evidence["inferred"] = [
            "A fresh passing canonical result plus Linux Gagamba receipts supports the tested lifecycle path on this one host and its current delegated cgroup v2 subtree.",
            "Passing M1-M4 consumer suites establish compatibility only for the pinned package set and recorded SDK/runtime environment.",
        ]
        evidence["qualificationPassed"] = bool(
            started_ok and restore_ok and build_ok
            and command_map.get("format", {}).get("ok") is True
            and tests_ok and behavior_ok
            and evidence["source"].get("unchangedAndClean") is True
        )
    except Exception as exc:
        evidence["failure"] = {"type": type(exc).__name__, "message": str(exc), "traceback": traceback.format_exc(limit=12)}
        evidence["qualificationPassed"] = False
    finally:
        evidence["completedAt"] = utc_now()
        evidence.setdefault("source", {})["end"] = source_state()
        start = evidence.get("source", {}).get("start", {})
        end = evidence.get("source", {}).get("end", {})
        evidence["source"]["unchangedAndClean"] = (
            end.get("clean") is True and end.get("commit") == start.get("commit")
        )
        write_evidence(evidence)

    print(json.dumps({
        "qualificationPassed": evidence.get("qualificationPassed", False),
        "evidence": str((ARTIFACTS / "qualification.json").relative_to(ROOT)),
        "runId": run_id,
        "failure": evidence.get("failure"),
    }, indent=2))
    return 0 if evidence.get("qualificationPassed") else 1


if __name__ == "__main__":
    sys.exit(main())
