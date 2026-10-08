#!/usr/bin/env python3
"""Build the complete Mir3 Zircon client ZIP without launcher binaries.

Reads a reviewed extracted Windows client tree and produces a ZIP archive
containing the game executable and its data, excluding Launcher.exe,
Patcher.exe, debug symbols (.pdb), and stale build/checksum metadata. A fresh
SHA256SUMS.txt is generated from the ZIP contents. Output is written to a
unique temporary file and atomically linked into place; a caller-supplied
existing output is never overwritten, and cleanup removes only the temporary
file created by this tool.
"""

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import sys
import tempfile
import zipfile

EXCLUDED_SUFFIXES = {".pdb"}
EXCLUDED_PREFIXES = ("hermes-build",)
SUMS_NAME = "SHA256SUMS.txt"
EXCLUDED_NAMES = {"launcher.exe", "patcher.exe", SUMS_NAME.lower()}

REQUIRED = ("Zircon.exe", "Zircon.dll", "Data/System.db", "Zircon.ini")

WINDOWS_RESERVED = {"CON", "PRN", "AUX", "NUL"} | {
    f"{prefix}{index}" for prefix in ("COM", "LPT") for index in range(1, 10)
}
WINDOWS_INVALID = set('<>:"|?*') | {'\\'}


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", required=True, type=Path,
                        help="reviewed extracted Windows client tree")
    parser.add_argument("--output", required=True, type=Path,
                        help="destination ZIP path; must not already exist")
    return parser.parse_args(argv)


def check_windows_name(component):
    if not component or component in (".", ".."):
        return f"empty or relative component {component!r}"
    stem = component.split(".", 1)[0].upper()
    if stem in WINDOWS_RESERVED:
        return f"reserved Windows device name {component!r}"
    if component[-1] in ". ":
        return f"trailing dot or space in {component!r}"
    for char in component:
        if char in WINDOWS_INVALID or ord(char) < 32:
            return f"character {char!r} not allowed in {component!r}"
    return None


def collect_entries(client):
    """Return (entries, errors); entries are (zip_name, source_path) tuples."""
    entries, errors, seen = [], [], set()
    for dirpath, dirnames, filenames in os.walk(client, followlinks=False):
        directory = Path(dirpath)
        for name in dirnames:
            if (directory / name).is_symlink():
                errors.append(f"symlinked directory: {directory / name}")
        for name in filenames:
            source = directory / name
            relative = source.relative_to(client)
            zip_name = "/".join(relative.parts)
            if source.is_symlink():
                errors.append(f"symlink: {source}")
                continue
            lower = name.lower()
            if (lower in EXCLUDED_NAMES
                    or lower.endswith(tuple(EXCLUDED_SUFFIXES))
                    or lower.startswith(EXCLUDED_PREFIXES)):
                continue
            for component in relative.parts:
                problem = check_windows_name(component)
                if problem:
                    errors.append(f"{problem} in {source}")
                    break
            else:
                folded = zip_name.casefold()
                if folded in seen:
                    errors.append(f"case-insensitive name collision: {zip_name}")
                else:
                    seen.add(folded)
                    entries.append((zip_name, source))
    return sorted(entries), errors


def build(client, output):
    client = client.resolve()
    output = output.resolve()
    if not client.is_dir():
        raise SystemExit(f"error: client tree not found: {client}")
    if output.exists():
        raise SystemExit(f"error: refusing to overwrite existing output: {output}")
    try:
        output.relative_to(client)
    except ValueError:
        pass
    else:
        raise SystemExit("error: output must not be inside the client tree")

    # Explicit loading also supports tests importing this hyphenated script.
    import importlib.util
    spec = importlib.util.spec_from_file_location('publication_input', Path(__file__).with_name('publication_input.py'))
    validation = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validation)
    validation.validate_publication_input(client)

    entries, errors = collect_entries(client)
    for required in REQUIRED:
        if required not in {name for name, _ in entries}:
            errors.append(f"missing required file: {required}")
    if errors:
        raise SystemExit("error: client tree rejected:\n  " + "\n  ".join(errors))

    output.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(prefix=output.name + ".tmp-", dir=output.parent)
    sums = []
    try:
        with os.fdopen(fd, "wb") as raw:
            with zipfile.ZipFile(raw, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
                for zip_name, source in entries:
                    digest = hashlib.sha256()
                    info = zipfile.ZipInfo(zip_name)
                    info.compress_type = zipfile.ZIP_DEFLATED
                    with source.open("rb") as stream, \
                            archive.open(info, "w", force_zip64=True) as dest:
                        while chunk := stream.read(1024 * 1024):
                            digest.update(chunk)
                            dest.write(chunk)
                    sums.append((digest.hexdigest(), zip_name))
        with zipfile.ZipFile(tmp_name) as archive:
            bad = archive.testzip()
            if bad is not None:
                raise SystemExit(f"error: corrupt ZIP entry: {bad}")
        digest_file = zipfile.ZipInfo(SUMS_NAME)
        digest_file.compress_type = zipfile.ZIP_DEFLATED
        listing = "".join(f"{digest}  {name}\n" for digest, name in sorted(sums))
        with zipfile.ZipFile(tmp_name, "a") as archive:
            archive.writestr(digest_file, listing.encode("utf-8"))
        os.link(tmp_name, output)
        os.unlink(tmp_name)
    except BaseException:
        if os.path.exists(tmp_name):
            os.unlink(tmp_name)
        raise

    size = output.stat().st_size
    print(f"wrote {output} ({len(entries) + 1} entries, {size} bytes)")


def main(argv=None):
    args = parse_args(argv)
    build(args.client, args.output)


if __name__ == "__main__":
    main()
