import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

OPS = Path(__file__).resolve().parents[2] / 'ops' / 'mir3-web'

def load(name):
    spec = importlib.util.spec_from_file_location(name, OPS / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

class PublicationInputTests(unittest.TestCase):
    def invoke(self, builder, client, output):
        module = load(builder)
        if builder == 'build-client-zip':
            module.build(client, output)
        else:
            with patch.object(sys, 'argv', [builder, '--client', str(client), '--output', str(output)]):
                module.main()

    def fixture(self, root):
        client = root / 'client'
        (client / 'Data').mkdir(parents=True)
        for name in ('Zircon.exe', 'Zircon.dll', 'Data/System.db'):
            (client / name).write_bytes(b'fixture')
        (client / 'Zircon.ini').write_text('[Login]\nRememberedEMail=\nRememberedPassword=\nRememberDetails=False\n')
        return client

    def test_forbidden_names_rejected_before_any_content_read(self):
        for builder in ('build-client-zip', 'create-patch'):
            for name in ('Data/Users.db', '.env', '.env.production', 'secret.pem', 'id_ed25519', 'private.key', 'client.pfx', 'x.bak', 'x.backup', 'x.before-update', 'backup-ui-old/file.txt'):
                with self.subTest(builder=builder, name=name), tempfile.TemporaryDirectory() as tmp:
                    root = Path(tmp)
                    client = self.fixture(root)
                    forbidden = client / name
                    forbidden.parent.mkdir(parents=True, exist_ok=True)
                    forbidden.write_bytes(b'not to be read')
                    with patch.object(Path, 'open', side_effect=AssertionError('input content read')):
                        with self.assertRaisesRegex((ValueError, SystemExit), 'publication input'):
                            self.invoke(builder, client, root / 'output')
                    self.assertFalse((root / 'output').exists())

    def test_remembered_fields_rejected_and_sanitized_tree_succeeds(self):
        for builder in ('build-client-zip', 'create-patch'):
            for setting in ('RememberedEMail=someone@example.invalid', 'RememberedPassword=secret', 'RememberDetails=True', 'RememberedPassword=\nRememberedPassword=secret'):
                with self.subTest(builder=builder, setting=setting), tempfile.TemporaryDirectory() as tmp:
                    root = Path(tmp)
                    client = self.fixture(root)
                    (client / 'Zircon.ini').write_text('[Login]\n' + setting, encoding='utf-16')
                    with self.assertRaisesRegex((ValueError, SystemExit), 'publication input'):
                        self.invoke(builder, client, root / 'output')
                    self.assertFalse((root / 'output').exists())
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                client = self.fixture(root)
                self.invoke(builder, client, root / 'output')
                self.assertTrue((root / 'output').exists())

if __name__ == '__main__':
    unittest.main()
