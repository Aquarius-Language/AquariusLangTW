#!/usr/bin/env python3
"""Prepare a pinned CUDA inference payload for source runtime-pack publishing.

Uses only Python's standard library. Released aqua compilers already contain the
published pack; neither application developers nor recipients run this script.
"""
import argparse
import base64
import csv
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile
import urllib.request
import zipfile


def digest(file):
    with file.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest() if hasattr(hashlib, 'file_digest') else stream_hash(stream)


def stream_hash(stream):
    h = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b''):
        h.update(block)
    return h.hexdigest()


def prepare(runtime, output, source=None):
    lock_path = Path(__file__).with_name('cuda-runtime.lock.json')
    lock = json.loads(lock_path.read_text())
    manifest_path = output / 'aquarius-cuda-runtime.json'
    lock_hash = digest(lock_path)
    if manifest_path.is_file():
        manifest = json.loads(manifest_path.read_text())
        if manifest.get('format') != 'aquarius-cuda-runtime' or not isinstance(manifest.get('files'), list):
            raise ValueError('Refusing to replace an unrelated CUDA payload directory: ' + str(output))
        allowed = {f['path'] for f in manifest['files']} | {'aquarius-cuda-runtime.json'}
        if any(p.is_symlink() or (p.is_file() and p.relative_to(output).as_posix() not in allowed) for p in output.rglob('*')):
            raise ValueError('CUDA payload contains unlisted files or links; preserving the existing directory.')
        if manifest.get('lockSha256') == lock_hash and manifest.get('runtimeIdentifier') == runtime:
            if all((output / f['path']).is_file() and digest(output / f['path']) == f['sha256'] for f in manifest['files']):
                print('CUDA runtime payload verified:', output)
                return
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists() and not manifest_path.is_file():
        raise ValueError('Refusing to replace an unrelated CUDA payload directory: ' + str(output))
    with tempfile.TemporaryDirectory(prefix='.cuda-prepare-', dir=output.parent) as temporary:
        stage = Path(temporary) / 'payload'
        stage.mkdir()
        entries = []
        for package in lock['packages']:
            stem = package['name'].replace('-', '_') + '-' + package['version'] + '.dist-info'
            if source:
                metadata = source / stem
                wheel_info = (metadata / 'WHEEL').read_text()
                if ('win_amd64' if runtime == 'win-x64' else 'x86_64') not in wheel_info or (runtime == 'linux-x64' and 'manylinux' not in wheel_info):
                    raise ValueError('Installed NVIDIA package has the wrong platform: ' + stem)
                records = {name: (hash_value, size) for name, hash_value, size in csv.reader((metadata / 'RECORD').read_text().splitlines())}
                names = records.keys()
                def read(name):
                    file = source / name
                    value = file.read_bytes()
                    expected = records[name][0]
                    actual = 'sha256=' + base64.urlsafe_b64encode(hashlib.sha256(value).digest()).decode().rstrip('=')
                    if expected != actual:
                        raise ValueError('Installed NVIDIA package checksum mismatch: ' + name)
                    return value
                archive = None
            else:
                wheel = Path(temporary) / (stem + '.whl')
                pin = package[runtime]
                print('Downloading pinned', package['name'], package['version'], flush=True)
                with urllib.request.urlopen(pin['url'], timeout=180) as response, wheel.open('wb') as target:
                    shutil.copyfileobj(response, target)
                if digest(wheel) != pin['sha256']:
                    raise ValueError('NVIDIA wheel checksum mismatch: ' + package['name'])
                archive = zipfile.ZipFile(wheel)
                names = archive.namelist()
                read = archive.read
            try:
                selected = []
                for name in names:
                    filename = PurePosixPath(name).name
                    native = name.startswith('nvidia/') and (filename.endswith('.dll') if runtime == 'win-x64' else re.search(r'\.so(?:\.[\d.]+)?$', filename))
                    license_file = name.startswith(stem + '/licenses/') and not name.endswith('/')
                    if not native and not license_file:
                        continue
                    relative = filename if native else 'licenses/onnx-cuda/' + stem + '-' + filename
                    target = stage / relative
                    if target.exists():
                        raise ValueError('Duplicate CUDA runtime file: ' + relative)
                    value = read(name)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(value)
                    selected.append(relative)
                    entries.append({'path': relative, 'bytes': len(value), 'sha256': hashlib.sha256(value).hexdigest()})
                if not any(n.startswith('licenses/') for n in selected) or not any(not n.startswith('licenses/') for n in selected):
                    raise ValueError('NVIDIA package is missing runtime binaries or licenses: ' + stem)
            finally:
                if archive:
                    archive.close()
        manifest = {'format': 'aquarius-cuda-runtime', 'version': 1, 'runtimeIdentifier': runtime, 'lockSha256': lock_hash,
                    'packages': [{'name': p['name'], 'version': p['version']} for p in lock['packages']], 'files': sorted(entries, key=lambda f: f['path'])}
        (stage / 'aquarius-cuda-runtime.json').write_text(json.dumps(manifest, indent=2) + '\n')
        backup = Path(temporary) / 'previous'
        if output.exists():
            output.rename(backup)
        try:
            stage.rename(output)
        except BaseException:
            if backup.exists():
                backup.rename(output)
            raise
    print('CUDA runtime payload prepared:', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', choices=['win-x64', 'linux-x64'], required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--source', type=Path, help='Reuse an installed pinned NVIDIA wheel tree, verified against its RECORD hashes.')
    args = parser.parse_args()
    prepare(args.runtime, args.output.resolve(), args.source.resolve() if args.source else None)
