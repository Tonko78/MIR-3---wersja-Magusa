"""Offline publication guards; never open account databases or contact servers."""
from pathlib import Path
import hashlib
import unittest

ROOT = Path(__file__).resolve().parents[2]

class PublicationTests(unittest.TestCase):
    def test_launcher_requires_operator_patch_origin(self):
        source = (ROOT / 'Launcher/Config.cs').read_text(encoding='utf-8-sig')
        self.assertIn('Host { get; set; } = string.Empty;', source)

    def test_game_defaults_are_local_and_match_server(self):
        source = (ROOT / 'Client/Envir/Config.cs').read_text(encoding='utf-8-sig')
        self.assertIn('DefaultIPAddress = "127.0.0.1";', source)
        self.assertIn('DefaultPort = 7000;', source)
        self.assertNotIn('TestServer.mode', (ROOT / 'Client/Program.cs').read_text(encoding='utf-8-sig'))

    def test_only_audited_system_database_is_published(self):
        database_files = sorted(p.name for p in (ROOT / 'data').glob('*.db'))
        self.assertEqual(['System.db'], database_files)
        self.assertEqual('0bbab12e09b38ba3714b03d9bf4f483e6b92e071f82ac84d0311d62d32e7f2fe',
                         hashlib.sha256((ROOT / 'data/System.db').read_bytes()).hexdigest())

if __name__ == '__main__':
    unittest.main()
