"""Offline contracts for public deployment examples; never execute payloads."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OPS = ROOT / "ops"

class PublicDefaultsTests(unittest.TestCase):
    def test_examples_have_no_original_deployment_identity(self):
        forbidden = ("mir3." + "swoojeff.online", "swf." + "net.pl", "89.69." + "244.194", "C:\\Users\\" + "pawel", "=paolo")
        for path in OPS.rglob("*"):
            if path.is_file():
                text = path.read_text(encoding="utf-8")
                for value in forbidden:
                    self.assertNotIn(value, text, str(path))

    def test_packaging_defaults_are_explicit_and_inert(self):
        text = (OPS / "mir3-web/package-client.ps1").read_text(encoding="utf-8")
        self.assertIn("[string]$ServerHost = '127.0.0.1'", text)
        self.assertIn('"IPAddress=$ServerHost"', text)
        self.assertIn("if (-not $VerifyLaunch -or $SkipLaunch)", text)
        self.assertIn("SourcePath is required", text)

    def test_template_secret_fields_are_empty(self):
        text = (OPS / "mir3-web/mir3-web.env.example").read_text(encoding="utf-8")
        for line in text.splitlines():
            if any(key in line.partition('=')[0] for key in ('PASSWORD', 'HMAC_KEY')):
                self.assertEqual(line.partition('=')[2], '')

if __name__ == '__main__':
    unittest.main()
