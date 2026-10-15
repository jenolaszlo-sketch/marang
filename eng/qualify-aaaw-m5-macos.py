#!/usr/bin/env python3
"""Native macOS qualification attempt. Refusals are evidence, never PASS."""
import importlib.util
import json
import os
from pathlib import Path
import platform
import sys
import time
import traceback
import zipfile

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('m5_shared', ROOT / 'eng/qualify-aaaw-m5.py')
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
OUT = ROOT / 'artifacts/aaaw-m5/macos-qualification'


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    started = time.time()
    environment = os.environ.copy()
    environment.update(UseSharedCompilation='false', MSBUILDDISABLENODEREUSE='1',
                       DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER='1', VSTEST_CONNECTION_TIMEOUT='180',
                       PYTHONDONTWRITEBYTECODE='1')
    record = {'schema': 'aaaw-m5-macos-attempt/v1', 'startedAt': shared.utc_now(),
              'source': {'start': shared.source_state()}, 'commands': [], 'testRuns': [],
              'qualificationPassed': False, 'observed': {},
              'host': {'platform': platform.platform(), 'machine': platform.machine(),
                       'effectiveUid': os.geteuid(), 'tempDirectory': environment.get('TMPDIR'),
                       'imageOS': environment.get('ImageOS'), 'imageVersion': environment.get('ImageVersion')},
              'notExercised': ['Gagamba producer full macOS source suite: exact unpublished producer commit is not remotely available.',
                               'Owner-death/setsid escape containment, hostile isolation, power loss and generic exactly-once effects.']}

    def save():
        (OUT / 'qualification.json').write_text(json.dumps(record, indent=2) + '\n')

    def command(name, argv, timeout=1800, required=True):
        print(name, flush=True)
        result = shared.run_command(name, argv, timeout, environment)
        record['commands'].append(result)
        save()
        if required and not result['ok']:
            raise RuntimeError(f'{name} failed; original output and cleanup retained')
        return result

    try:
        if platform.system() != 'Darwin' or not record['source']['start']['clean']:
            raise RuntimeError('Requires native macOS and clean source')
        command('sw-vers', ['/usr/bin/sw_vers'])
        command('sdk-info', ['dotnet', '--info'])
        command('runtime-info', ['dotnet', '--list-runtimes'])
        command('bootstrap-pinned-feed', [sys.executable, 'eng/bootstrap-aaaw-m5-feed.py'])
        record['packages'] = {
            'm1': shared.validate_package_lock(shared.M1_LOCK),
            'runtime': shared.validate_package_lock(shared.M5_LOCK, require_runtime_set=True)}
        command('native-prerequisite-restore', ['dotnet', 'restore', 'eng/aaaw-m5-native-prerequisites.csproj',
                                               '--source', 'https://api.nuget.org/v3/index.json', '--disable-parallel'])
        command('restore', ['dotnet', 'restore', 'Marang.slnx', '--configfile', 'NuGet.AAAWM1.Config', '--disable-parallel'])
        command('build', ['dotnet', 'build', 'Marang.slnx', '-c', 'Release', '--no-restore', '-m:1', '-p:UseSharedCompilation=false'])
        command('format', ['dotnet', 'format', 'Marang.slnx', '--verify-no-changes', '--no-restore'])
        probe = 'eng/aaaw-m5-macos-probe/aaaw-m5-macos-probe.csproj'
        command('native-probe-restore', ['dotnet', 'restore', probe, '--configfile', 'NuGet.AAAWM1.Config'])
        command('native-probe-build', ['dotnet', 'build', probe, '-c', 'Release', '--no-restore', '-p:UseSharedCompilation=false'])
        command('native-profile-controls', ['dotnet', 'eng/aaaw-m5-macos-probe/bin/Release/net10.0/aaaw-m5-macos-probe.dll',
                                             str(OUT / 'native-profile.json')], 300, required=False)
        native = OUT / 'native-profile.json'
        record['observed']['nativeProfile'] = json.loads(native.read_text()) if native.exists() else {'missing': True}
        for framework in ['net8.0', 'net10.0']:
            trx = OUT / f'm5-{framework}.trx'
            result = command('test-' + framework, ['dotnet', 'test', 'tests/Marang.Tests/Marang.Tests.csproj',
                              '-c', 'Release', '-f', framework, '--no-build', '--no-restore',
                              '--logger', f'trx;LogFileName={trx.name}', '--results-directory', str(OUT)], required=False)
            parsed = shared.parse_trx(trx, framework) if trx.exists() else {'framework': framework, 'missingTrx': True}
            parsed['commandOk'] = result['ok']
            record['testRuns'].append(parsed)
            save()
        scenario = shared.find_newest(ROOT / 'artifacts/aaaw-m4/process-probes', 'multi-effect-result.json', started)
        partial = shared.find_newest(ROOT / 'artifacts/aaaw-m4/process-probes', 'multi-effect-K4-result.json', started)
        control = shared.find_newest(ROOT / 'artifacts/aaaw-m5/process-probes', 'multi-effect-reconcile-2.json', started)
        record['observed']['canonical'] = shared.check_m4_scenario(scenario, started) if scenario else {'observed': False, 'verified': False}
        record['observed']['partial'] = shared.check_k4(partial, started) if partial else {'observed': False, 'verified': False}
        record['observed']['barriers'] = shared.check_barriers(
            scenario.parent if scenario else ROOT / 'artifacts/missing-macos-canonical',
            partial.parent if partial else ROOT / 'artifacts/missing-macos-k4', started)
        record['observed']['actualStateControl'] = shared.check_negative_control(control.parent, started) if control else {'observed': False, 'verified': False}
        # A complete native semantic run is required. A refusal probe alone never qualifies AAAW.
        record['qualificationPassed'] = (all(c['ok'] for c in record['commands'])
            and all(r.get('passed') for r in record['testRuns'])
            and record['observed']['canonical'].get('verified') is True
            and record['observed']['partial'].get('verified') is True
            and len(record['observed']['barriers']) == 12
            and all(b.get('verified') is True for b in record['observed']['barriers'].values())
            and record['observed']['actualStateControl'].get('verified') is True)
    except Exception as exc:
        record['failure'] = f'{type(exc).__name__}: {exc}'
        record['traceback'] = traceback.format_exc(limit=6)
    finally:
        record['source']['end'] = shared.source_state()
        record['source']['unchangedAndClean'] = (record['source']['start']['clean']
            and record['source']['end']['clean']
            and record['source']['start']['commit'] == record['source']['end']['commit'])
        record['qualificationPassed'] &= record['source']['unchangedAndClean']
        record['completedAt'] = shared.utc_now()
        save()
        selected = set(OUT.glob('*.json')) | set(OUT.glob('*.trx'))
        for folder in ['aaaw-m4', 'aaaw-m5']:
            for path in (ROOT / 'artifacts' / folder / 'process-probes').glob('**/*'):
                if path.is_file() and path.suffix in {'.json', '.txt', '.log'}:
                    selected.add(path)
        manifest = []
        with zipfile.ZipFile(OUT / 'captured-artifacts.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
            for path in sorted(selected):
                manifest.append(shared.file_record(path))
                archive.write(path, path.relative_to(ROOT).as_posix())
            archive.writestr('capture-manifest.json', json.dumps({'files': manifest}, indent=2))
        print(json.dumps({'qualificationPassed': record['qualificationPassed'], 'evidence': str(OUT / 'qualification.json')}, indent=2))
    return 0 if record['qualificationPassed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
