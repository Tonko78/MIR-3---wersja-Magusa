"""Offline build-time validator. Accept only HTTPS DNS origins, not URLs with paths."""
import re
import sys


def valid_origin(value):
    match = re.fullmatch(r'https://([A-Za-z0-9.-]+)(?::([0-9]{1,5}))?', value)
    if not match:
        return False
    host, port = match.groups()
    if len(host) > 253 or any(not re.fullmatch(r'[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?', label) for label in host.split('.')):
        return False
    return port is None or 1 <= int(port) <= 65535


if __name__ == '__main__':
    if len(sys.argv) != 2 or not valid_origin(sys.argv[1]):
        print('Explicit HTTPS patch origin required: https://hostname[:port], without path, credentials, query or fragment.', file=sys.stderr)
        sys.exit(1)
