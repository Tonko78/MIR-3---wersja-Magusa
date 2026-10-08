#!/usr/bin/env python3
"""Validate every binary Zircon manifest entry against its gzip payload."""
import argparse
import gzip
import hashlib
from pathlib import Path
import struct
import sys


def read_7bit(stream):
    count = shift = 0
    for _ in range(5):
        byte = stream.read(1)
        if not byte:
            raise ValueError('Truncated string length')
        count |= (byte[0] & 127) << shift
        if byte[0] < 128:
            return count
        shift += 7
    raise ValueError('Oversized string length')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--client', type=Path, required=True)
    args = parser.parse_args()
    directory, client = args.directory, args.client
    manifest = directory / 'PList.Bin'
    if not manifest.is_file() or manifest.stat().st_size > 64 * 1024 * 1024:
        raise ValueError('Manifest missing or too large')
    seen = set()
    count = total = 0
    with manifest.open('rb') as stream:
        while stream.tell() < manifest.stat().st_size:
            length = read_7bit(stream)
            if length < 1 or length > 1024:
                raise ValueError('Invalid manifest name length')
            name = stream.read(length).decode('utf-8')
            fields = stream.read(12)
            if len(fields) != 12:
                raise ValueError('Truncated manifest entry')
            size, digest_length = struct.unpack('<qi', fields)
            if digest_length != 16:
                raise ValueError('Invalid digest length')
            digest = stream.read(digest_length)
            if len(digest) != 16:
                raise ValueError('Invalid digest')
            path = Path(*name.replace('/', '\\').split('\\'))
            if any(part in ('', '.', '..') for part in path.parts) or path.is_absolute():
                raise ValueError(f'Unsafe file name: {name}')
            payload_name = name.replace('\\', '-').replace('/', '-') + '.gz'
            if payload_name.lower() in seen:
                raise ValueError(f'Duplicate payload: {payload_name}')
            seen.add(payload_name.lower())
            payload = directory / payload_name
            if not payload.is_file() or payload.stat().st_size != size:
                raise ValueError(f'Missing/incorrect payload: {payload_name}')
            source = client / path
            if not source.is_file():
                raise ValueError(f'Missing source: {name}')
            source_hash, unpacked_hash = hashlib.md5(), hashlib.md5()
            with source.open('rb') as src, gzip.open(payload, 'rb') as gz:
                while data := src.read(1024 * 1024):
                    source_hash.update(data)
                while data := gz.read(1024 * 1024):
                    unpacked_hash.update(data)
            if source_hash.digest() != digest or unpacked_hash.digest() != digest:
                raise ValueError(f'Checksum mismatch: {name}')
            count += 1
            total += size
    actual = {f.name.lower() for f in directory.iterdir() if f.is_file() and f.suffix == '.gz'}
    if not count or actual != seen:
        raise ValueError(f'Payload list mismatch: entries={count} files={len(actual)}')
    print(f'VERIFIED entries={count} compressedBytes={total} manifestBytes={manifest.stat().st_size}')


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, EOFError, gzip.BadGzipFile) as exc:
        print(f'PATCH_VERIFICATION_FAILED: {exc}', file=sys.stderr)
        sys.exit(1)
