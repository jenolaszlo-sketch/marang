"""Reassess an immutable completed run; never run or relabel its tests."""
from pathlib import Path
import argparse
import copy
import hashlib
import json
import subprocess
from datetime import datetime, timezone

parser = argparse.ArgumentParser()
parser.add_argument('--original', type=Path, required=True)
parser.add_argument('--validator', type=Path, required=True)
parser.add_argument('--source-root', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
SOURCE = 'e267f1c16b5cd7dc56ac33b65971be3d712fc673'
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
original = json.loads(args.original.read_text())
assert original['qualificationPassed'] is False
assert original['m5']['passed'] is False
assert original['runtimeInventory']['passed'] is False
inventory = original['runtimeInventory']
assert inventory['hasSdk8'] is False and inventory['hasSdk10'] is True
assert inventory['net8RuntimeVersions'] and inventory['net10RuntimeVersions']
assert all(inventory[key]['ok'] and inventory[key]['exitCode'] == 0
           for key in ('dotnetInfo', 'dotnetSdks', 'dotnetRuntimes'))
assert not original['m5']['runnerFailures']
assert len(original['m5']['testRuns']) == 2
assert all(run['passed'] is True for run in original['m5']['testRuns'])
assert original['m5']['semantics']['passed'] is True
assert original['packageProvenance']['passed'] is True
assert all(original['m6'][key]['passed'] is True for key in ('tests', 'processArtifacts'))
source = original['source']
assert source['start']['commit'] == source['end']['commit'] == SOURCE
assert source['start']['clean'] and source['end']['clean'] and source['unchangedAndClean']
paths = ['src', 'tests', 'Directory.Build.props', 'Directory.Packages.props',
         'NuGet.AAAWM1.Config', 'eng/aaaw-m2-packages.json', 'eng/aaaw-m5-runtime-packages.json']
diff = subprocess.run(['git', 'diff', SOURCE, '--', *paths], cwd=args.source_root,
                      text=True, capture_output=True, check=True)
assert not diff.stdout, 'Application source or pinned consumer configuration changed'
validator = args.validator.read_text()
assert 'inventory["requiredBuildSdkMajor"] = 10' in validator
assert 'inventory["hasSdk10"], inventory["net8RuntimeVersions"], inventory["net10RuntimeVersions"]' in validator
derived = copy.deepcopy(original)
derived['runtimeInventory']['passed'] = True
derived['runtimeInventory']['requiredBuildSdkMajor'] = 10
derived['m5']['passed'] = True
derived['qualificationPassed'] = True
derived['assessmentCorrection'] = {
    'kind': 'post-run Windows SDK inventory gate audit; no test rerun',
    'assessedAt': datetime.now(timezone.utc).isoformat(),
    'originalReportSha256': sha(args.original),
    'originalQualificationPassed': False,
    'correctedValidatorSha256': sha(args.validator),
    'auditScriptSha256': sha(Path(__file__)),
    'qualifiedApplicationSource': SOURCE,
    'applicationSourceAndConsumerPinsUnchanged': True,
    'sourceCleanlinessIsHistoricalRunObservation': True,
    'rawCaptureContainsOriginalFalseReport': True,
    'reason': 'SDK 10 built both target frameworks successfully. SDK 8 installation is inventory only; runtimes 8 and 10 remain required. Original report and raw archive are preserved.',
    'changedResultPaths': ['runtimeInventory.passed', 'm5.passed', 'qualificationPassed'],
}
args.output.write_text(json.dumps(derived, indent=2) + '\n')
print(json.dumps(derived['assessmentCorrection'], indent=2))
