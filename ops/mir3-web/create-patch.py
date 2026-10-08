#!/usr/bin/env python3
"""Stage Zircon's original PList.Bin/gzip format from a reviewed client tree.

The PatchManager UI uses the same wire layout and compressed flat names. This
headless path is for building a first patch from the already verified LXC client
archive without copying gigabytes to a Windows workstation or using FTP.
"""
import argparse
import gzip
import hashlib
import os
from pathlib import Path
import shutil
import struct
import sys

EXCLUDED = {
    'launcher.ini', 'version.bin', 'sha256sums.txt',
    'hermes-build.txt', 'patcher-error.log',
}
RESERVED = {'con', 'prn', 'aux', 'nul'} | {f'{prefix}{digit}' for prefix in ('com', 'lpt') for digit in range(1, 10)}


def safe_relative(path: Path) -> bool:
    text = str(path).replace(os.sep, '\\')
    if len(text) > 260:
        return False
    for part in path.parts:
        if not part or part in ('.', '..') or part[-1] in ('.', ' '):
            return False
        if part.split('.', 1)[0].lower() in RESERVED:
            return False
        if any(ord(c) < 32 or c in ':*?"<>|\\/' for c in part):
            return False
    return True


def include(relative: Path) -> bool:
    name = relative.as_posix().lower()
    return name not in EXCLUDED and not name.endswith('.pdb') and not name.startswith('errors/') and name != 'chat logs.txt'


def write_string(stream, value: str):
    data = value.encode('utf-8')
    length = len(data)
    while length >= 128:
        stream.write(bytes([(length & 127) | 128]))
        length >>= 7
    stream.write(bytes([length]))
    stream.write(data)


def reject_symlinks(path: Path):
    for item in (path, *path.parents):
        if item.is_symlink():
            raise ValueError(f'Reparse point in path: {item}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    client = args.client.absolute()
    output = args.output.absolute()
    reject_symlinks(client)
    reject_symlinks(output)
    if not client.is_dir():
        raise ValueError('Client root does not exist')
    if output.exists() or output.is_symlink():
        raise ValueError('Output directory already exists; refusing overwrite')
    if output.is_relative_to(client) or client.is_relative_to(output):
        raise ValueError('Output and client directories must be separate')

    # Explicit loading also supports tests importing this hyphenated script.
    import importlib.util
    spec = importlib.util.spec_from_file_location('publication_input', Path(__file__).with_name('publication_input.py'))
    validation = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validation)
    validation.validate_publication_input(client)

    files = []
    payloads = set()
    paths = set()
    for path in client.rglob('*'):
        if path.is_symlink():
            raise ValueError(f'Symlink in client tree: {path}')
        if not path.is_file():
            continue
        relative = path.relative_to(client)
        if not include(relative):
            continue
        if not safe_relative(relative):
            raise ValueError(f'Unsafe patch path: {relative}')
        name = str(relative).replace(os.sep, '\\')
        payload = name.replace('\\', '-') + '.gz'
        if name.lower() in paths or payload.lower() in payloads:
            raise ValueError(f'Duplicate patch path or payload: {relative}')
        paths.add(name.lower())
        payloads.add(payload.lower())
        files.append((path, name, payload))
    if not files or len(files) > 200000:
        raise ValueError('Empty or oversized patch input')
    files.sort(key=lambda item: item[1].lower())

    output.mkdir(mode=0o700)
    try:
        with (output / 'PList.Bin.tmp').open('wb') as manifest:
            for source, name, payload in files:
                checksum = hashlib.md5()
                with source.open('rb') as input_file, (output / payload).open('xb') as compressed:
                    with gzip.GzipFile(fileobj=compressed, mode='wb', filename='', mtime=0) as gz:
                        while chunk := input_file.read(1024 * 1024):
                            checksum.update(chunk)
                            gz.write(chunk)
                write_string(manifest, name)
                manifest.write(struct.pack('<qi', (output / payload).stat().st_size, 16))
                manifest.write(checksum.digest())
        if (output / 'PList.Bin.tmp').stat().st_size > 64 * 1024 * 1024:
            raise ValueError('Manifest exceeds launcher limit')
        os.replace(output / 'PList.Bin.tmp', output / 'PList.Bin')
        print(f'STAGED files={len(files)} manifestBytes={(output / "PList.Bin").stat().st_size} output={output}')
    except BaseException:
        shutil.rmtree(output)
        raise


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError) as exc:
        print(f'Patch creation failed: {exc}', file=sys.stderr)
        sys.exit(1)
