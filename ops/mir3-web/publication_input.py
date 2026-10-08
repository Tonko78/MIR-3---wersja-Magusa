"""Read-only, fail-closed validation shared by publication builders."""
import os
from pathlib import Path
import re
import stat


def forbidden_name(name):
    name = name.casefold()
    return (name == 'users.db' or name.startswith('users.db.')
            or name == '.env' or name.startswith('.env.')
            or name.startswith(('backup-', 'backup_', 'id_rsa', 'id_dsa', 'id_ecdsa', 'id_ed25519'))
            or name in ('backup', 'backups', '.ssh')
            or '.before-' in name
            or name.endswith(('.pem', '.key', '.pfx', '.p12', '.ppk', '.bak', '.backup', '.old', '~')))


def validate_publication_input(client):
    """Reject names across the entire tree before opening any content.

    Never sanitizes source data; pass the reviewed sanitized staging tree.
    Remembered field names match package-client.ps1 Clear-RememberedCredentials.
    """
    client = Path(client).absolute()
    for ancestor in (client, *client.parents):
        if ancestor.is_symlink() or ancestor.is_junction():
            raise ValueError('publication input: reparse point')
    configs = []
    def walk_error(error):
        raise ValueError('publication input: unreadable directory') from error
    for directory, dirs, files in os.walk(client, followlinks=False, onerror=walk_error):
        for name in dirs + files:
            path = Path(directory) / name
            if forbidden_name(name):
                raise ValueError('publication input: forbidden private/backup filename')
            info = path.lstat()
            if (path.is_symlink() or path.is_junction()
                    or not (stat.S_ISREG(info.st_mode) or stat.S_ISDIR(info.st_mode))):
                raise ValueError('publication input: non-regular entry')
            if name.casefold() == 'zircon.ini' and name in files:
                configs.append(path)
    for path in configs:
        if path.stat().st_size > 20 * 1024 * 1024:
            raise ValueError('publication input: oversized Zircon.ini')
        try:
            data = path.read_bytes()
            text = data.decode('utf-16' if data.startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig')
        except (OSError, UnicodeError) as error:
            raise ValueError('publication input: unreadable Zircon.ini') from error
        if '\x00' in text:
            raise ValueError('publication input: invalid Zircon.ini encoding')
        for match in re.finditer(r'(?im)^[ \t]*(RememberedEMail|RememberedPassword|RememberDetails)[ \t]*=[ \t]*([^\r\n]*)', text):
            key, value = match.groups()
            value = re.sub(r'\s+[;#].*$', '', value).strip().strip('\"\'').strip()
            if (key.casefold() == 'rememberdetails' and value.casefold() != 'false') or (key.casefold() != 'rememberdetails' and value):
                raise ValueError('publication input: remembered credentials in Zircon.ini')
