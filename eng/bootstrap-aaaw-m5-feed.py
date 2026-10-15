#!/usr/bin/env python3
"""Materialize exact qualification inputs; never publish or resolve a new version."""
import hashlib
import json
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
pins = json.loads((root / 'eng/aaaw-m2-packages.json').read_text(encoding='utf-8'))
pins += json.loads((root / 'eng/aaaw-m5-runtime-packages.json').read_text(encoding='utf-8'))
expected = {f"{pin['id']}.{pin['version']}.nupkg": pin for pin in pins}
if len(expected) != 9:
    raise ValueError('Expected nine uniquely pinned qualification packages')
destination = root / 'artifacts/aaaw-m1/packages'
with zipfile.ZipFile(root / 'eng/aaaw-m5/consumer-feed.zip') as archive:
    if len(archive.infolist()) != 9 or set(archive.namelist()) != set(expected):
        raise ValueError('Feed snapshot differs from the exact pinned package set')
    verified = {}
    for name, pin in expected.items():
        entry = archive.getinfo(name)
        if entry.file_size > 16 * 1024 * 1024:
            raise ValueError('Qualification package exceeds the fixture bound')
        data = archive.read(entry)
        if hashlib.sha256(data).hexdigest() != pin['sha256']:
            raise ValueError(f'Qualification package hash mismatch: {name}')
        verified[name] = data
    destination.mkdir(parents=True, exist_ok=True)
    for name, data in verified.items():
        (destination / name).write_bytes(data)
print('Restored nine hash-pinned local qualification packages; nothing published.')
