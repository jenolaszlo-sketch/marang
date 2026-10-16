#!/usr/bin/env python3
"""Build the immutable M6.1 writer used by M6.2 cross-version probes."""

from __future__ import annotations

import hashlib
import json
import os
import platform
import subprocess
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
OLD_SHA = "5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29"
OLD_ROOT = ROOT / "artifacts" / "aaaw-m6-2" / "oldwriter-source"
OUTPUT = ROOT / "artifacts" / "aaaw-m6-2" / "oldwriter-build.json"
PROBE = OLD_ROOT / "tests" / "Marang.RestartProbe" / "bin" / "Release" / "net10.0" / "Marang.RestartProbe.dll"


def run(name: str, argv: list[str], cwd: Path, timeout: int = 1800) -> dict[str, Any]:
    started = datetime.now(timezone.utc).isoformat()
    result = subprocess.run(argv, cwd=cwd, capture_output=True, text=True, timeout=timeout)
    return {
        "name": name,
        "argv": argv,
        "cwd": str(cwd),
        "startedAt": started,
        "finishedAt": datetime.now(timezone.utc).isoformat(),
        "exitCode": result.returncode,
        "stdout": result.stdout,
        "stderr": result.stderr,
        "passed": result.returncode == 0,
    }


def git(cwd: Path, *args: str) -> str:
    result = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, check=True)
    return result.stdout.strip()


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> int:
    evidence: dict[str, Any] = {
        "schema": "aaaw-m6-2-oldwriter-build/v1",
        "oldSourceSha": OLD_SHA,
        "platform": platform.platform(),
        "machine": platform.machine(),
        "commands": [],
        "passed": False,
    }
    try:
        current = git(ROOT, "rev-parse", "HEAD")
        evidence["newSourceSha"] = current
        evidence["newSourceClean"] = git(ROOT, "status", "--porcelain=v1", "--untracked-files=all") == ""
        if not evidence["newSourceClean"]:
            raise RuntimeError("Build the old writer only from a clean, committed M6.2 checkout.")
        if OLD_ROOT.exists():
            raise RuntimeError(f"Old-writer checkout already exists; preserve it and choose a clean job directory: {OLD_ROOT}")

        worktree = run("old-source-worktree", ["git", "worktree", "add", "--detach", str(OLD_ROOT), OLD_SHA], ROOT)
        evidence["commands"].append(worktree)
        if not worktree["passed"]:
            raise RuntimeError("Could not create the exact old-source worktree.")
        observed = git(OLD_ROOT, "rev-parse", "HEAD")
        status = git(OLD_ROOT, "status", "--porcelain=v1", "--untracked-files=all")
        evidence["oldSource"] = {"commit": observed, "clean": status == "", "status": status.splitlines()}
        if observed != OLD_SHA or status:
            raise RuntimeError("Old writer checkout is not exact and clean.")
        lock_files = sorted(OLD_ROOT.rglob("packages.lock.json"))
        evidence["oldPackageInputs"] = {
            "nugetConfig": {"path": str(OLD_ROOT / "NuGet.AAAWM1.Config"),
                            "sha256": digest(OLD_ROOT / "NuGet.AAAWM1.Config")},
            "lockFiles": [{"path": str(path.relative_to(OLD_ROOT)), "sha256": digest(path)} for path in lock_files],
            "probeProjectSha256": digest(OLD_ROOT / "tests" / "Marang.RestartProbe" / "Marang.RestartProbe.csproj"),
        }

        evidence["dotnetInfo"] = run("dotnet-info", ["dotnet", "--info"], OLD_ROOT, 60)
        evidence["dotnetSdks"] = run("dotnet-sdks", ["dotnet", "--list-sdks"], OLD_ROOT, 60)
        evidence["dotnetRuntimes"] = run("dotnet-runtimes", ["dotnet", "--list-runtimes"], OLD_ROOT, 60)
        commands = [
            ("restore-pinned-feed", [sys.executable, "eng/bootstrap-aaaw-m5-feed.py"]),
            ("restore-old-probe", ["dotnet", "restore", "tests/Marang.RestartProbe/Marang.RestartProbe.csproj",
                                   "--configfile", "NuGet.AAAWM1.Config", "--disable-parallel"]),
            ("build-old-probe", ["dotnet", "build", "tests/Marang.RestartProbe/Marang.RestartProbe.csproj",
                                  "-c", "Release", "--no-restore", "-m:1", "-p:UseSharedCompilation=false"]),
        ]
        environment = dict(os.environ)
        environment.update({"UseSharedCompilation": "false", "MSBUILDDISABLENODEREUSE": "1",
                            "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER": "1", "PYTHONDONTWRITEBYTECODE": "1"})
        for name, argv in commands:
            start = datetime.now(timezone.utc).isoformat()
            result = subprocess.run(argv, cwd=OLD_ROOT, capture_output=True, text=True, timeout=1800, env=environment)
            item = {"name": name, "argv": argv, "cwd": str(OLD_ROOT), "startedAt": start,
                    "finishedAt": datetime.now(timezone.utc).isoformat(), "exitCode": result.returncode,
                    "stdout": result.stdout, "stderr": result.stderr, "passed": result.returncode == 0}
            evidence["commands"].append(item)
            if result.returncode != 0:
                raise RuntimeError(f"{name} failed; its complete output is retained in {OUTPUT}.")

        assets_path = OLD_ROOT / "tests" / "Marang.RestartProbe" / "obj" / "project.assets.json"
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
        evidence["resolvedOldProbePackages"] = [
            {"id": name.rsplit("/", 1)[0], "version": name.rsplit("/", 1)[1],
             "sha512": metadata.get("sha512"), "path": metadata.get("path")}
            for name, metadata in sorted(assets.get("libraries", {}).items()) if metadata.get("type") == "package"
        ]

        observed = git(OLD_ROOT, "rev-parse", "HEAD")
        status = git(OLD_ROOT, "status", "--porcelain=v1", "--untracked-files=all")
        evidence["oldSourceEnd"] = {"commit": observed, "clean": status == "", "status": status.splitlines()}
        if observed != OLD_SHA or status:
            raise RuntimeError("Old writer source changed during its build.")
        if not PROBE.is_file():
            raise FileNotFoundError(f"Built old probe is missing: {PROBE}")
        evidence["oldProbe"] = {"path": str(PROBE), "bytes": PROBE.stat().st_size,
                                "sha256": hashlib.sha256(PROBE.read_bytes()).hexdigest()}
        evidence["passed"] = True
    except Exception as exc:
        evidence["failure"] = {"type": type(exc).__name__, "message": str(exc),
                               "traceback": traceback.format_exc(limit=10)}
    finally:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"passed": evidence["passed"], "evidence": str(OUTPUT)}, indent=2))
    return 0 if evidence["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
